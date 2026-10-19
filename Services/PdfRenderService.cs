using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InkCanvasForPDF.Services;

/// <summary>页面渲染尺寸。</summary>
public readonly record struct PageSize(int Width, int Height);

/// <summary>基于直接 PDFium 的位图渲染服务（引擎抄自 KillerPDF：PdfiumInterop 栅格化 +
/// 后台线程 BitmapSource.Create+Freeze 烘焙 DPI；缩略图走 PdfiumInterop 渲染 + PNG→冻结 BitmapFrame）。
/// 位图按"烘焙 DPI"固定到 DIP 显示尺寸，多余像素用于缩放清晰度。
/// 内存策略（借鉴 KillerPDF）：hi-res（随缩放）不入缓存，只存活于可见页；base（≤2048 宽）进 LRU 供回扫/缩小。
/// 页面位图与缩略图均为后台线程创建的冻结位图（RenderPageAsync / RenderThumbnailAsync），
/// UI 线程只做赋值——高负载书写时页面渲染不占用 UI 帧。</summary>
public sealed class PdfRenderService : IDisposable
{
    /// <summary>base 渲染结果缓存上限（页，base ≤2048 宽）。8 页 ≈ 190MB，覆盖 1~2 屏回扫复用，
    /// 兼顾滚动常驻内存峰值。</summary>
    private const int MaxCachedBasePages = 8;

    /// <summary>缩略图渲染结果缓存上限（页）。</summary>
    private const int MaxCachedThumbnails = 96;

    private IntPtr _doc;   // FPDF 文档句柄：OpenDocument 打开，服务生命周期内复用；所有访问持 PdfiumLock
    private int _pageCount;

    private readonly Dictionary<int, ImageSource> _baseCache = new();
    private readonly List<int> _baseLruOrder = new();
    private readonly Dictionary<int, ImageSource> _thumbCache = new();
    private readonly List<int> _thumbLruOrder = new();
    private readonly object _thumbCacheLock = new();          // 后台缩略图渲染与 UI 清理共用
    private readonly object _baseCacheLock = new();           // base 缓存改由后台线程渲染，读/写/LRU 全部加锁
    private readonly SemaphoreSlim _pageRenderSem = new(2, 2); // 页面渲染并发上限（限制 90MB 级位图的并发内存峰值）

    /// <summary>缩略图优先级队列：可见区（当前滑动区域）先于缓冲区，缓冲区按到可见区距离由近及远。
    /// 替代原信号量 FIFO——快速滚动时过期请求不再挡在新可见区域前面。</summary>
    private const long ThumbPriorityBufferBase = 1_000_000;   // 缓冲区优先级基数（大于任何可见区优先级）

    private readonly object _thumbQueueLock = new();
    private readonly PriorityQueue<(int Page, int Width), long> _thumbQueue = new();
    private readonly Dictionary<int, TaskCompletionSource<ImageSource?>> _thumbInflight = new(); // 排队/渲染中的去重
    private readonly SemaphoreSlim _thumbQueueSignal = new(0);
    private int _thumbFocusFirst = -1, _thumbFocusLast = -1;   // 当前可见区（滑动区域）
    private int _thumbWantStart = -1, _thumbWantEnd = -1;      // 期望范围（可见区±缓冲），超出丢弃
    private bool _thumbWorkersStarted;
    private int _cacheGeneration;   // 缓存代数：ClearCache/ClearBaseCache 时递增，丢弃在途渲染的过期写入

    public int PageCount => _pageCount;

    public void OpenDocument(string path)
    {
        CloseDocument();
        // 确保 PDFium 已初始化（Docnet 懒初始化原生库）。
        try { _ = Docnet.Core.DocLib.Instance; } catch { }
        lock (PdfiumInterop.PdfiumLock)
        {
            _doc = PdfiumInterop.FPDF_LoadDocument(path, null);
            if (_doc == IntPtr.Zero)
                throw new InvalidOperationException("PDFium 无法打开文档（FPDF_LoadDocument 返回空句柄）");
            _pageCount = PdfiumInterop.FPDF_GetPageCount(_doc);
        }
    }

    /// <summary>按目标宽度计算某页等比例缩放后的显示尺寸。</summary>
    public PageSize GetPageSize(int pageIndex, int targetWidth)
    {
        lock (PdfiumInterop.PdfiumLock)
        {
            var doc = _doc;
            if (doc == IntPtr.Zero)
                throw new InvalidOperationException("文档未打开");
            var (w, h) = PdfiumInterop.GetPageSize(doc, pageIndex);
            if (w <= 0 || h <= 0)
                return new PageSize(Math.Max(1, targetWidth), Math.Max(1, targetWidth));
            int renderWidth = Math.Max(1, targetWidth);
            int renderHeight = Math.Max(1, (int)Math.Round(h / w * renderWidth));
            return new PageSize(renderWidth, renderHeight);
        }
    }

