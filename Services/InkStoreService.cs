using System.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Windows.Ink;
using Microsoft.Data.Sqlite;

namespace InkCanvasForPDF.Services;

/// <summary>单个 PDF 文档的墨迹持久化（SQLite WAL，存运行目录 Data\Ink\{文件名}.{XXH3-64}.db）。
/// 判别方式：对 PDF 文件内容算 XXH3-64（System.IO.Hashing，输出 64 位），列 Data\Ink\*.db 取
/// 文件名最后一个 '.' 段（校验码段）比对——纯文件名解析，不打开任何 db；文件名的名称段仅人类可读。
/// 线程模型：Save* 必须在 UI 线程调用（StrokeCollection 序列化有线程亲和），实际 SQL 写入经
/// 任务链在后台串行执行；Load* 由调用方在后台块调用；Flush 由 UI 线程在关闭前调用。</summary>
public sealed class InkStoreService : IDisposable
{
    private static readonly string InkDir = Path.Combine(AppContext.BaseDirectory, "Data", "Ink");

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);   // 串行化一切连接访问
    private readonly object _queueLock = new();
    private Task _tail = Task.CompletedTask;            // 写任务链尾（后台串行）
    private SqliteConnection? _conn;                    // 仅在持有 _gate 或单线程阶段访问
    private volatile bool _closed;                      // Flush 后置位：拒收新写入

    private InkStoreService(string path) => _path = path;

    /// <summary>PDF 文件内容的 XXH3-64 校验码（小写 hex，16 字符）。必须在非 UI 线程调用（大文件流式读）。</summary>
    public static string ComputePdfHash(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        var algo = new XxHash3();
        var buffer = new byte[1 << 16];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            algo.Append(buffer.AsSpan(0, read));
        return algo.GetCurrentHashAsUInt64().ToString("x16");   // 64 位输出 → 16 hex
    }

    /// <summary>按 PDF 文件定位（或新建）其墨迹库。必须在非 UI 线程调用（内部算文件内容哈希）。
    /// 匹配失败/库损坏时新建；失败返回 null（调用方降级为不持久化，不阻塞打开）。</summary>
    public static InkStoreService? TryOpen(string pdfPath)
    {
        try
        {
            Directory.CreateDirectory(InkDir);

            // 哈希计算：记录校验码与耗时（大文件流式读，耗时可见）
            var stopwatch = Stopwatch.StartNew();
            string hash = ComputePdfHash(pdfPath);
            stopwatch.Stop();
            ConsoleLog.Write("哈希验证",
                $"哈希计算: {Path.GetFileName(pdfPath)} → {hash} ({stopwatch.ElapsedMilliseconds}ms)");

            // 判别：列目录、切文件名末段比对（纯文件系统操作，不打开 db）
            string? found = null;
            int scanned = 0;
            foreach (var file in Directory.GetFiles(InkDir, "*.db"))
            {
                scanned++;
                var name = Path.GetFileNameWithoutExtension(file);
                int dot = name.LastIndexOf('.');
                if (dot >= 0 && string.Equals(name[(dot + 1)..], hash, StringComparison.OrdinalIgnoreCase))
                {
                    found = file;
                    break;
                }
            }

            bool created = false;
            if (found == null)
            {
                found = Path.Combine(InkDir, MakeDbName(pdfPath, hash));
                DeleteWalShm(found);   // 上次被手删的 db 可能残留 -wal/-shm，先清防误恢复
                created = true;
                ConsoleLog.Write("哈希验证", $"未命中，新建墨迹库: {Path.GetFileName(found)} (扫描={scanned}个)");
            }
            else
            {
                ConsoleLog.Write("哈希验证", $"命中已有墨迹库: {Path.GetFileName(found)} (扫描={scanned}个)");
            }

            var store = new InkStoreService(found);
            if (!store.TryConnect())
            {
                // 库损坏：改名保留现场（.bad）后新建空库，应用不中断
                App.Log("InkStore.Open", new Exception($"墨迹库打开失败，改名重建：{found}"));
                ConsoleLog.Write("哈希验证", $"墨迹库损坏，改名重建: {Path.GetFileName(found)}");
                TryRenameBad(found);
                DeleteWalShm(found);
                found = Path.Combine(InkDir, MakeDbName(pdfPath, hash));
                DeleteWalShm(found);
                store = new InkStoreService(found);
                if (!store.TryConnect())
                {
                    ConsoleLog.Write("哈希验证", "墨迹库无法打开，放弃（降级为不持久化）");
                    return null;
                }
                created = true;
            }

            if (created)
                store.WriteMeta(hash, pdfPath);
            return store;
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("哈希验证", $"异常: {ex}");
            App.Log("InkStore.TryOpen", ex);
            return null;
        }
    }

    // ────────────────────────── 读取（打开时后台块调用，仅取 byte[]，不建 Stroke）──────────────────────────

    /// <summary>读出全部 PDF 页墨迹（key=页码 0-based）。</summary>
    public Dictionary<int, byte[]> LoadPages() => LoadBlobTable("pdf_strokes", "page");

    /// <summary>读出全部画板槽位墨迹（key=槽位 1..99）。</summary>
    public Dictionary<int, byte[]> LoadBoardSlots() => LoadBlobTable("board_strokes", "slot");

    /// <summary>读出显示位置键值（pdf_zoom/pdf_offset/pdf_page/board_current/board_total）。</summary>
    public Dictionary<string, string> LoadViewState()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _gate.Wait();
        try
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT k, v FROM view_state";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = reader.GetString(1);
        }
        catch (Exception ex) { App.Log("InkStore.LoadViewState", ex); }
        finally { _gate.Release(); }
        return result;
    }

    private Dictionary<int, byte[]> LoadBlobTable(string table, string keyCol)
    {
        var result = new Dictionary<int, byte[]>();
        _gate.Wait();
        try
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = $"SELECT {keyCol}, data FROM {table}";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[reader.GetInt32(0)] = (byte[])reader[1];
        }
        catch (Exception ex) { App.Log($"InkStore.Load {table}", ex); }
        finally { _gate.Release(); }
        return result;
    }

    // ────────────────────────── 写入（Save* 须在 UI 线程调用）──────────────────────────

    /// <summary>写一页 PDF 墨迹（序列化在调用线程完成，SQL 后台执行）。</summary>
    public void SavePage(int page, StrokeCollection strokes)
    {
        if (strokes == null) return;
        var bytes = Serialize(strokes);
        Enqueue(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO pdf_strokes(page, data) VALUES($k, $d)";
            cmd.Parameters.AddWithValue("$k", page);
            cmd.Parameters.AddWithValue("$d", bytes);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>写一个画板槽位墨迹（1..99）。</summary>
    public void SaveBoardSlot(int slot, StrokeCollection strokes)
    {
        if (strokes == null) return;
        var bytes = Serialize(strokes);
        Enqueue(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO board_strokes(slot, data) VALUES($k, $d)";
            cmd.Parameters.AddWithValue("$k", slot);
            cmd.Parameters.AddWithValue("$d", bytes);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>写显示位置键值（整表替换；须在 Flush 之前调用，Flush 拒收新写入）。</summary>
    public void SaveViewState(IReadOnlyDictionary<string, string> values)
    {
        if (values == null) return;
        var pairs = values.ToArray();
        Enqueue(conn =>
        {
            using var tx = conn.BeginTransaction();
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM view_state";
                del.ExecuteNonQuery();
            }
            foreach (var (k, v) in pairs)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO view_state(k, v) VALUES($k, $v)";
                cmd.Parameters.AddWithValue("$k", k);
                cmd.Parameters.AddWithValue("$v", v);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        });
    }

    /// <summary>删除槽位号 &gt; total 的行（关闭 flush 时调用，任务链排队，顺序在 SaveViewState 之后）——防删页后残留陈旧槽位。</summary>
    public void TrimBoardSlots(int total) => Enqueue(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM board_strokes WHERE slot > $t";
        cmd.Parameters.AddWithValue("$t", total);
        cmd.ExecuteNonQuery();
    });

    /// <summary>画板中间加页：槽位 &gt;= from 的行整体 +1（主键递增会与现存行瞬时冲突，故 +1000 再 -999 两步，事务内）。</summary>
    public void ShiftBoardSlots(int from, int delta) => Enqueue(conn =>
    {
        if (delta == 0) return;
        using var tx = conn.BeginTransaction();
        if (delta > 0)
        {
            using (var up = conn.CreateCommand())
            {
                up.Transaction = tx;
                up.CommandText = "UPDATE board_strokes SET slot = slot + $o WHERE slot >= $f";
                up.Parameters.AddWithValue("$o", 1000);
                up.Parameters.AddWithValue("$f", from);
                up.ExecuteNonQuery();
            }
            using (var down = conn.CreateCommand())
            {
                down.Transaction = tx;
                down.CommandText = "UPDATE board_strokes SET slot = slot + $d WHERE slot >= $h";
                down.Parameters.AddWithValue("$d", delta - 1000);
                down.Parameters.AddWithValue("$h", from + 1000);
                down.ExecuteNonQuery();
            }
        }
        else
        {
            // 递减方向：主键升序扫描更新不会撞现存行，单语句即可
            using var down = conn.CreateCommand();
            down.Transaction = tx;
            down.CommandText = "UPDATE board_strokes SET slot = slot + $d WHERE slot >= $f";
            down.Parameters.AddWithValue("$d", delta);
            down.Parameters.AddWithValue("$f", from);
            down.ExecuteNonQuery();
        }
        tx.Commit();
    });

    /// <summary>画板删页：删除 removed 行并将 &gt;= shiftFrom 的行整体 -1（任务链排队，与前后写入保序）。</summary>
    public void DeleteBoardSlot(int removed, int shiftFrom) => Enqueue(conn =>
    {
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM board_strokes WHERE slot = $r";
            del.Parameters.AddWithValue("$r", removed);
            del.ExecuteNonQuery();
        }
        using (var down = conn.CreateCommand())
        {
            down.Transaction = tx;
            down.CommandText = "UPDATE board_strokes SET slot = slot - 1 WHERE slot >= $f";
            down.Parameters.AddWithValue("$f", shiftFrom);
            down.ExecuteNonQuery();
        }
        tx.Commit();
    });

    // ────────────────────────── 启动清理 ──────────────────────────

    /// <summary>启动时删除保留期外的墨迹库（按最后写入时间；days&lt;1 直接返回）。异常仅记日志，单文件失败跳过。</summary>
    public static void CleanExpired(int days)
    {
        if (days < 1)
            return;
        try
        {
            if (!Directory.Exists(InkDir))
            {
                ConsoleLog.Write("自动删除墨迹数据", "跳过: 墨迹目录不存在");
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(days);
            int scanned = 0, deleted = 0;
            foreach (var file in Directory.GetFiles(InkDir, "*.db"))
            {
                scanned++;
                if (File.GetLastWriteTimeUtc(file) >= cutoff)
                    continue;
                try
                {
                    File.Delete(file);
                    DeleteWalShm(file);   // 连带清 -wal/-shm 残留（防同名新建被误恢复）
                    deleted++;
                    ConsoleLog.Write("自动删除墨迹数据", $"删除: {Path.GetFileName(file)}");
                }
                catch { /* 单文件被占用/无权限：跳过 */ }
            }
            stopwatch.Stop();
            ConsoleLog.Write("自动删除墨迹数据",
                $"清理完成: 保留={days}天, 扫描={scanned}, 删除={deleted}, 耗时={stopwatch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("自动删除墨迹数据", $"清理异常: {ex}");
            App.Log("InkStore.CleanExpired", ex);
        }
    }

    // ────────────────────────── 生命周期 ──────────────────────────

    /// <summary>排空已入队写 + WAL checkpoint(TRUNCATE) + 关连接。之后拒收新写入。可重复调用。</summary>
    public void Flush()
    {
        if (_closed && _conn == null)
            return;
        _closed = true;   // UI 线程独占调用：拒新写入（Enqueue 与本方法同线程，无竞态）
        try
        {
            Task tail;
            lock (_queueLock)
                tail = _tail;
            tail.Wait();   // 等已入队的写完成（后台任务，不依赖 UI 线程，不会死锁）
            _gate.Wait();
            try
            {
                if (_conn is { State: ConnectionState.Open })
                {
                    using var cmd = _conn.CreateCommand();
                    cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    cmd.ExecuteNonQuery();
                    _conn.Close();
                }
                _conn?.Dispose();
                _conn = null;
            }
            finally { _gate.Release(); }
        }
        catch (Exception ex)
        {
            App.Log("InkStore.Flush", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;   // OnClosed 与 DocumentClosing 可能先后触发：防二次释放 _gate
        _disposed = true;
        Flush();
        _gate.Dispose();
    }

    private bool _disposed;

    // ────────────────────────── ISF 序列化（StrokeCollection ↔ bytes）──────────────────────────

    /// <summary>StrokeCollection → ISF 字节。<b>必须在 UI 线程调用</b>（Stroke 为 DependencyObject，有线程亲和）。</summary>
    public static byte[] Serialize(StrokeCollection strokes)
    {
        using var ms = new MemoryStream();
        strokes.Save(ms);
        return ms.ToArray();
    }

    /// <summary>ISF 字节 → StrokeCollection。<b>必须在 UI 线程调用</b>（恢复后要可擦可改，不冻结）。</summary>
    public static StrokeCollection Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        return new StrokeCollection(ms);
    }

    // ────────────────────────── 内部 ──────────────────────────

    /// <summary>生成库文件名：{安全文件名}.{hash}.db（名称段仅展示，判别只看 hash 段）。</summary>
    private static string MakeDbName(string pdfPath, string hash)
    {
        var name = Path.GetFileNameWithoutExtension(pdfPath);
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        if (name.Length > 100)
            name = name[..100].TrimEnd('.', ' ');
        if (name.Length == 0)
            name = "document";
        return name + "." + hash + ".db";
    }

    private static void DeleteWalShm(string dbPath)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            try { if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix); }
            catch { /* 残留清理失败不阻塞 */ }
        }
    }

    private static void TryRenameBad(string dbPath)
    {
        try { if (File.Exists(dbPath)) File.Move(dbPath, dbPath + ".bad", overwrite: true); }
        catch { /* 改名失败则后续打开结果仍由 TryConnect 决定 */ }
    }

    /// <summary>建连接 + PRAGMA(WAL/NORMAL) + 建表（幂等）。失败返回 false（文件损坏/被占用）。</summary>
    private bool TryConnect()
    {
        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = _path,
                Mode = SqliteOpenMode.ReadWriteCreate
            };
            _conn = new SqliteConnection(csb.ToString());
            _conn.Open();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT);
                    CREATE TABLE IF NOT EXISTS pdf_strokes (page INTEGER PRIMARY KEY, data BLOB);
                    CREATE TABLE IF NOT EXISTS board_strokes (slot INTEGER PRIMARY KEY, data BLOB);
                    CREATE TABLE IF NOT EXISTS view_state (k TEXT PRIMARY KEY, v TEXT);
                    """;
                cmd.ExecuteNonQuery();
            }
            return true;
        }
        catch (Exception ex)
        {
            App.Log("InkStore.TryConnect", ex);
            try { _conn?.Dispose(); } catch { /* 忽略 */ }
            _conn = null;
            return false;
        }
    }

    /// <summary>meta 写一次（建库时；仅供人排查，判别读取路径不使用）。</summary>
    private void WriteMeta(string hash, string pdfPath)
    {
        try
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO meta(k, v) VALUES('pdf_hash', $h),
                                                         ('pdf_name', $n),
                                                         ('created_utc', $t);
                """;
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$n", Path.GetFileName(pdfPath));
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            App.Log("InkStore.WriteMeta", ex);
        }
    }

    /// <summary>入队一次后台写（仅 UI 线程调用；写任务链保证与 Flush 的先后可见）。</summary>
    private void Enqueue(Action<SqliteConnection> op)
    {
        if (_closed || _conn == null)
            return;
        lock (_queueLock)
            _tail = _tail.ContinueWith(_ => WriteOne(op), TaskScheduler.Default).Unwrap();
    }

    private async Task WriteOne(Action<SqliteConnection> op)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;   // 服务已释放：该写入随关闭放弃
        }
        try
        {
            if (_conn is { State: ConnectionState.Open })
                op(_conn);
        }
        catch (Exception ex)
        {
            App.Log("InkStore.Write", ex);
        }
        finally
        {
            _gate.Release();
        }
    }
}
