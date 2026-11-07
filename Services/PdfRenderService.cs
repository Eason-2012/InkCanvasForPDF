using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InkCanvasForPDF.Services;

/// <summary>页面渲染尺寸。</summary>
public readonly record struct PageSize(int Width, int Height);

/// <summary>缩略图墨迹快照（请求入队时在 UI 线程取引用数组快照——微秒级，本次请求只读，后台安全绘制）。</summary>
public sealed record ThumbnailInk(Stroke[] Strokes, double CoordW, double CoordH);

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

    private readonly Dictionary<long, ImageSource> _baseCache = new();
    private readonly List<long> _baseLruOrder = new();
    private readonly Dictionary<long, ImageSource> _thumbCache = new();
    private readonly List<long> _thumbLruOrder = new();
    private readonly object _thumbCacheLock = new();          // 后台缩略图渲染与 UI 清理共用
    private readonly object _baseCacheLock = new();           // base 缓存改由后台线程渲染，读/写/LRU 全部加锁
    private readonly SemaphoreSlim _pageRenderSem = new(2, 2); // 页面渲染并发上限（限制 90MB 级位图的并发内存峰值）

    /// <summary>缩略图优先级队列：可见区（当前滑动区域）先于缓冲区，缓冲区按到可见区距离由近及远。
    /// 替代原信号量 FIFO——快速滚动时过期请求不再挡在新可见区域前面。</summary>
    private const long ThumbPriorityBufferBase = 1_000_000;   // 缓冲区优先级基数（大于任何可见区优先级）

    private readonly object _thumbQueueLock = new();
    private readonly PriorityQueue<(int Page, int Width, int Rotation, ThumbnailInk? Ink, int InkVer), long> _thumbQueue = new();
    private readonly Dictionary<long, TaskCompletionSource<ImageSource?>> _thumbInflight = new(); // 排队/渲染中的去重（键=(页,旋转)）
    private readonly SemaphoreSlim _thumbQueueSignal = new(0);
    private int _thumbFocusFirst = -1, _thumbFocusLast = -1;   // 当前可见区（滑动区域）
    private int _thumbWantStart = -1, _thumbWantEnd = -1;      // 期望范围（可见区±缓冲），超出丢弃
    private bool _thumbWorkersStarted;
    private int _cacheGeneration;   // 缓存代数：ClearCache/ClearBaseCache 时递增，丢弃在途渲染的过期写入

    /// <summary>墨迹提供者：按页返回墨迹快照（无墨迹返回 null 走纯 PDF 快路径）。由 MainViewModel 挂接。</summary>
    public Func<int, ThumbnailInk?>? ThumbnailInkProvider { get; set; }

    /// <summary>页→缩略图墨迹版本号：墨迹变更失效时 +1，旧快照渲染结果据此挡在缓存外（_thumbQueueLock 保护）。</summary>
    private readonly Dictionary<int, int> _thumbInkVersions = new();

    public int PageCount => _pageCount;

    public void OpenDocument(string path, string? password = null)
    {
        CloseDocument();
        // 确保 PDFium 已初始化（Docnet 懒初始化原生库）。
        try { _ = Docnet.Core.DocLib.Instance; } catch { }
        lock (PdfiumInterop.PdfiumLock)
        {
            _doc = PdfiumInterop.FPDF_LoadDocument(path, password);
            if (_doc == IntPtr.Zero)
            {
                if (PdfiumInterop.FPDF_GetLastError() == 4)   // FPDF_ERR_PASSWORD：需要密码或密码错误
                    throw new PasswordRequiredException();
                throw new InvalidOperationException("PDFium 无法打开文档（FPDF_LoadDocument 返回空句柄）");
            }
            _pageCount = PdfiumInterop.FPDF_GetPageCount(_doc);
        }
    }

    /// <summary>文档受密码保护（FPDF_ERR_PASSWORD=4）：需要密码或所输密码错误，弹密码框重试。</summary>
    internal sealed class PasswordRequiredException : Exception { }

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

    /// <summary>原始页面点尺寸（1/72 inch，未缩放）。供打印按真实物理尺寸排版。</summary>
    public (double Width, double Height) GetPageSizePoints(int pageIndex)
    {
        lock (PdfiumInterop.PdfiumLock)
        {
            var doc = _doc;
            if (doc == IntPtr.Zero)
                throw new InvalidOperationException("文档未打开");
            return PdfiumInterop.GetPageSize(doc, pageIndex);
        }
    }

    /// <summary>渲染某一页的 hi-res 位图（分辨率随缩放，最高 4096 宽）。不入缓存——
    /// 只存活于可见页±缓冲，滚出即释放（借鉴 KillerPDF：hi-res bitmaps never accumulate）。
    /// rotation = 顺时针旋转角（0/90/180/270），baseWidth/baseHeight 为旋转后的显示槽位尺寸。
    /// 同步渲染核心，仅供 <see cref="RenderPageAsync"/> 在后台线程调用。</summary>
    public ImageSource RenderPage(int pageIndex, int renderWidth, int baseWidth, int baseHeight, int rotation)
        => RenderAt(pageIndex, renderWidth, baseWidth, baseHeight, rotation);

    /// <summary>取某一页的 base 位图，LRU 缓存供回扫/缩小时快速显示。
    /// renderWidth = 实际栅格化像素宽（≤2048，不随缩放）；baseWidth/baseHeight = 显示 DIP 尺寸（= 旋转后槽位尺寸，
    /// 与 hi-res 路径同构，保证渲染框比例与烘焙 DPI 正确）。缓存键按 (页, 旋转) 区分。
    /// 同步渲染核心，仅供 <see cref="RenderPageBaseAsync"/> 在后台线程调用。</summary>
    public ImageSource RenderPageBase(int pageIndex, int renderWidth, int baseWidth, int baseHeight, int rotation)
    {
        long key = CacheKey(pageIndex, rotation);
        int generation = Volatile.Read(ref _cacheGeneration);
        lock (_baseCacheLock)
        {
            if (_baseCache.TryGetValue(key, out var cached))
            {
                Touch(_baseLruOrder, key);
                return cached;
            }
        }

        var image = RenderAt(pageIndex, renderWidth, baseWidth, baseHeight, rotation);
        lock (_baseCacheLock)
        {
            // 渲染期间缓存被清（换文档/视口宽度变化）→ 丢弃过期写入，避免旧尺寸/旧文档位图混入新缓存。
            if (generation == _cacheGeneration)
            {
                _baseCache[key] = image;
                Cache(_baseLruOrder, _baseCache, key, MaxCachedBasePages);
            }
        }
        return image;
    }

    /// <summary>后台渲染某一页的 hi-res 位图（并发上限 2；PdfiumLock 保证与缩略图互斥）。
    /// 位图在后台线程创建并冻结（BitmapSource.Create+Freeze），完成后由调用方 Dispatcher 回填 page.Image，
    /// UI 线程只做赋值——高负载书写时渲染不占用 UI 帧。</summary>
    public Task<ImageSource> RenderPageAsync(int pageIndex, int renderWidth, int baseWidth, int baseHeight, int rotation)
        => Task.Run(() =>
        {
            _pageRenderSem.Wait();
            try { return RenderPage(pageIndex, renderWidth, baseWidth, baseHeight, rotation); }
            finally { _pageRenderSem.Release(); }
        });

    /// <summary>后台取某一页的 base 位图（语义同 <see cref="RenderPageBase"/>，LRU 缓存线程安全）。</summary>
    public Task<ImageSource> RenderPageBaseAsync(int pageIndex, int renderWidth, int baseWidth, int baseHeight, int rotation)
        => Task.Run(() =>
        {
            _pageRenderSem.Wait();
            try { return RenderPageBase(pageIndex, renderWidth, baseWidth, baseHeight, rotation); }
            finally { _pageRenderSem.Release(); }
        });

    /// <summary>后台渲染某一页的缩略图位图（并发上限 2，由 worker 环保证；PdfiumLock 保证与页面渲染互斥）。
    /// 请求进入优先级队列：可见区（当前滑动区域）优先，缓冲区按到可见区距离由近及远；
    /// 同页同旋转角在途请求去重。rotation = 顺时针旋转角，thumbWidth = 旋转后的显示宽。
    /// 完成后由调用方 Dispatcher 更新 UI。缩略图走 PNG→冻结 BitmapFrame（KillerPDF 缩略图引擎），
    /// 后台线程安全。</summary>
    public Task<ImageSource?> RenderThumbnailAsync(int pageIndex, int thumbWidth, int rotation)
    {
        long key = CacheKey(pageIndex, rotation);
        lock (_thumbQueueLock)
        {
            if (_thumbInflight.TryGetValue(key, out var existing))
                return existing.Task;   // 去重：同页同旋转角不重复排队（命中不去快照）
            // 墨迹快照（UI 线程 ToArray 引用复制，微秒级）：在去重检查后采集，随请求入队
            var ink = ThumbnailInkProvider?.Invoke(pageIndex);
            int inkVer = _thumbInkVersions.GetValueOrDefault(pageIndex);
            var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _thumbInflight[key] = tcs;
            _thumbQueue.Enqueue((pageIndex, thumbWidth, rotation, ink, inkVer), ThumbPriority(pageIndex));
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
            var keep = new List<((int Page, int Width, int Rotation, ThumbnailInk? Ink, int InkVer), long)>();
            while (_thumbQueue.TryDequeue(out var item, out _))
            {
                if (item.Page >= _thumbWantStart && item.Page <= _thumbWantEnd)
                    keep.Add((item, ThumbPriority(item.Page)));
                else if (_thumbInflight.Remove(CacheKey(item.Page, item.Rotation), out var dropped))
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
                (int Page, int Width, int Rotation, ThumbnailInk? Ink, int InkVer) item = default;
                TaskCompletionSource<ImageSource?>? tcs = null;
                lock (_thumbQueueLock)
                {
                    while (_thumbQueue.TryDequeue(out var x, out _))
                    {
                        if (x.Page < _thumbWantStart || x.Page > _thumbWantEnd)   // 出队时再校验，过期项丢弃
                        {
                            if (_thumbInflight.Remove(CacheKey(x.Page, x.Rotation), out var dropped))
                                dropped.TrySetResult(null);
                            continue;
                        }
                        item = x;
                        _thumbInflight.TryGetValue(CacheKey(x.Page, x.Rotation), out tcs);
                        break;
                    }
                }
                if (tcs == null)
                    continue;   // 空唤醒：继续等下一次

                ImageSource? img = null;
                try { img = RenderThumbnailCore(item.Page, item.Width, item.Rotation, item.Ink, item.InkVer); }
                catch (Exception ex) { App.Log($"RenderThumbnail p{item.Page}", ex); }

                lock (_thumbQueueLock)
                {
                    if (_thumbInflight.TryGetValue(CacheKey(item.Page, item.Rotation), out var cur) && ReferenceEquals(cur, tcs))
                        _thumbInflight.Remove(CacheKey(item.Page, item.Rotation));
                }
                tcs.TrySetResult(img);
            }
            catch (Exception ex)
            {
                App.Log("ThumbWorker", ex);
            }
        }
    }

    private ImageSource? RenderThumbnailCore(int pageIndex, int thumbWidth, int rotation, ThumbnailInk? ink, int inkVer)
    {
        try
        {
            long key = CacheKey(pageIndex, rotation);
            lock (_thumbCacheLock)
            {
                if (_thumbCache.TryGetValue(key, out var cached))
                {
                    Touch(_thumbLruOrder, key);
                    return cached;
                }
            }

            // 同一 FPDF 句柄：取原生比例 → 按未旋转内容尺寸栅格化 → 像素旋转，全程持 PdfiumLock。
            // 旋转 90/270 时未旋转内容像素高 = thumbWidth，旋转后显示宽 = thumbWidth。
            byte[]? raw;
            int contentPxW, contentPxH;
            lock (PdfiumInterop.PdfiumLock)
            {
                var doc = _doc;
                if (doc == IntPtr.Zero)
                    return null;
                var (w, h) = PdfiumInterop.GetPageSize(doc, pageIndex);
                if (w <= 0 || h <= 0)
                    return null;
                bool swap = rotation is 90 or 270;
                contentPxW = Math.Max(1, swap
                    ? (int)Math.Round(thumbWidth * w / h)
                    : thumbWidth);
                contentPxH = Math.Max(1, swap
                    ? thumbWidth
                    : (int)Math.Round(thumbWidth * h / w));
                raw = PdfiumInterop.FPDF_RenderPage(doc, pageIndex, contentPxW, contentPxH);
            }
            if (raw == null)
                return null;

            ImageSource? bmp;
            if (ink == null)
            {
                // 无墨迹：保持原路径（像素旋转 → PNG 编码），零额外开销
                var (rotated, pxW, pxH) = BitmapHelpers.RotateBitmap(raw, contentPxW, contentPxH, rotation);
                bmp = EncodeThumbnail(rotated, pxW, pxH);
            }
            else
            {
                // 有墨迹：先编码未旋转位图，再走打印预览同款复合管线（内部缩放墨迹 + 旋转输出）
                var unrotated = EncodeThumbnail(raw, contentPxW, contentPxH);
                if (unrotated == null)
                    return null;
                bmp = PrintService.CompositePageWithInk(unrotated, ink.Strokes,
                    contentPxW, contentPxH, ink.CoordW, ink.CoordH, rotation);
            }
            if (bmp == null)
                return null;

            // 版本门槛 + 写缓存与 InvalidateThumbnailInk 同序嵌套（queueLock→cacheLock）：
            // 渲染期间该页墨迹已变更 → 结果过期，不入缓存、不回填。
            lock (_thumbQueueLock)
            {
                if (_thumbInkVersions.GetValueOrDefault(pageIndex) != inkVer)
                    return null;
                lock (_thumbCacheLock)
                {
                    _thumbCache[key] = bmp;
                    Cache(_thumbLruOrder, _thumbCache, key, MaxCachedThumbnails);
                }
            }
            return bmp;
        }
        catch (Exception ex)
        {
            App.Log($"RenderThumbnail p{pageIndex}", ex);
            return null;
        }
    }

    /// <summary>一页墨迹变更后失效其缩略图：版本号 +1、清该页 4 个旋转档缓存、
    /// 丢弃该页排队/在途旧请求（完成为 null，让刷新请求能立即重新入队）。</summary>
    public void InvalidateThumbnailInk(int pageIndex)
    {
        lock (_thumbQueueLock)
        {
            _thumbInkVersions[pageIndex] = _thumbInkVersions.GetValueOrDefault(pageIndex) + 1;

            // 剔除该页排队项（完成为 null）；其余项按当前焦点重排，开销同 UpdateThumbnailFocus
            var keep = new List<((int Page, int Width, int Rotation, ThumbnailInk? Ink, int InkVer), long)>();
            while (_thumbQueue.TryDequeue(out var item, out _))
            {
                if (item.Page == pageIndex)
                {
                    if (_thumbInflight.Remove(CacheKey(item.Page, item.Rotation), out var dropped))
                        dropped.TrySetResult(null);
                }
                else
                    keep.Add((item, ThumbPriority(item.Page)));
            }
            foreach (var (item, priority) in keep)
                _thumbQueue.Enqueue(item, priority);

            // 在途渲染：提前完成为 null（worker 随后的 TrySetResult 落在已完结 TCS 上无效果，版本门槛挡住缓存写入）
            for (int step = 0; step < 4; step++)
            {
                if (_thumbInflight.Remove(CacheKey(pageIndex, step * 90), out var inflight))
                    inflight.TrySetResult(null);
            }

            // 定向清该页 4 个旋转档缓存（与写缓存同序嵌套 queueLock→cacheLock，检查与清除不可拆开）
            lock (_thumbCacheLock)
            {
                for (int step = 0; step < 4; step++)
                {
                    long key = CacheKey(pageIndex, step * 90);
                    if (_thumbCache.Remove(key))
                        _thumbLruOrder.Remove(key);
                }
            }
        }
    }

    /// <summary>将一页栅格化并烘焙 DPI，使其自然 DIP 尺寸 = baseWidth×baseHeight（无缓存）。
    /// rotation = 顺时针旋转角：先按未旋转内容尺寸 0° 渲染，再在像素层旋转，方向与尺寸完全可控。</summary>
    private ImageSource RenderAt(int pageIndex, int renderWidth, int baseWidth, int baseHeight, int rotation)
    {
        bool swap = rotation is 90 or 270;
        double pxPerDip = (double)renderWidth / Math.Max(1, baseWidth);
        // 未旋转内容 DIP（槽位宽高在 90/270 时已交换）
        int contentDipW = swap ? baseHeight : baseWidth;
        int contentDipH = swap ? baseWidth : baseHeight;
        int contentPxW = Math.Max(1, (int)Math.Round(contentDipW * pxPerDip));
        int contentPxH = Math.Max(1, (int)Math.Round(contentDipH * pxPerDip));

        byte[]? raw;
        lock (PdfiumInterop.PdfiumLock)
        {
            var doc = _doc;
            if (doc == IntPtr.Zero)
                throw new InvalidOperationException("文档未打开");
            raw = PdfiumInterop.FPDF_RenderPage(doc, pageIndex, contentPxW, contentPxH);
        }
        if (raw == null)
            throw new InvalidOperationException($"PDFium 渲染失败 p{pageIndex}");

        // 像素级旋转：输出宽高 = 旋转后的槽位像素，方向由 rotation 决定（不依赖 PDFium rotate 语义）
        var (bytes, pxW, pxH) = BitmapHelpers.RotateBitmap(raw, contentPxW, contentPxH, rotation);

        // 烘焙 DPI：位图 (pxW×pxH) 像素固定显示在 (baseWidth×baseHeight) DIP 面积，
        // 缩放由 ScaleTransform 完成。BitmapSource.Create + Freeze：后台线程直接创建冻结位图
        // （与缩略图同款模式），不依赖 UI 线程，最小化/恢复也不会像 WriteableBitmap 那样因
        // BackBuffer surface 丢失而闪。
        var bmp = BitmapSource.Create(pxW, pxH,
            96.0 * pxW / Math.Max(1, baseWidth),
            96.0 * pxH / Math.Max(1, baseHeight),
            PixelFormats.Bgra32, null, bytes, pxW * 4);
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

    /// <summary>只清缩略图缓存（页面旋转导致缩略图内容尺寸变化时调用；页面 base 缓存保留，避免无谓重渲染）。</summary>
    public void ClearThumbnailCache()
    {
        Interlocked.Increment(ref _cacheGeneration);
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

    /// <summary>缓存键 = 页索引 × 4 + 旋转档位（0/90/180/270 → 0..3）：同一页不同旋转方向的位图互不覆盖。</summary>
    private static long CacheKey(int pageIndex, int rotation)
    {
        int step = ((rotation % 360) + 360) % 360 / 90;   // 0..3
        return (long)pageIndex * 4 + step;
    }

    /// <summary>近似 LRU：命中移到末尾；超限淘汰头部。</summary>
    private static void Touch(List<long> order, long key)
    {
        order.Remove(key);
        order.Add(key);
    }

    private static void Cache<TValue>(List<long> order, Dictionary<long, TValue> cache, long key, int max)
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
            _thumbInkVersions.Clear();
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