    /// <summary>渲染某一页的 hi-res 位图（分辨率随缩放，最高 4096 宽）。不入缓存——
    /// 只存活于可见页±缓冲，滚出即释放（借鉴 KillerPDF：hi-res bitmaps never accumulate）。
    /// 同步渲染核心，仅供 <see cref="RenderPageAsync"/> 在后台线程调用。</summary>
    public ImageSource RenderPage(int pageIndex, int renderWidth, int baseWidth, int baseHeight)
        => RenderAt(pageIndex, renderWidth, baseWidth, baseHeight);

    /// <summary>取某一页的 base 位图（≤2048 宽，不随缩放），LRU 缓存供回扫/缩小时快速显示。
    /// 同步渲染核心，仅供 <see cref="RenderPageBaseAsync"/> 在后台线程调用。</summary>
    public ImageSource RenderPageBase(int pageIndex, int baseWidth, int baseHeight)
    {
        int generation = Volatile.Read(ref _cacheGeneration);
        lock (_baseCacheLock)
        {
            if (_baseCache.TryGetValue(pageIndex, out var cached))
            {
                Touch(_baseLruOrder, pageIndex);
                return cached;
            }
        }

        var image = RenderAt(pageIndex, baseWidth, baseWidth, baseHeight);
        lock (_baseCacheLock)
        {
            // 渲染期间缓存被清（换文档/视口宽度变化）→ 丢弃过期写入，避免旧尺寸/旧文档位图混入新缓存。
            if (generation == _cacheGeneration)
            {
                _baseCache[pageIndex] = image;
                Cache(_baseLruOrder, _baseCache, pageIndex, MaxCachedBasePages);
            }
        }
        return image;
    }

    /// <summary>后台渲染某一页的 hi-res 位图（并发上限 2；PdfiumLock 保证与缩略图互斥）。
    /// 位图在后台线程创建并冻结（BitmapSource.Create+Freeze），完成后由调用方 Dispatcher 回填 page.Image，
    /// UI 线程只做赋值——高负载书写时渲染不占用 UI 帧。</summary>
    public Task<ImageSource> RenderPageAsync(int pageIndex, int renderWidth, int baseWidth, int baseHeight)
        => Task.Run(() =>
        {
            _pageRenderSem.Wait();
            try { return RenderPage(pageIndex, renderWidth, baseWidth, baseHeight); }
            finally { _pageRenderSem.Release(); }
        });

    /// <summary>后台取某一页的 base 位图（语义同 <see cref="RenderPageBase"/>，LRU 缓存线程安全）。</summary>
    public Task<ImageSource> RenderPageBaseAsync(int pageIndex, int baseWidth, int baseHeight)
        => Task.Run(() =>
        {
            _pageRenderSem.Wait();
            try { return RenderPageBase(pageIndex, baseWidth, baseHeight); }
            finally { _pageRenderSem.Release(); }
        });

    /// <summary>后台渲染某一页的缩略图位图（并发上限 2，由 worker 环保证；PdfiumLock 保证与页面渲染互斥）。
    /// 请求进入优先级队列：可见区（当前滑动区域）优先，缓冲区按到可见区距离由近及远；
    /// 同页在途请求去重。完成后由调用方 Dispatcher 更新 UI。缩略图走 PNG→冻结 BitmapFrame（KillerPDF 缩略图引擎），
    /// 后台线程安全。</summary>
    public Task<ImageSource?> RenderThumbnailAsync(int pageIndex, int thumbWidth)
    {
        lock (_thumbQueueLock)
        {
            if (_thumbInflight.TryGetValue(pageIndex, out var existing))
                return existing.Task;   // 去重：同页不重复排队
            var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _thumbInflight[pageIndex] = tcs;
            _thumbQueue.Enqueue((pageIndex, thumbWidth), ThumbPriority(pageIndex));
            _thumbQueueSignal.Release();
            StartThumbWorkersIfNeeded();
            return tcs.Task;
        }
    }

    /// <summary>更新缩略图焦点：可见区（优先区内）页码升序；缓冲区按到可见区最近边缘的距离由近及远。</summary>
    private long ThumbPriority(int page)
        => page >= _thumbFocusFirst && page <= _thumbFocusLast
            ? page - _thumbFocusFirst
            : ThumbPriorityBufferBase + Math.Min(Math.Abs(page - _thumbFocusFirst), Math.Abs(page - _thumbFocusLast));

