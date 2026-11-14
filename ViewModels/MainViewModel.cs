using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using InkCanvasForPDF.Models;
using InkCanvasForPDF.Services;
using Microsoft.Win32;

namespace InkCanvasForPDF.ViewModels;

/// <summary>文档打开时从墨迹库读出的待恢复数据（字节在后台块读，Stroke 反序列化由订阅方在 UI 线程做）。
/// Store 为 null 表示无库（新文档）：PageInks/BoardSlots/ViewState 为空，订阅方据此把画板重置为 1/1 空态。</summary>
public sealed record InkRestoreData(InkStoreService? Store, Dictionary<int, byte[]> PageInks,
    Dictionary<int, byte[]> BoardSlots, Dictionary<string, string> ViewState);

/// <summary>主视图模型：打开 PDF，按视口宽度渲染可见页（懒加载）。</summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private const int PageBufferSize = 2;
    private const int PageVerticalMargin = 12;   // 页间距（对齐 KillerPDF：槽位底边 Margin 12px）
    private const int ThumbnailWidth = 140;
    private const int ThumbnailMargin = 10;
    private const int ThumbnailBufferSize = 4;
    private const int MaxRenderWidth = 4096;
    private const int MaxBaseWidth = 2048;   // KillerPDF 连续 base 预算
    private const int ScrollDebounceMs = 50;   // 滚动停止：只补 base，不清晰
    private const int SharpenDebounceMs = 200;  // 缩放停止后清晰延迟
    private const int ReleaseIdleMs = 1000;    // 无缩放/滚动空闲多久释放内存
    private const int OpenReadyPollMs = 100;      // 首屏就绪轮询间隔
    private const int OpenReadyTimeoutMs = 15000; // 就绪兜底时限：超时强制撤加载页（渲染失败不卡死）
    private const double ScrollBarAllowance = 18;  // 页面列宽预留的纵向滚动条宽度（DIP）
    private const int MaterializeBufferPages = 1;  // 墨迹画布挂载缓冲：可见范围上下各多挂 1 页（防书写中滚出被断笔）
    /// <summary>缩放倍率：每档固定 ×1.1，保证各倍率下滚轮手感一致。</summary>
    public const double ZoomStepFactor = 1.1;

    private readonly PdfRenderService _pdf = new();

    private int _viewportWidth = 800;
    private int _targetPageWidth = 760;
    private int _loadedStartIndex = -1;
    private int _loadedEndIndex = -1;
    /// <summary>在途页面渲染（键=页索引）：单飞去重 + 可取消 + 在途字节记账（仅 UI 线程访问）。</summary>
    private readonly Dictionary<int, PendingPageRender> _pendingPageRenders = new();
    private long _inflightRenderBytes;                // 在途渲染的估算像素字节合计
    /// <summary>在途渲染字节上限：超过时取消离可见区最远的一页，避免滚动时多张 ≈20MB 位图同时驻留。</summary>
    private const long MaxInflightRenderBytes = 200L << 20;

    /// <summary>一次在途页面渲染：取消源 + 估算像素字节（完成回填时按此扣减）。</summary>
    private sealed class PendingPageRender(CancellationTokenSource cts, long estimatedBytes)
    {
        internal CancellationTokenSource Cts { get; } = cts;
        internal long EstimatedBytes { get; } = estimatedBytes;
    }
    private int _lastRequestFirst = -1;   // 最近一次 LoadPageImages 的可见范围与 needHi
    private int _lastRequestLast = -1;    // （完成回调据此重算该页当前期望宽度）
    private bool _lastRequestNeedHi;
    private int _thumbLoadedStartIndex = -1;
    private int _thumbLoadedEndIndex = -1;
    private int _firstVisiblePageIndex;
    private double _lastVerticalOffset;
    private double _lastViewportHeight;
    private double _dpiScale = 1.0;
    private DispatcherTimer? _scrollDebounceTimer;   // 滚动停止：只补 base，不清晰
    private DispatcherTimer? _sharpenTimer;          // 缩放停止：清晰当前页一次
    private DispatcherTimer? _releaseTimer;          // 空闲 1s：hi→base 释放内存
    private long _lastLohCompactTickMs;              // 空闲 LOH 压缩节流（距上次 ≥10s 才再压缩）
    private DispatcherTimer? _openReadyTimer;        // 首屏就绪轮询：视口可见页位图齐了才撤加载页
    private int _openReadyElapsedMs;                 // 已轮询时长（超时兜底）
    private bool _isRebuildingPages;                 // ReloadPages 进行中：Pages 与 _pageTopDips 处于不一致中间态，抑制滚动驱动处理
    private bool _restoringView;                     // 打开文档恢复位置期间：屏蔽视口宽度变化引起的滚动补偿
    private double _widthFit = 1.0;                  // 宽度适配系数：页面槽位固定，宽度变化只更新此系数（整层等比缩放）
    private readonly Dictionary<int, int> _pageRotations = new();   // 页索引 → 旋转角（0/90/180/270，仅非 0 记录；旋转只作用于当前页）
    private bool _zoomActive;                        // 缩放操作进行中（缩放引发的 ScrollChanged 不取消清晰）
    private bool _isZooming;                         // 缩放交互进行中：页面位图用 Linear 插值保证帧率
    private int _lastRangeFirst = -1;                // 上次可见范围（检测"有页退出视口"）
    private int _lastRangeLast = -1;
    private double[] _pageTopDips = Array.Empty<double>();   // 页面顶部 DIP 前缀和（未缩放，供二分找可见页）
    private double[] _thumbTopDips = Array.Empty<double>();  // 缩略图顶部 DIP 前缀和（供二分找可见缩略图）
    private double _thumbItemChrome = ThumbnailMargin;       // 每项图片外的布局高度（ExtentHeight 实测；ThumbnailMargin 作布局未就绪时的回退）
    private long _lastReloadPagesMs;                         // 最近一次 ReloadPages 的集合构建耗时（打开耗时日志用）
    private int _highlightedThumbIndex = -1;                 // 当前高亮的缩略图索引（增量更新用）
    private double _maxPageExtent = 800;                     // 文档内最大页槽位宽（Base DIP），决定页面列宽
    private int _materializedStart = -1;                     // 已挂载墨迹画布的页区间（含缓冲）
    private int _materializedEnd = -1;

    public List<PageModel> Pages { get; private set; } = new();
    public List<ThumbnailModel> Thumbnails { get; private set; } = new();

    /// <summary>页面集合整体替换（打开/关闭文档）：宿主据此清理旧画布与笔画追踪。</summary>
    public event Action? PagesReset;

    /// <summary>墨迹批注工具状态 + 全局撤销栈（GetPageStrokes 由构造器注入）。</summary>
    public InkToolViewModel InkTool { get; } = new();

    public ICommand OpenPdfCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand GoToPreviousPageCommand { get; }
    public ICommand GoToNextPageCommand { get; }
    public ICommand GoHomeCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand CloseSettingsCommand { get; }

    /// <summary>应用设置（加载自运行目录 Data\Settings.json，由设置面板绑定修改）。</summary>
    public AppSettings Settings { get; }

    private bool _hasPdf;

    public bool HasPdf
    {
        get => _hasPdf;
        private set
        {
            if (SetField(ref _hasPdf, value))
            {
                OnPropertyChanged(nameof(IsToolbarVisible));
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    /// <summary>顶部工具栏是否可见（有 PDF、不在打开加载中；设置页打开时不硬切隐藏，由不透明覆盖层
    /// 盖住工具栏，与画板飞入一致；加载页为纯云母，白色顶栏在此期间必须隐藏）。</summary>
    public bool IsToolbarVisible => HasPdf && !IsOpeningPdf;

    private string _pdfName = string.Empty;

    /// <summary>当前 PDF 文件名（不含扩展名），用于窗口标题；无文档时为空。</summary>
    public string PdfName
    {
        get => _pdfName;
        private set
        {
            if (SetField(ref _pdfName, value))
                OnPropertyChanged(nameof(WindowTitle));
        }
    }

    /// <summary>窗口标题（按状态动态，当前页面在前、应用名在后）：正在打开 &gt; 应用设置 &gt; PDF 名 &gt; 欢迎。</summary>
    public string WindowTitle =>
        IsOpeningPdf ? "正在打开 - InkCanvasForPDF"
        : IsSettingsVisible ? "应用设置 - InkCanvasForPDF"
        : HasPdf ? $"{PdfName} - InkCanvasForPDF"
        : "欢迎 - InkCanvasForPDF";

    private bool _isOpenOpeningPdf;

    /// <summary>打开 PDF 进行中（全屏加载页显隐）。</summary>
    public bool IsOpeningPdf
    {
        get => _isOpenOpeningPdf;
        set
        {
            if (SetField(ref _isOpenOpeningPdf, value))
            {
                OnPropertyChanged(nameof(WindowTitle));
                OnPropertyChanged(nameof(IsToolbarVisible));
            }
        }
    }

    public int TotalPages => Pages.Count;

    /// <summary>当前可见首页（1-based），滚动/跳页后更新。</summary>
    public int CurrentPage => _firstVisiblePageIndex + 1;

    /// <summary>页码输入框文本（常驻输入框，双向绑定）。</summary>
    private string _pageNumberText = "1";

    public string PageNumberText
    {
        get => _pageNumberText;
        set => SetField(ref _pageNumberText, value);
    }

    private bool _isThumbnailPanelVisible;

    /// <summary>缩略图侧栏是否展开。</summary>
    public bool IsThumbnailPanelVisible
    {
        get => _isThumbnailPanelVisible;
        set => SetField(ref _isThumbnailPanelVisible, value);
    }

    private bool _isSettingsVisible;

    /// <summary>设置覆盖层是否显示（切换时通知窗口标题）。</summary>
    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        set
        {
            if (SetField(ref _isSettingsVisible, value))
            {
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    /// <summary>跳页滚动回调（1-based），由 MainWindow 注入实际滚动实现。</summary>
    public Action<int>? RequestScrollToPage { get; set; }

    /// <summary>滚动到指定垂直偏移（布局坐标），由 MainWindow 注入实际滚动实现（宽度变化保持页内位置用）。</summary>
    public Action<double>? RequestScrollToOffset { get; set; }

    /// <summary>恢复文档显示滚动位置（offset、比例、页码回退），由 MainWindow 注入（Loaded 时机调用，内部 UpdateLayout）。</summary>
    public Action<double, double, int>? RequestRestoreScroll { get; set; }

    /// <summary>恢复文档水平滚动位置（0..1 比例；负值 = 无记忆，由宿主居中），由 MainWindow 注入。</summary>
    public Action<double>? RequestRestoreHorizontal { get; set; }

    /// <summary>请求宿主立即用真实滚动容器尺寸同步可见页（打开文档后首屏兜底）。</summary>
    public Action? RequestVisiblePagesSync { get; set; }

    /// <summary>浮动通知回调（消息 + 严重级别），由 MainWindow 注入 ShowFloatingTip。</summary>
    public Action<string, iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity>? ShowTip { get; set; }

    // ══ 墨迹持久化（D8：VM 只发事件，flush/恢复实现在 MainWindow）══
    /// <summary>当前文档的墨迹库（仅 UI 线程读写；打开后台块返回后赋值，关闭时随 DocumentClosing 置 null）。</summary>
    public InkStoreService? InkStore { get; set; }

    /// <summary>文档即将关闭/切换（GoHome 最开头与 OpenPdf 选定新文件后触发）：订阅方 flush 旧库。</summary>
    public event Action? DocumentClosing;

    /// <summary>文档打开完成（ReloadPages 之后触发）：订阅方恢复 PDF 页墨迹与画板（分帧执行，await 完成 = 墨迹全部就位）。</summary>
    public event Func<InkRestoreData, Task>? DocumentOpened;

    /// <summary>立即停止墨迹持久化（关闭"自动保存"开关时调用）：flush 当前库并置空（完全停用，E1）。</summary>
    public void StopInkPersistence()
    {
        DocumentClosing?.Invoke();
        InkStore = null;
    }

    private double _zoomLevel = 1.0;

    /// <summary>页面缩放级别（0.05x ~ 5x）。缩放过程中仅由 ScaleTransform 即时缩放（禁止清晰渲染），
    /// 停止 50ms 后清晰当前页一次，1s 无操作后释放 hi 内存。</summary>
    public double ZoomLevel
    {
        get => _zoomLevel;
        set
        {
            if (SetField(ref _zoomLevel, Math.Clamp(value, 0.05, 5.0)))
            {
                OnPropertyChanged(nameof(DisplayScale));
                OnPropertyChanged(nameof(PageColumnWidthBase));
                ScheduleSharpen();
            }
        }
    }

    /// <summary>缩放交互进行中：页面位图用低质量插值（Linear）保证帧率，停止后恢复 HighQuality。</summary>
    public bool IsZooming
    {
        get => _isZooming;
        set => SetField(ref _isZooming, value);
    }

    /// <summary>实际显示比例 = 用户缩放 × 宽度适配（KillerPDF 槽位固定 + 整层等比缩放）。
    /// 页面尺寸固定不重建，宽度变化只改 _widthFit → 无占位闪动。</summary>
    public double DisplayScale => _zoomLevel * _widthFit;

    /// <summary>页面列宽（Base DIP，未缩放）：取"视口可用宽反算值"与"最大页槽位宽"的较大者。
    /// 页槽按此宽度撑满，页面在槽内水平居中；整层 LayoutTransform 缩放后即为可视宽。</summary>
    public double PageColumnWidthBase =>
        Math.Max((_viewportWidth - ScrollBarAllowance) / Math.Max(0.01, DisplayScale), _maxPageExtent);

    /// <summary>高 DPI 缩放系数（由 MainWindow 注入），用于决定渲染分辨率。</summary>
    public double DpiScale
    {
        get => _dpiScale;
        set => SetField(ref _dpiScale, value);
    }

    /// <summary>渲染宽度：随 DisplayScale 与 DPI 增大（缩小不降级），上限 MaxRenderWidth。</summary>
    private int GetRenderWidth(int pageWidth) =>
        Math.Min(MaxRenderWidth, (int)Math.Round(pageWidth * _dpiScale * Math.Max(1.0, DisplayScale)));

    /// <summary>base 渲染宽度：不随缩放，上限 MaxBaseWidth（供回扫/缩小时快速显示并进 LRU）。</summary>
    private int GetBaseWidth(int pageWidth) =>
        Math.Min(MaxBaseWidth, (int)Math.Round(pageWidth * _dpiScale));

    /// <summary>按目标像素宽把页面点尺寸换算为显示尺寸（取整规则与 PdfRenderService.GetPageSize 一致）。
    /// 打开文档时同一页只向 PDFium 取一次点尺寸，页面槽位与缩略图槽位都由本方法派生。</summary>
    private static PageSize ScaleToWidth(double pointWidth, double pointHeight, int targetWidth)
    {
        int renderWidth = Math.Max(1, targetWidth);
        if (pointWidth <= 0 || pointHeight <= 0)
            return new PageSize(renderWidth, renderWidth);
        return new PageSize(renderWidth, Math.Max(1, (int)Math.Round(pointHeight / pointWidth * renderWidth)));
    }

    /// <summary>滚动停止去抖：只补缺失页的模糊 base，不渲染清晰位图（滚动不清晰）。</summary>
    private void ScheduleScrollRender(bool pageExited)
    {
        // 缩放引发的变化：滚动 debounce 与 1s 释放交给缩放路径（sharpen/release），此处不重复处理，
        // 避免缩放每 tick 重启计时器并叠加 base 补渲染。
        if (_zoomActive)
            return;
        // 真实用户滚动会取消待执行的清晰（缩放停止后的 sharpen 已由 OnSharpenTick 消费）。
        _sharpenTimer?.Stop();
        // 有页面退出视口才计 1s 释放；否则不重启（避免无谓释放/计时）。
        if (pageExited)
            RestartReleaseTimer();
        _scrollDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ScrollDebounceMs) };
        _scrollDebounceTimer.Stop();
        _scrollDebounceTimer.Tick -= OnScrollDebounceTick;
        _scrollDebounceTimer.Tick += OnScrollDebounceTick;
        _scrollDebounceTimer.Start();
    }

    private void OnScrollDebounceTick(object? sender, EventArgs e)
    {
        _scrollDebounceTimer!.Stop();
        _scrollDebounceTimer.Tick -= OnScrollDebounceTick;
        if (!HasPdf || Pages.Count == 0 || _lastViewportHeight <= 0)
            return;
        var (first, last) = GetVisiblePageRange(_lastVerticalOffset, _lastViewportHeight);
        // 滚动停止后：补缺失 base；视口内页面（含部分可见）处于放大状态且没有清晰位图的直接补 hi
        // （不再等成为"当前页"）；未放大时 needHi=false 自动走 base，零额外开销。
        LoadPageImages(first, last, needHiInViewport: true);
        RestartReleaseTimer();   // 清晰后重新计 1s 空闲，避免立即释放
    }

    /// <summary>缩放停止后清晰当前页一次（缩放过程中只 ScaleTransform，禁止清晰渲染）。</summary>
    private void ScheduleSharpen()
    {
        IsZooming = true;        // 进入缩放交互：页面位图切 Linear 插值，保证每帧重采样流畅
        RestartReleaseTimer();   // 每次缩放重置 1s 空闲计时
        _zoomActive = true;      // 缩放进行中：由缩放引发的 ScrollChanged 不取消清晰
        _sharpenTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SharpenDebounceMs) };
        _sharpenTimer.Stop();
        _sharpenTimer.Tick -= OnSharpenTick;
        _sharpenTimer.Tick += OnSharpenTick;
        _sharpenTimer.Start();
    }

    private void OnSharpenTick(object? sender, EventArgs e)
    {
        _sharpenTimer!.Stop();
        _sharpenTimer.Tick -= OnSharpenTick;
        _zoomActive = false;   // 缩放已停止，后续滚动视为真实滚动
        IsZooming = false;     // 缩放停止：恢复 HighQuality，之后显示新 hi 位图即为清晰
        if (!HasPdf || Pages.Count == 0 || _lastViewportHeight <= 0)
            return;
        var (first, last) = GetVisiblePageRange(_lastVerticalOffset, _lastViewportHeight);
        LoadPageImages(first, last, needHiInViewport: true);   // 视口内页面直接清晰
        RestartReleaseTimer();                                // 清晰完成后再计 1s 空闲
    }

    /// <summary>重启 1s 空闲释放计时：无缩放/滚动后把 hi 换回 base，释放大图内存。</summary>
    private void RestartReleaseTimer()
    {
        _releaseTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ReleaseIdleMs) };
        _releaseTimer.Stop();
        _releaseTimer.Tick -= OnReleaseTick;
        _releaseTimer.Tick += OnReleaseTick;
        _releaseTimer.Start();
    }

    private void OnReleaseTick(object? sender, EventArgs e)
    {
        _releaseTimer!.Stop();
        _releaseTimer.Tick -= OnReleaseTick;
        // 低优先级：等 UI 空闲再执行释放，避免阻塞渲染帧（WPF Dispatcher 支持该优先级）。
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                ReleaseVisibleHiToBase();
                CompactLohSoon();   // 释放后压缩大对象堆，把空段归还系统
            }));
    }

    /// <summary>空闲期一次性 LOH 压缩：普通 Gen2 回收只释放对象，大对象堆分段不压缩、
    /// 已提交内存不归还；CompactOnce 才把对象挪紧并归还空段（借鉴 KillerPDF：关掉缓存后
    /// 任务管理器仍显示峰值内存即此因）。仅空闲/关文档时机调用，10 秒节流。</summary>
    private void CompactLohSoon()
    {
        long now = Environment.TickCount64;
        if (now - _lastLohCompactTickMs < 10_000)
            return;
        _lastLohCompactTickMs = now;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
        }));
    }

    /// <summary>空闲释放：把可见页±缓冲中的 hi 位图换回 base（LRU 命中复用）。
    /// 视口内页面全部保留 hi 清晰（"进入视口就清晰"策略下 1s 空闲不清糊）；仅释放滚出视口的旧 hi 大图。</summary>
    private void ReleaseVisibleHiToBase()
    {
        if (!HasPdf || Pages.Count == 0)
            return;
        var (first, last) = GetVisiblePageRange(_lastVerticalOffset, _lastViewportHeight);
        int start = Math.Max(0, first - PageBufferSize);
        int end = Math.Min(Pages.Count - 1, last + PageBufferSize);
        for (int i = start; i <= end; i++)
        {
            if (i >= first && i <= last)
                continue;                              // 视口内全部保留 hi，不清糊
            var page = Pages[i];
            if (page.Image is not BitmapSource bmp)
                continue;
            int baseW = GetBaseWidth(page.Width);
            if (bmp.PixelWidth > baseW)                // 是 hi：换回 base 释放大图（后台渲染，完成回调回填）
                StartPageRender(i, wantHi: false);
        }
    }

    /// <summary>视口宽度变化：槽位固定（页面尺寸永不重建），只更新宽度适配系数 WidthFit，
    /// 由整层 LayoutTransform=DisplayScale 等比缩放（对齐 KillerPDF lite 宽度适配）。
    /// 同一内容按比例保持在视口内 → 无旧尺寸占位、无闪动；渲染由滚动去抖驱动。</summary>
    public int ViewportWidth
    {
        get => _viewportWidth;
        set
        {
            if (!SetField(ref _viewportWidth, value))
                return;
            OnPropertyChanged(nameof(PageColumnWidthBase));   // 内容宽下限=视口宽：视口变化即需重算
            if (!HasPdf)
                return;
            double newFit = Math.Clamp((_viewportWidth - 40.0) / Math.Max(1, _targetPageWidth), 0.15, 5.0);
            if (Math.Abs(newFit - _widthFit) < 1e-6)
                return;
            double ratio = newFit / _widthFit;
            _widthFit = newFit;
            OnPropertyChanged(nameof(DisplayScale));
            OnPropertyChanged(nameof(PageColumnWidthBase));
            // 整层等比缩放：同一内容保持在视口内（比例精确，无重建、无占位闪动）。
            // 打开文档恢复位置期间不补偿：此时 _lastVerticalOffset 尚未恢复到目标位置，补偿会把视图拉回顶部
            if (!_restoringView && _lastViewportHeight > 0 && Pages.Count > 0)
                RequestScrollToOffset?.Invoke(_lastVerticalOffset * ratio);
        }
    }

    public MainViewModel()
    {
        _pdf.ThumbnailInkProvider = GetThumbnailInk;   // 缩略图墨迹复合：渲染请求入队时采集该页笔画快照
        OpenPdfCommand = new RelayCommand(OpenPdf);
        ZoomInCommand = new RelayCommand(() => ZoomBy(ZoomStepFactor));
        ZoomOutCommand = new RelayCommand(() => ZoomBy(1.0 / ZoomStepFactor));
        GoToPreviousPageCommand = new RelayCommand(GoToPreviousPage);
        GoToNextPageCommand = new RelayCommand(GoToNextPage);
        GoHomeCommand = new RelayCommand(GoHome);
        OpenSettingsCommand = new RelayCommand(() => IsSettingsVisible = true);
        CloseSettingsCommand = new RelayCommand(() => IsSettingsVisible = false);
        Settings = SettingsService.Load();
        ApplyDefaultInkOptions();   // 「默认选项」作为启动初始值（打开文档后由 ApplyDocumentMenuState 按文档记忆覆盖）
        InkTool.EraserSize = InkToolViewModel.EraserSizeFromSlider(Settings.EraserSizeSetting);   // 橡皮粗细设置 → 实际直径（15 + 3.5x），启动即生效
        InkTool.IsStrokeEraser = Settings.EraserModeByStrokeSetting;   // 橡皮模式（面积擦/墨迹擦）恢复
        // 恢复上次批注墨迹选项（LoadFromSettings：装入各类型记忆并按记忆页签类型装入活动值）
        InkTool.FitToCurve = Settings.IsFitToCurve;
        InkTool.IsUselightThemeColor = Settings.UseLightThemeColor;
        InkTool.LoadFromSettings(Settings);
    }

    /// <summary>构建打印数据源（UI 线程）：读取每页物理点尺寸并换算为 96 DIP/inch，
    /// 按当前页面旋转角给出显示尺寸。墨迹与渲染服务由返回值携带给打印对话框。</summary>
    internal PrintDocumentSource CreatePrintSource()
    {
        int count = Math.Min(_pdf.PageCount, Pages.Count);
        var list = new List<PrintPageInfo>(count);
        for (int i = 0; i < count; i++)
        {
            var (pw, ph) = _pdf.GetPageSizePoints(i);
            double cw = pw * 96.0 / 72.0;
            double ch = ph * 96.0 / 72.0;
            var page = Pages[i];
            bool swap = page.Rotation is 90 or 270;
            list.Add(new PrintPageInfo(i, page, cw, ch,
                swap ? ch : cw, swap ? cw : ch, page.Rotation));
        }
        return new PrintDocumentSource(_pdf, list);
    }

    private async void OpenPdf()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            Title = "打开 PDF"
        };

        if (dialog.ShowDialog() != true) return;

        await OpenPdfFile(dialog.FileName);
    }

    /// <summary>打开指定路径的 PDF（对话框选择与启动参数带入共用）。</summary>
    internal async Task OpenPdfFile(string path, string? password = null)
    {
        IsOpeningPdf = true;   // 先显示加载页（不透明全屏，同时天然阻断再次点击）
        try
        {
            ConsoleLog.Write("PDF打开", $"开始: {path}");
            var stopwatch = Stopwatch.StartNew();

            // 切换文档：先 flush 旧库（首次打开 InkStore 为 null，处理器内自判），随后置空防串写
            DocumentClosing?.Invoke();
            InkStore = null;

            // 目标宽度依赖视口，先在 UI 线程算好再进后台
            _targetPageWidth = Math.Max(400, _viewportWidth - 40);
            int targetWidth = _targetPageWidth;
            bool autoSave = Settings.IsAutoSaveInk;   // 关闭"自动保存"= 完全停用：不建库不恢复（E1）

            // 后台：打开文档 + 页面/缩略图尺寸循环（均持 PdfiumLock，线程安全）
            // + 墨迹库（算文件内容哈希、列目录判别、读三份数据——均非 UI 线程）
            var sizes = await Task.Run(() =>
            {
                var sizeSw = Stopwatch.StartNew();
                _pdf.OpenDocument(path, password);
                long openMs = sizeSw.ElapsedMilliseconds;   // 仅打开文档（解析交叉引用/页树）
                var pageSizes = new PageSize[_pdf.PageCount];
                var thumbSizes = new PageSize[_pdf.PageCount];
                // 同一页只向 PDFium 取一次点尺寸（一次 FPDF_LoadPage），页面槽位与缩略图槽位由同一结果派生
                for (int i = 0; i < _pdf.PageCount; i++)
                {
                    var (pw, ph) = _pdf.GetPageSizePoints(i);
                    pageSizes[i] = ScaleToWidth(pw, ph, targetWidth);
                    thumbSizes[i] = ScaleToWidth(pw, ph, ThumbnailWidth);
                }
                long sizeMs = sizeSw.ElapsedMilliseconds - openMs;   // 仅逐页尺寸测量

                var store = autoSave ? InkStoreService.TryOpen(path) : null;
                if (!autoSave)
                    ConsoleLog.Write("PDF打开", "墨迹恢复: 自动保存关闭，跳过哈希验证");
                var pageInks = store?.LoadPages() ?? new Dictionary<int, byte[]>();
                var boardSlots = store?.LoadBoardSlots() ?? new Dictionary<int, byte[]>();
                var viewState = store?.LoadViewState() ?? new Dictionary<string, string>();
                long inkMs = sizeSw.ElapsedMilliseconds - openMs - sizeMs;
                // 逐页旋转态：键 pdf_rot_{0-based页索引}（忽略旧版全局 pdf_rotation）
                var pageRotations = new Dictionary<int, int>();
                foreach (var (key, value) in viewState)
                {
                    if (!key.StartsWith("pdf_rot_", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!int.TryParse(key["pdf_rot_".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                        continue;
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var deg))
                        continue;
                    int rotation = NormalizeRotation(deg);
                    if (index >= 0 && rotation != 0)
                        pageRotations[index] = rotation;
                }
                return (pageSizes, thumbSizes, pageRotations, store, pageInks, boardSlots, viewState, openMs, sizeMs, inkMs);
            });

            // 回 UI 线程：建集合 + 后续懒加载（InkStore 只在 UI 线程赋值/读取）
            InkStore = sizes.store;
            ApplyDocumentMenuState(sizes.viewState);   // 菜单记忆回读（先复位代码默认，再按键覆盖）
            PdfName = Path.GetFileNameWithoutExtension(path);
            HasPdf = true;

            // 恢复缩放必须先于 ReloadPages：页槽按最终 DisplayScale 构建，避免打开后再整体重排
            if (sizes.viewState.TryGetValue("pdf_zoom", out var zoomStr)
                && double.TryParse(zoomStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom))
                ZoomLevel = zoom;
            else
                ZoomLevel = 1.0;   // 无墨迹文档：缩放复位，防止继承上一个 PDF 的缩放

            _restoringView = true;   // 恢复期间屏蔽视口宽度变化引起的滚动补偿

            _pageRotations.Clear();   // 旋转态必须先于 ReloadPages 生效（槽位尺寸按各页旋转角交换）
            foreach (var (index, rotation) in sizes.pageRotations)
                _pageRotations[index] = rotation;
            await ReloadPages(sizes.pageSizes, sizes.thumbSizes);

            // 恢复墨迹（订阅方分帧执行：PDF 页墨迹 + 画板槽位/页签；无库时以空数据触发，把画板重置为 1/1 防上一文档残留）
            if (DocumentOpened != null)
                await DocumentOpened(new InkRestoreData(sizes.store, sizes.pageInks,
                    sizes.boardSlots, sizes.viewState));

            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!HasPdf)
                {
                    IsOpeningPdf = false;   // 文档已不在：直接撤加载页，不留死等
                    _restoringView = false;
                    return;
                }
                double offset = 0;
                bool hasOffset = sizes.viewState.TryGetValue("pdf_offset", out var offStr)
                                 && double.TryParse(offStr, NumberStyles.Float, CultureInfo.InvariantCulture, out offset);
                int page = 1;
                if (sizes.viewState.TryGetValue("pdf_page", out var pageStr))
                    int.TryParse(pageStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out page);

                // 垂直位置：优先比例（跨缩放/视口稳定），其次绝对偏移，最后页码
                double vRatio = -1;
                if (sizes.viewState.TryGetValue("pdf_offset_ratio", out var vStr)
                    && double.TryParse(vStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var vr)
                    && vr >= 0)
                    vRatio = vr;
                if ((hasOffset || vRatio >= 0) && RequestRestoreScroll != null)
                    RequestRestoreScroll(offset, vRatio, Math.Max(1, page));
                else
                    GoToPage(Math.Max(1, page));   // 无墨迹文档：滚动复位到页顶，防止继承上一个 PDF 的滚动位置

                // 水平位置：db 有记忆（0..1 比例）就恢复，没有则居中（负值）
                double hRatio = -1;
                if (sizes.viewState.TryGetValue("pdf_hoffset", out var hStr)
                    && double.TryParse(hStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
                    && h >= 0)
                    hRatio = h;
                RequestRestoreHorizontal?.Invoke(hRatio);

                // 本帧布局与 SizeChanged 全部走完后，再恢复正常宽度补偿
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => _restoringView = false));

                // 首屏兜底：用真实滚动容器尺寸同步一次可见页，确保位图请求一定被发起
                RequestVisiblePagesSync?.Invoke();

                // 位置恢复后再等首屏可见页位图就位，才撤加载页（成功路径唯一出口；含超时兜底）
                stopwatch.Stop();
                ConsoleLog.Write("PDF打开",
                    $"打开成功: 页数={_pdf.PageCount}, 打开文档={sizes.openMs}ms, 尺寸={sizes.sizeMs}ms, 墨迹库={sizes.inkMs}ms, 集合={_lastReloadPagesMs}ms, 恢复墨迹页={sizes.pageInks.Count}, 画板槽={sizes.boardSlots.Count}, 耗时={stopwatch.ElapsedMilliseconds}ms");
                BeginWaitFirstScreenReady();
            }));
        }
        catch (PdfRenderService.PasswordRequiredException)
        {
            _restoringView = false;
            ConsoleLog.Write("PDF打开", "需要密码");
            await ShowPasswordDialog(path);   // 加载页保持打开，密码框盖在其上；撤页在弹窗放弃出口做
        }
        catch (Exception ex)
        {
            _restoringView = false;
            IsOpeningPdf = false;   // 失败路径撤加载页（成功路径由首屏就绪轮询撤），再弹错误框
            ConsoleLog.Write("PDF打开", $"打开失败: {ex}");
            App.Log("OpenPdfFile", ex);
            await ShowOpenFailedDialog(path, ex.Message);   // iuwm 弹窗替代系统 MessageBox
        }
    }

    /// <summary>PDF 打开失败的 iuwm 弹窗（替代系统 MessageBox）：确定=关闭，重试=同路径重开（再失败再次弹）。</summary>
    private async Task ShowOpenFailedDialog(string path, string message)
    {
        try
        {
            var dialog = new iNKORE.UI.WPF.Modern.Controls.ContentDialog
            {
                Title = "PDF 打开失败",
                Content = message,
                PrimaryButtonText = "确定",
                SecondaryButtonText = "重试",
                Owner = Application.Current.MainWindow,
            };
            // 主按钮用强调样式（与更新日志/渐近线弹窗一致）
            dialog.SetResourceReference(iNKORE.UI.WPF.Modern.Controls.ContentDialog.PrimaryButtonStyleProperty,
                iNKORE.UI.WPF.Modern.ThemeKeys.AccentButtonStyleKey);
            var result = await dialog.ShowAsync();
            if (result == iNKORE.UI.WPF.Modern.Controls.ContentDialogResult.Secondary)
                await OpenPdfFile(path);   // 重试：同路径重新打开，再次失败会再进 catch 再弹
        }
        catch (Exception ex)
        {
            App.Log("OpenPdfFile.Dialog", ex);   // 弹窗本身失败只记日志，不回退系统弹窗
        }
    }

    /// <summary>密码 PDF 输入弹窗（iuwm ContentDialog + 最简 PasswordBox）：检测到密码时加载页保持打开、密码框盖在其上。
    /// 打开=验证密码（错则弹窗不关+弹窗内红字提示，对则关闭+成功浮动通知并完成打开），关闭=放弃并撤加载页。</summary>
    private async Task ShowPasswordDialog(string path)
    {
        try
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            panel.Children.Add(new TextBlock { Text = "此 PDF 受密码保护，请输入打开密码：", Margin = new Thickness(0, 0, 0, 8) });
            var box = new PasswordBox { Width = 300 };   // 画廊最简用法，iuwm 主题接管样式
            panel.Children.Add(box);
            // 密码错误提示（弹窗内红字）：遮罩压住浮动通知，错误改在弹窗内部显示，初始隐藏
            var errorText = new TextBlock
            {
                Text = "密码不正确，请重新输入密码",
                Foreground = Brushes.Red,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            panel.Children.Add(errorText);

            var dialog = new iNKORE.UI.WPF.Modern.Controls.ContentDialog
            {
                Title = "输入密码",
                Content = panel,
                PrimaryButtonText = "打开",
                CloseButtonText = "关闭",
                Owner = Application.Current.MainWindow,
            };
            // 主按钮强调样式（与打开失败/更新日志弹窗一致）
            dialog.SetResourceReference(iNKORE.UI.WPF.Modern.Controls.ContentDialog.PrimaryButtonStyleProperty,
                iNKORE.UI.WPF.Modern.ThemeKeys.AccentButtonStyleKey);
            dialog.Opened += (_, _) => box.Focus();   // 弹出即聚焦密码框

            bool validatedOk = false;
            dialog.PrimaryButtonClick += async (_, e) =>
            {
                var deferral = e.GetDeferral();   // 异步验证期间弹窗保持打开
                errorText.Visibility = Visibility.Collapsed;   // 每次尝试先清掉上次错误提示
                try
                {
                    await Task.Run(() => _pdf.OpenDocument(path, box.Password));
                    validatedOk = true;
                }
                catch (PdfRenderService.PasswordRequiredException)
                {
                    e.Cancel = true;   // 密码不正确：不关闭弹窗
                    errorText.Visibility = Visibility.Visible;
                    box.Focus(); box.SelectAll();
                }
                catch (Exception)
                {
                    // 非密码问题（如文件损坏/被删）：放行关闭，交回 OpenPdfFile 的打开失败弹窗
                }
                deferral.Complete();
            };

            var result = await dialog.ShowAsync();
            if (result == iNKORE.UI.WPF.Modern.Controls.ContentDialogResult.Primary)
            {
                if (validatedOk)
                    ShowTip?.Invoke("密码正确，已成功打开 PDF",
                        iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity.Success);
                await OpenPdfFile(path, box.Password);   // 加载页全程保持 true，由重开的成功/失败路径接管撤页
            }
            else
                IsOpeningPdf = false;   // 「关闭」放弃：撤加载页（检测到密码后加载页一直开着，必须在此收）
        }
        catch (Exception ex)
        {
            IsOpeningPdf = false;   // 弹窗自身出错：兜底撤加载页，避免永久转圈
            App.Log("OpenPdfFile.PasswordDialog", ex);   // 弹窗本身失败只记日志
        }
    }

    /// <summary>把设置页「默认选项」写入运行期墨迹记忆字段（均 JsonIgnore，不入 Settings.json）。</summary>
    private void ApplyDefaultInkOptions()
    {
        Settings.InkType = Math.Clamp(Settings.DefaultPdfPenMode, 0, 2);
        Settings.PenColor = Settings.DefaultPdfPenColor;
        Settings.PenWidth = Math.Clamp(Settings.DefaultPdfPenWidth, 1, 30);
        Settings.HighlighterColor = Settings.DefaultPdfHighlighterColor;
        Settings.HighlighterWidth = Math.Clamp(Settings.DefaultPdfHighlighterWidth, 1, 30);
        Settings.InkAlpha = Math.Clamp(Settings.DefaultPdfHighlighterAlpha, 20, 240);
        Settings.UseLightThemeColor = Settings.DefaultPdfColorLightTheme;
        Settings.IsMultiFingerInkEnabled = Settings.DefaultPdfMultiFinger;
        Settings.EraserSizeSetting = Math.Clamp(Settings.DefaultPdfEraserSize, 1, 30);
        Settings.EraserModeByStrokeSetting = Settings.DefaultPdfEraserMode == 1;
    }

    /// <summary>文档打开时回读菜单记忆（per-PDF 墨迹库 view_state）：先把迁移字段重置为「默认选项」
    /// （防止继承上一个 PDF 的值，同 ZoomLevel 复位语义），再逐键覆盖，最后按构造函数同款顺序灌入 InkTool。
    /// 键缺失/解析失败保留默认值；无库文档（自动保存关闭）自然全走默认。</summary>
    private void ApplyDocumentMenuState(IReadOnlyDictionary<string, string> viewState)
    {
        ApplyDefaultInkOptions();

        if (viewState.TryGetValue("pdf_ink_type", out var s1)
            && int.TryParse(s1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v1))
            Settings.InkType = Math.Clamp(v1, 0, 2);
        if (viewState.TryGetValue("pdf_pen_color", out var s2)
            && int.TryParse(s2, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v2))
            Settings.PenColor = v2;
        if (viewState.TryGetValue("pdf_pen_width", out var s3)
            && double.TryParse(s3, NumberStyles.Float, CultureInfo.InvariantCulture, out var v3))
            Settings.PenWidth = v3;
        if (viewState.TryGetValue("pdf_hl_color", out var s4)
            && int.TryParse(s4, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v4))
            Settings.HighlighterColor = v4;
        if (viewState.TryGetValue("pdf_hl_width", out var s5)
            && double.TryParse(s5, NumberStyles.Float, CultureInfo.InvariantCulture, out var v5))
            Settings.HighlighterWidth = v5;
        if (viewState.TryGetValue("pdf_ink_alpha", out var s6)
            && int.TryParse(s6, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v6))
            Settings.InkAlpha = v6;
        if (viewState.TryGetValue("pdf_palette_light", out var s7))
            Settings.UseLightThemeColor = s7 == "1";
        if (viewState.TryGetValue("pdf_multi_finger", out var s8))
            Settings.IsMultiFingerInkEnabled = s8 == "1";
        if (viewState.TryGetValue("pdf_eraser_size", out var s9)
            && int.TryParse(s9, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v9))
            Settings.EraserSizeSetting = v9;
        if (viewState.TryGetValue("pdf_eraser_bystroke", out var s10))
            Settings.EraserModeByStrokeSetting = s10 == "1";

        InkTool.EraserSize = InkToolViewModel.EraserSizeFromSlider(Settings.EraserSizeSetting);   // 橡皮粗细 → 实际直径
        InkTool.IsStrokeEraser = Settings.EraserModeByStrokeSetting;   // 橡皮模式（面积擦/墨迹擦）
        InkTool.IsUselightThemeColor = Settings.UseLightThemeColor;
        InkTool.LoadFromSettings(Settings);   // 装入各类型记忆并按记忆页签装入活动值
    }

    /// <summary>启动首屏就绪轮询：恢复位置后视口内页面位图全部就位才撤加载页；超时兜底强制退出。</summary>
    private void BeginWaitFirstScreenReady()
    {
        _openReadyElapsedMs = 0;
        _openReadyTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(OpenReadyPollMs) };
        _openReadyTimer.Tick -= OnOpenReadyTick;
        _openReadyTimer.Tick += OnOpenReadyTick;
        _openReadyTimer.Start();
    }

    private void OnOpenReadyTick(object? sender, EventArgs e)
    {
        bool done = false;
        try
        {
            if (!HasPdf) done = true;   // 文档被关闭/回主页：直接放行
            else if (_lastViewportHeight > 0 && Pages.Count > 0)
            {
                var (first, last) = GetVisiblePageRange(_lastVerticalOffset, _lastViewportHeight);
                done = true;
                for (int i = first; i <= last; i++)
                    if (Pages[i].Image == null) { done = false; break; }   // 可见页还有没位图的
            }
            _openReadyElapsedMs += OpenReadyPollMs;
            if (_openReadyElapsedMs >= OpenReadyTimeoutMs)
            {
                ConsoleLog.Write("PDF打开", $"首屏超时: 视口高={_lastViewportHeight:F0}, 页数={Pages.Count}");
                App.LogText($"OpenReady: 首屏位图未在 {OpenReadyTimeoutMs}ms 内就绪，强制撤加载页");
                done = true;
            }
        }
        catch (Exception ex)
        {
            App.Log("OnOpenReadyTick", ex);
            done = true;   // 就绪判断异常不卡加载页
        }
        finally
        {
            if (done)
            {
                _openReadyTimer!.Stop();
                _openReadyTimer.Tick -= OnOpenReadyTick;
                IsOpeningPdf = false;
                CompactLohSoon();   // 首屏渲染产生的大对象堆垃圾：就绪后压缩归还
            }
        }
    }

    /// <summary>回到主页：关闭当前文档并清空页面/缩略图，回到启动时的空态（打开 PDF 页）。</summary>
    private void GoHome()
    {
        // 最开头 flush 旧库（Pages/滚动数据此时仍完整），随后置空防串写
        DocumentClosing?.Invoke();
        InkStore = null;
        _scrollDebounceTimer?.Stop();
        _sharpenTimer?.Stop();
        _releaseTimer?.Stop();
        CancelAllPendingRenders();   // 在途页面渲染全部作废
        _pdf.CloseDocument();
        PagesReset?.Invoke();   // 旧页模型全部作废：宿主清理画布与笔画追踪
        Pages = new();
        Thumbnails = new();
        OnPropertyChanged(nameof(Pages));
        OnPropertyChanged(nameof(Thumbnails));
        _pageTopDips = Array.Empty<double>();
        _thumbTopDips = Array.Empty<double>();
        _loadedStartIndex = _loadedEndIndex = -1;
        _thumbLoadedStartIndex = _thumbLoadedEndIndex = -1;
        _highlightedThumbIndex = -1;                  // 缩略图模型已清空：高亮索引复位
        _materializedStart = _materializedEnd = -1;   // 页模型已清空：挂载区间复位
        _firstVisiblePageIndex = 0;
        // 滚动/缩放交互状态复位：避免下一份文档沿用上一份的视口尺寸与缩放交互态
        _lastViewportHeight = 0;
        _lastVerticalOffset = 0;
        _lastRangeFirst = _lastRangeLast = -1;
        _zoomActive = false;
        IsZooming = false;
        _pageRotations.Clear();   // 回到主页：逐页旋转态复位（下一文档按自身侧车 db 恢复）
        PageNumberText = "1";
        IsThumbnailPanelVisible = false;
        IsSettingsVisible = false;
        InkTool.ClearHistory();
        PdfName = string.Empty;
        HasPdf = false;   // 触发工具栏/空态按钮 Visibility 切换
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(CurrentPage));
        CompactLohSoon();   // 关文档后压缩大对象堆（缓存/位图引用已断，正是归还空段的时机）
    }

    /// <summary>按当前视口宽度重建全部页面模型，并加载可见页（尺寸由后台线程预算好传入）。
    /// 页槽全量但极轻（Border/Grid/Image），墨迹画布只按可见范围挂载；
    /// 集合一次性填充即可——分批让帧会让 StackPanel 重复全量布局（O(N²) 卡顿源）。</summary>
    private Task ReloadPages(PageSize[] pageSizes, PageSize[] thumbSizes)
    {
        _isRebuildingPages = true;   // 中间态抑制 UpdateVisiblePages（Pages 与 _pageTopDips 暂不一致）
        // 文档切换：停掉旧文档的防抖/清晰/释放计时器（原来只有 GoHome 停，直开新文档会残留）
        // 并复位缩放交互态：打开时恢复 ZoomLevel 会启动 sharpen，若只停计时器不复位 _zoomActive，
        // ScheduleScrollRender 会永久早退 → 位图从不加载（表现为整页白屏，手动缩放一下才恢复）
        _zoomActive = false;
        IsZooming = false;
        _scrollDebounceTimer?.Stop();
        _sharpenTimer?.Stop();
        _releaseTimer?.Stop();

        _pdf.ClearCache();
        CancelAllPendingRenders();   // 旧文档的在途渲染全部作废，避免旧位图写入新页模型
        _loadedStartIndex = -1;
        _loadedEndIndex = -1;
        _thumbLoadedStartIndex = -1;
        _thumbLoadedEndIndex = -1;
        _firstVisiblePageIndex = 0;
        _highlightedThumbIndex = -1;                  // 旧缩略图模型全部销毁：高亮索引复位
        _materializedStart = _materializedEnd = -1;   // 旧 PageModel 全部销毁：挂载区间复位
        _targetPageWidth = Math.Max(400, _viewportWidth - 40);
        // 宽度适配按当前视口宽一次性算好：打开后 SizeChanged 因值未变而提前返回，不会触发滚动补偿
        _widthFit = Math.Clamp((_viewportWidth - 40.0) / Math.Max(1, _targetPageWidth), 0.15, 5.0);

        var sw = Stopwatch.StartNew();

        // 一次性构造并整体替换：List 替换只触发一次 UI 通知（1 万页时省掉上万次 CollectionChanged）
        var newThumbs = new List<ThumbnailModel>(thumbSizes.Length);
        for (int i = 0; i < thumbSizes.Length; i++)
        {
            // NaturalWidth/Height 为未旋转尺寸；显示尺寸按该页旋转角推算（旋转后缩略图宽度仍为 ThumbnailWidth）
            int rotation = _pageRotations.TryGetValue(i, out var deg) ? deg : 0;
            var thumb = new ThumbnailModel
            {
                PageIndex = i,
                NaturalWidth = thumbSizes[i].Width,
                NaturalHeight = thumbSizes[i].Height,
                Rotation = rotation,
            };
            ApplyThumbnailRotation(thumb, rotation);
            newThumbs.Add(thumb);
        }

        var newPages = new List<PageModel>(pageSizes.Length);
        double maxExtent = 0;
        for (int i = 0; i < pageSizes.Length; i++)
        {
            // 内容尺寸固定为未旋转尺寸（墨迹坐标系）；显示槽位 = 该页旋转后的包围盒（位图由 PDFium 原生旋转）
            int rotation = _pageRotations.TryGetValue(i, out var deg) ? deg : 0;
            var page = new PageModel
            {
                PageIndex = i,
                ContentWidth = pageSizes[i].Width,
                ContentHeight = pageSizes[i].Height,
                Rotation = rotation,
            };
            ApplyPageRotation(page, rotation);
            maxExtent = Math.Max(maxExtent, page.Width);   // 旋转后的槽位宽（列宽只需覆盖页面宽度）
            newPages.Add(page);
        }

        PagesReset?.Invoke();   // 旧页模型全部作废：宿主清理画布与笔画追踪
        Thumbnails = newThumbs;
        Pages = newPages;
        OnPropertyChanged(nameof(Thumbnails));
        OnPropertyChanged(nameof(Pages));

        _maxPageExtent = maxExtent > 0 ? maxExtent : 800;
        OnPropertyChanged(nameof(PageColumnWidthBase));   // 列宽 = 最大页槽位宽 × 缩放（旋转 90° 也不超宽）

        // 缩略图顶部 DIP 前缀和：填充后一次性重建
        RebuildThumbTopDips();

        // 构建页面顶部 DIP 前缀和：可见页查找由 O(N) 遍历降为 O(logN) 二分（页面高度固定，缩放仅整体乘 ZoomLevel）。
        // 必须在 _isRebuildingPages=false 之前完成，保证守卫解除时状态已一致。
        RebuildPageTopDips();

        _lastReloadPagesMs = sw.ElapsedMilliseconds;
        _isRebuildingPages = false;

        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(CurrentPage));

        InkTool.ClearHistory();   // 新文档：旧命令引用的 PageModel 已销毁，清空撤销/重做栈

        UpdateThumbnailHighlight();   // 初始高亮当前页（首次 first==0 不触发 UpdateVisiblePages 的变更分支）

        if (_lastViewportHeight > 0)
            UpdateVisiblePages(_lastVerticalOffset, _lastViewportHeight);
        else
        {
            // 视口尺寸未知：请宿主用真实滚动容器同步一次（否则首屏位图可能永远不请求）
            RequestVisiblePagesSync?.Invoke();
            if (_lastViewportHeight > 0)
                UpdateVisiblePages(_lastVerticalOffset, _lastViewportHeight);
            else
            {
                UpdateMaterializedRange(0, Math.Min(PageBufferSize, Pages.Count - 1));   // 首屏附近先挂墨迹画布
                LoadPageImages(0, Math.Min(PageBufferSize, Pages.Count - 1), needHiInViewport: false);   // 初始只补 base
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>滚动/内容位置变化时：实时更新当前页指示与缩略图高亮，位图渲染走去抖（滚动期间不刷新位图）。</summary>
    public void UpdateVisiblePages(double verticalOffset, double viewportHeight)
    {
        _lastVerticalOffset = verticalOffset;
        _lastViewportHeight = viewportHeight;

        if (_isRebuildingPages)   // 重建中间态：Pages 与 _pageTopDips 不一致，跳过本轮（末尾 ReloadPages 会补调一次）
            return;
        if (!HasPdf || Pages.Count == 0 || viewportHeight <= 0)
            return;

        var (first, last) = GetVisiblePageRange(verticalOffset, viewportHeight);
        bool pageExited = _lastRangeFirst >= 0 && (first != _lastRangeFirst || last != _lastRangeLast);
        _lastRangeFirst = first;
        _lastRangeLast = last;

        UpdateMaterializedRange(first, last);   // 只保留可见范围（±缓冲）的墨迹画布

        // 当前页 = 视口中占比最大的页（交界处哪页占得多算哪页）。
        int dominant = GetDominantPageIndex(first, last, verticalOffset, viewportHeight);
        if (dominant >= 0 && dominant != _firstVisiblePageIndex)
        {
            _firstVisiblePageIndex = dominant;
            OnPropertyChanged(nameof(CurrentPage));
            UpdateThumbnailHighlight();
            SyncPageNumberText();
        }

        // 滚动停止后：补缺失 base；视口内页面（含部分可见）需要清晰的直接补 hi（不等成为当前页）。
        ScheduleScrollRender(pageExited);
    }

    /// <summary>墨迹画布按可见范围挂载：区间外的 PageModel 置 IsMaterialized=false，页槽随之销毁 InkCanvas，
    /// 滚回时重新生成。只对区间差集逐页切换，区间未变时 O(1) 早退——滚动高频调用不产生额外开销。
    /// Strokes 保存在 PageModel，卸载不丢墨迹。</summary>
    private void UpdateMaterializedRange(int firstVisible, int lastVisible)
    {
        int n = Pages.Count;
        if (n == 0)
        {
            _materializedStart = _materializedEnd = -1;
            return;
        }
        int start = Math.Max(0, firstVisible - MaterializeBufferPages);
        int end = Math.Min(n - 1, lastVisible + MaterializeBufferPages);
        if (start == _materializedStart && end == _materializedEnd)
            return;

        // 先卸掉离开区间的页（可能两段），再挂上新进入区间的页（同样最多两段）
        if (_materializedStart >= 0)
        {
            for (int i = _materializedStart; i <= _materializedEnd; i++)
                if (i < start || i > end)
                    Pages[i].IsMaterialized = false;
        }
        for (int i = start; i <= end; i++)
            if (i < _materializedStart || i > _materializedEnd)
                Pages[i].IsMaterialized = true;

        _materializedStart = start;
        _materializedEnd = end;
    }

    /// <summary>把页码输入框文本同步为当前页。</summary>
    private void SyncPageNumberText()
    {
        if (PageNumberText != CurrentPage.ToString())
            PageNumberText = CurrentPage.ToString();
    }

    /// <summary>更新缩略图当前页高亮（增量：只改旧/新两项，避免每次滚动对全部页发属性通知）。</summary>
    private void UpdateThumbnailHighlight()
    {
        int target = _firstVisiblePageIndex >= 0 && _firstVisiblePageIndex < Thumbnails.Count
            ? _firstVisiblePageIndex
            : -1;
        if (target == _highlightedThumbIndex)
            return;
        if (_highlightedThumbIndex >= 0 && _highlightedThumbIndex < Thumbnails.Count)
            Thumbnails[_highlightedThumbIndex].IsCurrent = false;
        if (target >= 0)
            Thumbnails[target].IsCurrent = true;
        _highlightedThumbIndex = target;
    }

    /// <summary>页面顶部/底部的缩放后坐标（前缀和 O(1)，整层等比）。</summary>
    private double ScaledPageTop(int i) => (_pageTopDips[i] + i * PageVerticalMargin) * DisplayScale;
    private double ScaledPageBottom(int i) => (_pageTopDips[i + 1] + (i + 1) * PageVerticalMargin) * DisplayScale;

    private (int First, int Last) GetVisiblePageRange(double verticalOffset, double viewportHeight)
    {
        int n = Pages.Count;
        if (n == 0)
            return (-1, -1);

        var visibleTop = Math.Max(0, verticalOffset);
        var visibleBottom = visibleTop + viewportHeight;

        // 整层等比缩放（槽位固定）：页面布局 = (高度+间距)×DisplayScale，位置由前缀和 O(1) 得到；两个单调条件各一次二分（O(logN)）。
        double TopOf(int i) => ScaledPageTop(i);
        double BottomOf(int i) => ScaledPageBottom(i);

        int firstVisible = -1;
        int lo = 0, hi = n - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (BottomOf(mid) >= visibleTop) { firstVisible = mid; hi = mid - 1; }
            else lo = mid + 1;
        }

        int lastVisible = -1;
        lo = 0; hi = n - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (TopOf(mid) <= visibleBottom) { lastVisible = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        if (firstVisible == -1)
            firstVisible = n - 1;
        if (lastVisible == -1)
            lastVisible = firstVisible;

        return (firstVisible, lastVisible);
    }

    /// <summary>当前页 = 与视口相交面积最大的页（交界处哪页占得多算哪页；平局取靠前者）。</summary>
    private int GetDominantPageIndex(int first, int last, double verticalOffset, double viewportHeight)
    {
        if (first < 0)
            return first;
        double visibleTop = Math.Max(0, verticalOffset);
        double visibleBottom = visibleTop + viewportHeight;
        int dominant = first;
        double best = -1;
        for (int i = first; i <= last; i++)
        {
            double inter = Math.Min(ScaledPageBottom(i), visibleBottom) - Math.Max(ScaledPageTop(i), visibleTop);
            if (inter > best)
            {
                best = inter;
                dominant = i;
            }
        }
        return dominant;
    }

    /// <summary>按固定倍率缩放（自动夹在 0.05x~5x）。返回是否发生实际变化（clamp 边界时为 false）。</summary>
    public bool ZoomBy(double factor)
    {
        double next = Math.Clamp(_zoomLevel * factor, 0.05, 5.0);
        if (Math.Abs(next - _zoomLevel) < 1e-9)
            return false;
        ZoomLevel = next;
        return true;
    }

    /// <summary>跳转到指定页（1-based，自动夹在 1~总页数）。滚动由 RequestScrollToPage 完成。</summary>
    public void GoToPage(int page)
    {
        if (!HasPdf || TotalPages == 0)
            return;
        RequestScrollToPage?.Invoke(Math.Clamp(page, 1, TotalPages));
    }

    public void GoToPreviousPage() => GoToPage(CurrentPage - 1);

    public void GoToNextPage() => GoToPage(CurrentPage + 1);

    /// <summary>计算某页（0-based）顶部的垂直偏移，用于跳页滚动（前缀和 O(1)，整层等比）。</summary>
    public double GetPageVerticalOffset(int pageIndex)
    {
        int i = Math.Min(pageIndex, Pages.Count);
        return (_pageTopDips[i] + i * PageVerticalMargin) * DisplayScale;
    }

    /// <summary>旋转当前页（±90°，顺时针为正）：页面位图按新方向原生重渲染，显示槽位同步交换；
    /// 墨迹坐标系不变（墨迹层仅显示旋转，不改 ISF）。其他页方向与尺寸不受影响。</summary>
    public void RotateCurrentPage(int delta)
    {
        if (!HasPdf || Pages.Count == 0)
            return;
        int index = CurrentPage - 1;
        if (index < 0 || index >= Pages.Count)
            return;

        var page = Pages[index];
        int next = NormalizeRotation(page.Rotation + delta);
        if (next == page.Rotation)
            return;

        page.Rotation = next;
        ApplyPageRotation(page, next);
        page.Image = null;   // 方向变了：按新旋转角重新渲染（缓存按 (页,旋转) 区分，不误用旧方向）

        if (next == 0)
            _pageRotations.Remove(index);
        else
            _pageRotations[index] = next;

        RebuildPageTopDips();

        // 缩略图同步（仅该页；位图尺寸/方向变化 → 清位图，由调用方按新布局重载）
        if (index < Thumbnails.Count)
        {
            var thumb = Thumbnails[index];
            ApplyThumbnailRotation(thumb, next);
            thumb.Image = null;
            thumb.IsLoading = false;
            _thumbLoadedStartIndex = _thumbLoadedEndIndex = -1;   // 强制下一次 LoadThumbnails 重新加载（可见范围可能未变）
            RebuildThumbTopDips();
        }

        ConsoleLog.Write("PDF旋转",
            $"第{index + 1}页: 旋转={next}°, 槽位={page.Width}×{page.Height}, 内容={page.ContentWidth}×{page.ContentHeight}");
    }

    /// <summary>页面显示槽位 = 旋转后包围盒：90/270 时宽高交换，其余与内容尺寸相同。</summary>
    private static void ApplyPageRotation(PageModel page, int rotation)
    {
        bool swap = rotation is 90 or 270;
        page.Width = swap ? page.ContentHeight : page.ContentWidth;
        page.Height = swap ? page.ContentWidth : page.ContentHeight;
    }

    /// <summary>缩略图显示尺寸（位图由渲染层按旋转角原生旋转，尺寸即显示尺寸）：
    /// 旋转后显示宽度恒为 ThumbnailWidth（不撑破侧栏），高度按未旋转宽高比换算。</summary>
    private static void ApplyThumbnailRotation(ThumbnailModel thumb, int rotation)
    {
        thumb.Rotation = rotation;
        if (rotation is 90 or 270)
        {
            thumb.Width = ThumbnailWidth;
            thumb.Height = Math.Max(1, (int)Math.Round(
                ThumbnailWidth * (double)thumb.NaturalWidth / Math.Max(1, thumb.NaturalHeight)));
        }
        else
        {
            thumb.Width = thumb.NaturalWidth;
            thumb.Height = thumb.NaturalHeight;
        }
    }

    /// <summary>重建页面顶部 DIP 前缀和（页面高度 = 当前旋转下的显示槽位高）。</summary>
    private void RebuildPageTopDips()
    {
        _pageTopDips = new double[Pages.Count + 1];
        double acc = 0;
        for (int i = 0; i < Pages.Count; i++)
        {
            _pageTopDips[i] = acc;
            acc += Pages[i].Height;
        }
        _pageTopDips[Pages.Count] = acc;
    }

    /// <summary>重建缩略图顶部 DIP 前缀和（缩略图高度 = 当前旋转下的显示槽位高）。</summary>
    private void RebuildThumbTopDips()
    {
        _thumbTopDips = new double[Thumbnails.Count + 1];
        double acc = 0;
        for (int i = 0; i < Thumbnails.Count; i++)
        {
            _thumbTopDips[i] = acc;
            acc += Thumbnails[i].Height;
        }
        _thumbTopDips[Thumbnails.Count] = acc;
    }

    /// <summary>旋转角归一化：仅接受 0/90/180/270，其它值按 0 处理。</summary>
    private static int NormalizeRotation(int degrees)
    {
        int r = ((degrees % 360) + 360) % 360;
        return r is 0 or 90 or 180 or 270 ? r : 0;
    }

    /// <summary>提交页码：解析输入框内容并跳页（Enter/失焦时调用）。</summary>
    public void CommitPageNumber()
    {
        if (!HasPdf)
            return;
        if (int.TryParse(PageNumberText, out int page))
            GoToPage(page);
        else
            SyncPageNumberText();   // 非法输入回填为当前页
    }

    /// <summary>根据缩略图滚动位置加载可见缩略图及前后缓冲页。extentHeight 用于实测每项图片外布局高度。</summary>
    public void UpdateVisibleThumbnails(double verticalOffset, double viewportHeight, double extentHeight)
    {
        if (!HasPdf || Thumbnails.Count == 0 || viewportHeight <= 0)
            return;

        // 实测每项图片外布局高度：总高度 = Σ(图高 + chrome) → chrome = (extent − Σ图高)/项数。
        // 模板改动/字体行高/IsCurrent 加粗边框都自动吸收；布局未就绪（extent 不足）时保留回退值。
        double sumImages = _thumbTopDips[_thumbTopDips.Length - 1];
        if (extentHeight > sumImages)
            _thumbItemChrome = (extentHeight - sumImages) / Thumbnails.Count;

        var (firstVisible, lastVisible) = GetVisibleThumbnailRange(verticalOffset, viewportHeight);
        LoadThumbnails(firstVisible, lastVisible);
    }

    private (int First, int Last) GetVisibleThumbnailRange(double verticalOffset, double viewportHeight)
    {
        int n = Thumbnails.Count;
        if (n == 0)
            return (-1, -1);

        var visibleTop = Math.Max(0, verticalOffset);
        var visibleBottom = visibleTop + viewportHeight;
        double m = _thumbItemChrome;

        // 缩略图布局高度 = Height + 实测 chrome，位置由前缀和 O(1) 得到；单调条件各一次二分（O(logN)）。
        double TopOf(int i) => _thumbTopDips[i] + i * m;
        double BottomOf(int i) => _thumbTopDips[i + 1] + (i + 1) * m;

        int firstVisible = -1;
        int lo = 0, hi = n - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (BottomOf(mid) >= visibleTop) { firstVisible = mid; hi = mid - 1; }
            else lo = mid + 1;
        }

        int lastVisible = -1;
        lo = 0; hi = n - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (TopOf(mid) <= visibleBottom) { lastVisible = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        if (firstVisible == -1)
            firstVisible = n - 1;
        if (lastVisible == -1)
            lastVisible = firstVisible;

        return (firstVisible, lastVisible);
    }

    private void LoadThumbnails(int firstVisibleIndex, int lastVisibleIndex)
    {
        if (Thumbnails.Count == 0)
            return;

        var startIndex = Math.Max(0, firstVisibleIndex - ThumbnailBufferSize);
        var endIndex = Math.Min(Thumbnails.Count - 1, lastVisibleIndex + ThumbnailBufferSize);

        // 焦点每次滚动都更新：即使缓冲区未变，也要让排队项按新可见区重排、丢弃滚出期望范围的过期请求
        //（必须在下面的早退之前——可见区在缓冲区内滑动时缓冲区间不变，但优先级要刷新）。
        _pdf.UpdateThumbnailFocus(firstVisibleIndex, lastVisibleIndex, startIndex, endIndex);

        if (startIndex == _thumbLoadedStartIndex && endIndex == _thumbLoadedEndIndex)
            return;

        // 释放已滚出缓冲区的缩略图。
        if (_thumbLoadedStartIndex >= 0 && _thumbLoadedEndIndex >= _thumbLoadedStartIndex)
        {
            for (int i = _thumbLoadedStartIndex; i <= _thumbLoadedEndIndex; i++)
            {
                if (i < startIndex || i > endIndex)
                    Thumbnails[i].Image = null;
            }
        }

        // 渲染缺失的缩略图：后台线程渲染（并发受限、独立 reader），完成后 Dispatcher 更新 UI——
        // 避免大 PDF 打开/快速滚动时逐张阻塞 UI 线程。
        for (int i = startIndex; i <= endIndex; i++)
            RequestThumbnail(i);

        _thumbLoadedStartIndex = startIndex;
        _thumbLoadedEndIndex = endIndex;
    }

    /// <summary>发起单页缩略图渲染（LoadThumbnails 与墨迹刷新共用）：非 force 时已有图/已在加载则跳过。
    /// 渲染宽度取该页旋转后的显示宽 × DPI 系数；完成后校验模型/方向/尺寸未变才回填。</summary>
    private void RequestThumbnail(int index, bool force = false)
    {
        var thumb = Thumbnails[index];
        if (!force && (thumb.Image != null || thumb.IsLoading))
            return;
        thumb.IsLoading = true;
        int rotation = thumb.Rotation;
        int logicalWidth = thumb.Width;   // 旋转后显示宽（DIP）；完成回调据此校验方向/尺寸未变
        int thumbW = (int)Math.Round(logicalWidth * Math.Max(1.0, _dpiScale));
        _pdf.RenderThumbnailAsync(index, thumbW, rotation).ContinueWith(t =>
        {
            var img = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                thumb.IsLoading = false;
                // ReloadPages 可能已重建集合 / 该页旋转角已变：仅当模型与方向都未变才回填。
                if (img != null && index < Thumbnails.Count && ReferenceEquals(Thumbnails[index], thumb)
                    && thumb.Rotation == rotation && thumb.Width == logicalWidth)
                    thumb.Image = img;
            }));
        });
    }

    /// <summary>缩略图墨迹提供者（UI 线程调用）：引用数组快照；无墨迹返回 null 走纯 PDF 快路径。</summary>
    private ThumbnailInk? GetThumbnailInk(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count)
            return null;
        var page = Pages[pageIndex];
        if (page.Strokes.Count == 0)
            return null;
        // 引用数组快照（非深拷贝）：UI 线程只复制引用，微秒级，滚动/刷新/恢复多页时不再顿挫。
        // 后台绘制期间原集合可继续书写/撤销；快照内某笔恰被并发修改（点编辑/属性改，概率极低）
        // 由渲染侧 try/catch 兜底返回 null，旧图仍显示，下次墨迹变更/滚动自愈。
        var snap = page.Strokes.ToArray();
        return new ThumbnailInk(snap, page.ContentWidth, page.ContentHeight);
    }

    /// <summary>一页墨迹变更后刷新缩略图：失效该页缓存；已加载/加载中的缩略图立即重新渲染（含墨迹），
    /// 未加载的不动（后续滚动加载时自然带墨迹快照渲染）。</summary>
    public void RefreshThumbnailInk(int pageIndex)
    {
        if (!HasPdf || pageIndex < 0 || pageIndex >= Thumbnails.Count)
            return;
        _pdf.InvalidateThumbnailInk(pageIndex);
        var thumb = Thumbnails[pageIndex];
        bool inLoadedRange = _thumbLoadedStartIndex >= 0
            && pageIndex >= _thumbLoadedStartIndex && pageIndex <= _thumbLoadedEndIndex;
        if (!inLoadedRange && thumb.Image == null && !thumb.IsLoading)
            return;
        // 不清旧图：旧请求已被失效完成为 null（不会回填），新图就位后直接替换——期间旧图保留，无空白闪烁。
        // force 跳过守卫；若恰有新请求在途，RenderThumbnailAsync 的 inflight 去重共享同一任务，安全。
        RequestThumbnail(pageIndex, force: true);
    }

    private void LoadPageImages(int firstVisibleIndex, int lastVisibleIndex, bool needHiInViewport)
    {
        if (Pages.Count == 0)
            return;

        _lastRequestFirst = firstVisibleIndex;
        _lastRequestLast = lastVisibleIndex;
        _lastRequestNeedHi = needHiInViewport;

        var startIndex = Math.Max(0, firstVisibleIndex - PageBufferSize);
        var endIndex = Math.Min(Pages.Count - 1, lastVisibleIndex + PageBufferSize);

        // 释放已滚出缓冲区的页面位图。
        if (_loadedStartIndex >= 0 && _loadedEndIndex >= _loadedStartIndex)
        {
            for (int i = _loadedStartIndex; i <= _loadedEndIndex; i++)
            {
                if (i < startIndex || i > endIndex)
                {
                    Pages[i].Image = null;
                    CancelPendingRender(i);   // 该页已离开缓冲区：在途渲染开跑前作废，不再白渲染大图
                }
            }
        }

        // 渲染缺失页；放大超过 base 5% 时，视口内页面（含部分可见）都直接清晰——不再等成为"当前页"。
        // 内存策略（借鉴 KillerPDF）：hi 不入缓存（滚出即释放），缓冲区±2 页与未放大状态一律用 base（≤2048 宽，LRU 复用）。
        // 渲染全部在后台线程执行（单飞去重），完成回调校验期望宽度后回填——书写时不占用 UI 帧。
        for (int i = startIndex; i <= endIndex; i++)
        {
            var page = Pages[i];
            int baseW = GetBaseWidth(page.Width);
            int hiW = GetRenderWidth(page.Width);
            // 视口内 && 放大 → 需要清晰位图；缓冲区页与未放大状态 base 即视觉最优。
            bool wantHi = needHiInViewport && i >= firstVisibleIndex && i <= lastVisibleIndex && hiW > baseW * 1.05;
            if (page.Image == null)
            {
                StartPageRender(i, wantHi);
            }
            else if (wantHi && page.Image is BitmapSource bmp && bmp.PixelWidth != hiW)
            {
                // 视口内已有 base 的页 → 补渲染 hi（"进入视口没有清晰的就清晰"）。
                StartPageRender(i, wantHi: true);
            }
        }

        _loadedStartIndex = startIndex;
        _loadedEndIndex = endIndex;
    }

    /// <summary>发起一页的后台渲染（UI 线程调用）。单飞去重：同页在途渲染未完成时新请求由
    /// 完成回调的期望宽度重算兜底（尺寸过期自动按当前期望重发），此处直接跳过。
    /// 请求带取消源：滚出缓冲区/换文档时取消，PDFium 开跑前即可作废，避免白渲染 ≈20MB。</summary>
    private void StartPageRender(int index, bool wantHi)
    {
        if (index < 0 || index >= Pages.Count)
            return;
        var existing = _pendingPageRenders.GetValueOrDefault(index);
        if (existing != null && !existing.Cts.IsCancellationRequested)
            return;   // 在途且未作废：单飞跳过
        if (existing != null)
            DropPendingRender(index);   // 已作废旧请求：清掉账目后重发

        var page = Pages[index];
        int rotation = page.Rotation;   // 位图按该页旋转角原生渲染；完成回调校验方向未变
        int renderW = wantHi ? GetRenderWidth(page.Width) : GetBaseWidth(page.Width);
        long estimatedBytes = EstimateRenderBytes(page, renderW);

        // 在途字节上限：超限时取消离可见区最远的一页，腾出名额（与 KillerPDF 的队列字节预算同思路）。
        if (_inflightRenderBytes + estimatedBytes > MaxInflightRenderBytes)
        {
            int farthest = FindFarthestInflightRender(index);
            if (farthest >= 0)
                CancelPendingRender(farthest);
            else
                return;   // 无可取消项：放弃本次，下一轮防抖重试
        }

        var cts = new CancellationTokenSource();
        _pendingPageRenders[index] = new PendingPageRender(cts, estimatedBytes);
        _inflightRenderBytes += estimatedBytes;
        Task<ImageSource> task = wantHi
            ? _pdf.RenderPageAsync(index, renderW, page.Width, page.Height, rotation, cts.Token)
            : _pdf.RenderPageBaseAsync(index, renderW, page.Width, page.Height, rotation, cts.Token);
        task.ContinueWith(t =>
        {
            // 取消源不在这里释放：它可能仍在字典里被后续请求读取 IsCancellationRequested / Cancel，
            // 提前 Dispose 会抛 ObjectDisposedException；取消源本身无原生资源，交由 GC 回收。
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => CompletePageRender(index, page, rotation, cts, t)));
        });
    }

    /// <summary>估算一页渲染的像素字节（未旋转内容宽高 × 渲染像素宽 ÷ 槽位宽，BGRA 4 字节）。</summary>
    private static long EstimateRenderBytes(PageModel page, int renderW)
    {
        bool swap = page.Rotation is 90 or 270;
        int contentDipH = swap ? page.Width : page.Height;
        double pxPerDip = (double)renderW / Math.Max(1, page.Width);
        int pxH = Math.Max(1, (int)Math.Round(contentDipH * pxPerDip));
        return (long)renderW * pxH * 4;
    }

    /// <summary>取消一页在途渲染（滚出缓冲区时调用）：只发取消信号，账目与释放留给完成回调。</summary>
    private void CancelPendingRender(int index)
    {
        _pendingPageRenders.GetValueOrDefault(index)?.Cts.Cancel();
    }

    /// <summary>移除一页在途记录并冲销字节账目（取消源不在此释放：可能仍被旧任务/后续请求引用）。</summary>
    private void DropPendingRender(int index)
    {
        if (!_pendingPageRenders.Remove(index, out var entry))
            return;
        _inflightRenderBytes -= entry.EstimatedBytes;
    }

    /// <summary>取消全部在途渲染（换文档/回主页）：清账目、清字典（取消源交由旧任务自己的完成回调与 GC 处理）。</summary>
    private void CancelAllPendingRenders()
    {
        foreach (var entry in _pendingPageRenders.Values)
            entry.Cts.Cancel();
        _pendingPageRenders.Clear();
        _inflightRenderBytes = 0;
    }

    /// <summary>找离可见区最远且在途的页（排除 excludeIndex）：用于字节超限时腾名额。</summary>
    private int FindFarthestInflightRender(int excludeIndex)
    {
        int first = _lastRequestFirst < 0 ? 0 : _lastRequestFirst;
        int last = _lastRequestLast < 0 ? first : _lastRequestLast;
        int farthest = -1;
        int bestDistance = -1;
        foreach (var index in _pendingPageRenders.Keys)
        {
            if (index == excludeIndex)
                continue;
            int distance = index < first ? first - index : index > last ? index - last : 0;
            if (distance > bestDistance)
            {
                bestDistance = distance;
                farthest = index;
            }
        }
        return farthest;
    }

    /// <summary>后台渲染完成回填（UI 线程，Background 优先级——不与输入/湿墨抢帧）：
    /// 校验页模型仍有效、方向未变后按当前期望宽度决定：赋值 / 丢弃（尺寸过期且已滚出缓冲）/ 按当前期望重发。</summary>
    private void CompletePageRender(int index, PageModel page, int rotation, CancellationTokenSource cts,
        Task<ImageSource> task)
    {
        // 只冲销这笔自己的账目：同页可能已被新请求替换（尺寸过期重发），不能误删新记录
        if (_pendingPageRenders.TryGetValue(index, out var entry) && ReferenceEquals(entry.Cts, cts))
            DropPendingRender(index);
        if (index >= Pages.Count || !ReferenceEquals(Pages[index], page))
            return;   // 文档已重建，丢弃
        if (page.Rotation != rotation)
        {
            StartPageRender(index, _lastRequestNeedHi && index >= _lastRequestFirst && index <= _lastRequestLast);   // 旋转角已变：按新方向重发
            return;
        }
        if (task.IsCanceled)
            return;   // 已主动作废：静默丢弃（不是错误）
        if (!task.IsCompletedSuccessfully)
        {
            App.Log($"RenderPage p{index}", task.Exception?.GetBaseException());
            return;   // 放弃本次，下一轮防抖会重试
        }
        if (task.Result is not BitmapSource bmp)
            return;

        // 已滚出缓冲区（LoadPageImages 已置 Image=null）：丢弃。
        if (index < _loadedStartIndex || index > _loadedEndIndex)
            return;

        int baseW = GetBaseWidth(page.Width);
        int hiW = GetRenderWidth(page.Width);
        bool wantHi = _lastRequestNeedHi && index >= _lastRequestFirst && index <= _lastRequestLast
                      && hiW > baseW * 1.05;
        int desired = wantHi ? hiW : baseW;

        if (page.Image is BitmapSource current && current.PixelWidth == desired)
            return;   // 已有正确尺寸位图，防乒乓
        if (bmp.PixelWidth == desired)
        {
            page.Image = bmp;
            return;
        }

        // 交付尺寸过期（缩放/可见范围在渲染期间变化）：按当前期望重新发起。
        StartPageRender(index, wantHi);
    }

    /// <summary>窗口最小化/隐藏时停渲染与释放定时器；恢复前台时不主动重渲染
    /// （位图已在位，WPF 自动重传纹理），仅重启 1s 空闲释放计时，避免恢复瞬间同步渲染闪烁。</summary>
    public void OnWindowVisibilityChanged(bool visible)
    {
        if (!visible)
        {
            _scrollDebounceTimer?.Stop();
            _sharpenTimer?.Stop();
            _releaseTimer?.Stop();
            return;
        }

        RestartReleaseTimer();
    }

    public void Dispose()
    {
        _scrollDebounceTimer?.Stop();
        _sharpenTimer?.Stop();
        _releaseTimer?.Stop();
        _pdf.Dispose();
    }
}