    /// <summary>每次滚动更新可见区与期望范围（可见区±缓冲）。排队项按新焦点重排，
    /// 滚出期望范围的过期请求立即丢弃（完成为 null，调用方复位 IsLoading，滚回时重新请求）。
    /// 必须在 LoadThumbnails 的缓冲区早退之前调用——缓冲区不变但可见区内滑动时也要刷新优先级。</summary>
    public void UpdateThumbnailFocus(int focusFirst, int focusLast, int wantStart, int wantEnd)
    {
        lock (_thumbQueueLock)
        {
            if (focusFirst == _thumbFocusFirst && focusLast == _thumbFocusLast
                && wantStart == _thumbWantStart && wantEnd == _thumbWantEnd)
                return;
            _thumbFocusFirst = focusFirst;
            _thumbFocusLast = focusLast;
            _thumbWantStart = wantStart;
            _thumbWantEnd = wantEnd;

            // 队列很小（可见区+缓冲 ≈ 十几项），全量出队→重排/丢弃，开销可忽略。
            var keep = new List<((int Page, int Width), long)>();
            while (_thumbQueue.TryDequeue(out (int Page, int Width) item, out _))
            {
                if (item.Page >= _thumbWantStart && item.Page <= _thumbWantEnd)
                    keep.Add((item, ThumbPriority(item.Page)));
                else if (_thumbInflight.Remove(item.Page, out var dropped))
                    dropped.TrySetResult(null);
            }
            foreach (var (item, priority) in keep)
                _thumbQueue.Enqueue(item, priority);
        }
    }

    /// <summary>惰性启动 2 个常驻 worker（并发上限与原 _thumbLoadSem(2,2) 一致），只启动一次。</summary>
    private void StartThumbWorkersIfNeeded()
    {
        if (_thumbWorkersStarted)
            return;
        _thumbWorkersStarted = true;
        for (int i = 0; i < 2; i++)
            _ = Task.Run(ThumbWorkerLoopAsync);
    }

    /// <summary>缩略图渲染 worker：等信号→取队列中最高优先级且仍在期望范围内的项→渲染→完成。
    /// 空唤醒（过期项已被丢弃）直接继续等待；恒有信号数 ≥ 排队项数，不存在丢失唤醒。</summary>
    private async Task ThumbWorkerLoopAsync()
    {
        while (true)
        {
            try
            {
                await _thumbQueueSignal.WaitAsync().ConfigureAwait(false);
                (int Page, int Width) item = default;
                TaskCompletionSource<ImageSource?>? tcs = null;
                lock (_thumbQueueLock)
                {
                    while (_thumbQueue.TryDequeue(out var x, out _))
                    {
                        if (x.Page < _thumbWantStart || x.Page > _thumbWantEnd)   // 出队时再校验，过期项丢弃
                        {
                            if (_thumbInflight.Remove(x.Page, out var dropped))
                                dropped.TrySetResult(null);
                            continue;
                        }
                        item = x;
                        _thumbInflight.TryGetValue(x.Page, out tcs);
                        break;
                    }
                }
                if (tcs == null)
                    continue;   // 空唤醒：继续等下一次

                ImageSource? img = null;
                try { img = RenderThumbnailCore(item.Page, item.Width); }
                catch (Exception ex) { App.Log($"RenderThumbnail p{item.Page}", ex); }

                lock (_thumbQueueLock)
                {
                    if (_thumbInflight.TryGetValue(item.Page, out var cur) && ReferenceEquals(cur, tcs))
                        _thumbInflight.Remove(item.Page);
                }
                tcs.TrySetResult(img);
            }
            catch (Exception ex)
            {
                App.Log("ThumbWorker", ex);
            }
        }
    }

    private ImageSource? RenderThumbnailCore(int pageIndex, int thumbWidth)
    {
        try
        {
            lock (_thumbCacheLock)
            {
                if (_thumbCache.TryGetValue(pageIndex, out var cached))
                {
                    Touch(_thumbLruOrder, pageIndex);
                    return cached;
                }
            }

            // 同一 FPDF 句柄：取原生比例 → 等比渲染尺寸 → 栅格化，全程持 PdfiumLock。
            byte[]? raw;
            int rw, rh;
            lock (PdfiumInterop.PdfiumLock)
            {
                var doc = _doc;
                if (doc == IntPtr.Zero)
                    return null;
                var (w, h) = PdfiumInterop.GetPageSize(doc, pageIndex);
                if (w <= 0 || h <= 0)
                    return null;
                rw = Math.Max(1, thumbWidth);
                rh = Math.Max(1, (int)Math.Round(h / w * rw));
                raw = PdfiumInterop.FPDF_RenderPage(doc, pageIndex, rw, rh);
            }
            if (raw == null)
                return null;

            var bmp = EncodeThumbnail(raw, rw, rh);
            if (bmp == null)
                return null;

            lock (_thumbCacheLock)
            {
                _thumbCache[pageIndex] = bmp;
                Cache(_thumbLruOrder, _thumbCache, pageIndex, MaxCachedThumbnails);
            }
            return bmp;
        }
        catch (Exception ex)
        {
            App.Log($"RenderThumbnail p{pageIndex}", ex);
            return null;
        }
    }

    /// <summary>将一页按渲染尺寸栅格化，烘焙 DPI 使其自然 DIP 尺寸 = baseWidth×baseHeight（无缓存）。</summary>
    private ImageSource RenderAt(int pageIndex, int renderWidth, int baseWidth, int baseHeight)
    {
        int renderHeight = Math.Max(1, (int)Math.Round((double)baseHeight / baseWidth * renderWidth));
        byte[]? raw;
        lock (PdfiumInterop.PdfiumLock)
        {
            var doc = _doc;
            if (doc == IntPtr.Zero)
                throw new InvalidOperationException("文档未打开");
            raw = PdfiumInterop.FPDF_RenderPage(doc, pageIndex, renderWidth, renderHeight);
        }
        if (raw == null)
            throw new InvalidOperationException($"PDFium 渲染失败 p{pageIndex}");

        // 烘焙 DPI：位图 (renderWidth×renderHeight) 像素固定显示在 (baseWidth×baseHeight) DIP 面积，
        // 缩放由 ScaleTransform 完成。BitmapSource.Create + Freeze：后台线程直接创建冻结位图
        // （与缩略图同款模式），不依赖 UI 线程，最小化/恢复也不会像 WriteableBitmap 那样因
        // BackBuffer surface 丢失而闪。
        var bmp = BitmapSource.Create(renderWidth, renderHeight,
            96.0 * renderWidth / Math.Max(1, baseWidth),
            96.0 * renderHeight / Math.Max(1, baseHeight),
            PixelFormats.Bgra32, null, raw, renderWidth * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>BGRA 像素 → PNG → 冻结 BitmapFrame（抄自 KillerPDF PageThumbnailVm.EncodeToBitmapSource）。
    /// GDI+ Format32bppArgb 内存序即 BGRA，与 pdfium 输出一致。</summary>
    private static BitmapSource? EncodeThumbnail(byte[] bgra, int width, int height)
    {
        var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var bmp = new System.Drawing.Bitmap(
                width, height, width * 4,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb,
                pin.AddrOfPinnedObject());
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            var src = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            src.Freeze();
            return src;
        }
        catch (Exception ex)
        {
            App.Log("EncodeThumbnail", ex);
            return null;
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>清空全部渲染缓存（重新打开文档时调用）。</summary>
    public void ClearCache()
    {
        Interlocked.Increment(ref _cacheGeneration);
        lock (_baseCacheLock)
        {
            _baseCache.Clear();
            _baseLruOrder.Clear();
        }
        lock (_thumbCacheLock)
        {
            _thumbCache.Clear();
            _thumbLruOrder.Clear();
        }
    }

    /// <summary>只清页面 base 缓存（视口宽度变化时页面位图尺寸失效），缩略图缓存保留——缩略图尺寸固定，无需重建。</summary>
    public void ClearBaseCache()
    {
        Interlocked.Increment(ref _cacheGeneration);
        lock (_baseCacheLock)
        {
            _baseCache.Clear();
            _baseLruOrder.Clear();
        }
    }

    /// <summary>近似 LRU：命中移到末尾；超限淘汰头部。</summary>
    private static void Touch(List<int> order, int key)
    {
        order.Remove(key);
        order.Add(key);
    }

    private static void Cache<TValue>(List<int> order, Dictionary<int, TValue> cache, int key, int max)
    {
        order.Add(key);
        if (order.Count > max)
            cache.Remove(order[0]);
        order.RemoveAt(0);
    }

    public void CloseDocument()
    {
        ClearCache();
        // 丢弃排队中的缩略图请求（换文档后旧请求不得混入），worker 无需停止：空队列时挂起。
        lock (_thumbQueueLock)
        {
            _thumbQueue.Clear();
            foreach (var tcs in _thumbInflight.Values)
                tcs.TrySetResult(null);
            _thumbInflight.Clear();
            _thumbFocusFirst = _thumbFocusLast = -1;
            _thumbWantStart = _thumbWantEnd = -1;
        }
        lock (PdfiumInterop.PdfiumLock)
        {
            var doc = _doc;
            _doc = IntPtr.Zero;
            if (doc != IntPtr.Zero)
                PdfiumInterop.FPDF_CloseDocument(doc);
        }
        _pageCount = 0;
    }

    public void Dispose() => CloseDocument();
}
