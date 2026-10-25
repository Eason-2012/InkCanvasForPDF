using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
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
    private const int RebuildPageSliceMs = 8;     // 页面容器拆除/填充时间片：每片让帧一次，圆圈动画不冻结
    /// <summary>缩放倍率：每档固定 ×1.1，保证各倍率下滚轮手感一致。</summary>
    public const double ZoomStepFactor = 1.1;

    private readonly PdfRenderService _pdf = new();

    private int _viewportWidth = 800;
    private int _targetPageWidth = 760;
    private int _loadedStartIndex = -1;
    private int _loadedEndIndex = -1;
    private readonly HashSet<int> _pendingPageRenders = new();   // 页面渲染单飞去重（仅 UI 线程访问）
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
    private DispatcherTimer? _openReadyTimer;        // 首屏就绪轮询：视口可见页位图齐了才撤加载页
    private int _openReadyElapsedMs;                 // 已轮询时长（超时兜底）
    private bool _isRebuildingPages;                 // ReloadPages 进行中：Pages 与 _pageTopDips 处于不一致中间态，抑制滚动驱动处理
    private double _widthFit = 1.0;                  // 宽度适配系数：页面槽位固定，宽度变化只更新此系数（整层等比缩放）
    private bool _zoomActive;                        // 缩放操作进行中（缩放引发的 ScrollChanged 不取消清晰）
    private bool _isZooming;                         // 缩放交互进行中：页面位图用 Linear 插值保证帧率
    private int _lastRangeFirst = -1;                // 上次可见范围（检测"有页退出视口"）
    private int _lastRangeLast = -1;
    private double[] _pageTopDips = Array.Empty<double>();   // 页面顶部 DIP 前缀和（未缩放，供二分找可见页）
    private double[] _thumbTopDips = Array.Empty<double>();  // 缩略图顶部 DIP 前缀和（供二分找可见缩略图）
    private double _thumbItemChrome = ThumbnailMargin;       // 每项图片外的布局高度（ExtentHeight 实测；ThumbnailMargin 作布局未就绪时的回退）

    public ObservableCollection<PageModel> Pages { get; } = new();
    public ObservableCollection<ThumbnailModel> Thumbnails { get; } = new();

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

    /// <summary>顶部工具栏是否可见（有 PDF、未打开设置、不在打开加载中；与回到主页一致：进设置即隐藏工具栏；
    /// 加载页为纯云母，白色顶栏在此期间必须隐藏）。</summary>
    public bool IsToolbarVisible => HasPdf && !IsSettingsVisible && !IsOpeningPdf;

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

    /// <summary>设置覆盖层是否显示（切换时通知工具栏可见性，进入设置即隐藏工具栏）。</summary>
    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        set
        {
            if (SetField(ref _isSettingsVisible, value))
            {
                OnPropertyChanged(nameof(IsToolbarVisible));
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    /// <summary>跳页滚动回调（1-based），由 MainWindow 注入实际滚动实现。</summary>
    public Action<int>? RequestScrollToPage { get; set; }

    /// <summary>滚动到指定垂直偏移（布局坐标），由 MainWindow 注入实际滚动实现（宽度变化保持页内位置用）。</summary>
    public Action<double>? RequestScrollToOffset { get; set; }

    /// <summary>恢复文档显示滚动位置（offset + 页码回退），由 MainWindow 注入（Loaded 时机调用，内部 UpdateLayout）。</summary>
    public Action<double, int>? RequestRestoreScroll { get; set; }

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

    /// <summary>页面缩放级别（0.15x ~ 5x）。缩放过程中仅由 ScaleTransform 即时缩放（禁止清晰渲染），
    /// 停止 50ms 后清晰当前页一次，1s 无操作后释放 hi 内存。</summary>
    public double ZoomLevel
    {
        get => _zoomLevel;
        set
        {
            if (SetField(ref _zoomLevel, Math.Clamp(value, 0.15, 5.0)))
            {
                OnPropertyChanged(nameof(DisplayScale));
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
            new Action(ReleaseVisibleHiToBase));
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
            if (!SetField(ref _viewportWidth, value) || !HasPdf)
                return;
            double newFit = Math.Clamp((_viewportWidth - 40.0) / Math.Max(1, _targetPageWidth), 0.15, 5.0);
            if (Math.Abs(newFit - _widthFit) < 1e-6)
                return;
            double ratio = newFit / _widthFit;
            _widthFit = newFit;
            OnPropertyChanged(nameof(DisplayScale));
            // 整层等比缩放：同一内容保持在视口内（比例精确，无重建、无占位闪动）。
            if (_lastViewportHeight > 0 && Pages.Count > 0)
                RequestScrollToOffset?.Invoke(_lastVerticalOffset * ratio);
        }
    }

    public MainViewModel()
    {
        OpenPdfCommand = new RelayCommand(OpenPdf);
        ZoomInCommand = new RelayCommand(() => ZoomBy(ZoomStepFactor));
        ZoomOutCommand = new RelayCommand(() => ZoomBy(1.0 / ZoomStepFactor));
        GoToPreviousPageCommand = new RelayCommand(GoToPreviousPage);
        GoToNextPageCommand = new RelayCommand(GoToNextPage);
        GoHomeCommand = new RelayCommand(GoHome);
        OpenSettingsCommand = new RelayCommand(() => IsSettingsVisible = true);
        CloseSettingsCommand = new RelayCommand(() => IsSettingsVisible = false);
        Settings = SettingsService.Load();
        InkTool.EraserSize = InkToolViewModel.EraserSizeFromSlider(Settings.EraserSizeSetting);   // 橡皮粗细设置 → 实际直径（15 + 3.5x），启动即生效
        InkTool.IsStrokeEraser = Settings.EraserModeByStrokeSetting;   // 橡皮模式（面积擦/墨迹擦）恢复
        // 恢复上次批注墨迹选项（LoadFromSettings：装入各类型记忆并按记忆页签类型装入活动值）
        InkTool.FitToCurve = Settings.IsFitToCurve;
        InkTool.IsUselightThemeColor = Settings.UseLightThemeColor;
        InkTool.LoadFromSettings(Settings);
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
    internal async Task OpenPdfFile(string path)
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
                _pdf.OpenDocument(path);
                var pageSizes = new PageSize[_pdf.PageCount];
                var thumbSizes = new PageSize[_pdf.PageCount];
                for (int i = 0; i < _pdf.PageCount; i++)
                    pageSizes[i] = _pdf.GetPageSize(i, targetWidth);
                for (int i = 0; i < _pdf.PageCount; i++)
                    thumbSizes[i] = _pdf.GetPageSize(i, ThumbnailWidth);

                var store = autoSave ? InkStoreService.TryOpen(path) : null;
                if (!autoSave)
                    ConsoleLog.Write("PDF打开", "墨迹恢复: 自动保存关闭，跳过哈希验证");
                var pageInks = store?.LoadPages() ?? new Dictionary<int, byte[]>();
                var boardSlots = store?.LoadBoardSlots() ?? new Dictionary<int, byte[]>();
                var viewState = store?.LoadViewState() ?? new Dictionary<string, string>();
                return (pageSizes, thumbSizes, store, pageInks, boardSlots, viewState);
            });

            // 回 UI 线程：建集合 + 后续懒加载（InkStore 只在 UI 线程赋值/读取）
            InkStore = sizes.store;
            PdfName = Path.GetFileNameWithoutExtension(path);
            HasPdf = true;
            await ReloadPages(sizes.pageSizes, sizes.thumbSizes);

            // 恢复墨迹（订阅方分帧执行：PDF 页墨迹 + 画板槽位/页签；无库时以空数据触发，把画板重置为 1/1 防上一文档残留）
            if (DocumentOpened != null)
                await DocumentOpened(new InkRestoreData(sizes.store, sizes.pageInks,
                    sizes.boardSlots, sizes.viewState));

            // 恢复显示位置：先缩放（clamp 自带），再 Loaded 时机恢复滚动（布局未跑时 offset 无效，交回退页码）
            if (sizes.viewState.TryGetValue("pdf_zoom", out var zoomStr)
                && double.TryParse(zoomStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom))
                ZoomLevel = zoom;
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!HasPdf)
                {
                    IsOpeningPdf = false;   // 文档已不在：直接撤加载页，不留死等
                    return;
                }
                double offset = 0;
                bool hasOffset = sizes.viewState.TryGetValue("pdf_offset", out var offStr)
                                 && double.TryParse(offStr, NumberStyles.Float, CultureInfo.InvariantCulture, out offset);
                int page = 1;
                if (sizes.viewState.TryGetValue("pdf_page", out var pageStr))
                    int.TryParse(pageStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out page);
                if (hasOffset && RequestRestoreScroll != null)
                    RequestRestoreScroll(offset, Math.Max(1, page));
                else if (page > 1)
                    GoToPage(page);

                // 位置恢复后再等首屏可见页位图就位，才撤加载页（成功路径唯一出口；含超时兜底）
                stopwatch.Stop();
                ConsoleLog.Write("PDF打开",
                    $"打开成功: 页数={_pdf.PageCount}, 恢复墨迹页={sizes.pageInks.Count}, 画板槽={sizes.boardSlots.Count}, 耗时={stopwatch.ElapsedMilliseconds}ms");
                BeginWaitFirstScreenReady();
            }));
        }
        catch (Exception ex)
        {
            IsOpeningPdf = false;   // 失败路径撤加载页（成功路径由首屏就绪轮询撤），再弹错误框
            ConsoleLog.Write("PDF打开", $"打开失败: {ex}");
            App.Log("OpenPdfFile", ex);
            MessageBox.Show($"PDF 打开失败：{ex.Message}", "InkCanvasForPDF",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
        _pdf.CloseDocument();
        Pages.Clear();
        Thumbnails.Clear();
        _pageTopDips = Array.Empty<double>();
        _thumbTopDips = Array.Empty<double>();
        _loadedStartIndex = _loadedEndIndex = -1;
        _thumbLoadedStartIndex = _thumbLoadedEndIndex = -1;
        _firstVisiblePageIndex = 0;
        PageNumberText = "1";
        IsThumbnailPanelVisible = false;
        IsSettingsVisible = false;
        InkTool.ClearHistory();
        PdfName = string.Empty;
        HasPdf = false;   // 触发工具栏/空态按钮 Visibility 切换
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(CurrentPage));
    }

    /// <summary>按当前视口宽度重建全部页面模型，并加载可见页（尺寸由后台线程预算好传入）。
    /// 容器拆除/填充按 8ms 时间片分批、批间让帧：非虚拟化 ItemsControl 的全量容器实现若集中在一个
    /// 布局帧会冻结 ProgressRing 动画——分批 = 每帧只实现一批，总成本不变只摊薄。</summary>
    private async Task ReloadPages(PageSize[] pageSizes, PageSize[] thumbSizes)
    {
        _isRebuildingPages = true;   // 中间态抑制 UpdateVisiblePages（Pages 与 _pageTopDips 暂不一致）
        // 文档切换：停掉旧文档的防抖/清晰/释放计时器（原来只有 GoHome 停，直开新文档会残留）
        _scrollDebounceTimer?.Stop();
        _sharpenTimer?.Stop();
        _releaseTimer?.Stop();

        _pdf.ClearCache();
        _loadedStartIndex = -1;
        _loadedEndIndex = -1;
        _thumbLoadedStartIndex = -1;
        _thumbLoadedEndIndex = -1;
        _firstVisiblePageIndex = 0;
        _targetPageWidth = Math.Max(400, _viewportWidth - 40);
        _widthFit = 1.0;   // 新文档：宽度适配复位（页面尺寸固定，宽度变化只改此系数）

        var sw = Stopwatch.StartNew();
        long lastSlice = 0;

        // ① 分批拆除旧页容器：Clear 是一个 Reset 同步拆光（重开文档卡顿源）；逐个 Remove + 片间让帧。
        //    首次打开集合为空，循环体不执行。
        while (Pages.Count > 0)
        {
            Pages.RemoveAt(Pages.Count - 1);
            if (sw.ElapsedMilliseconds - lastSlice >= RebuildPageSliceMs)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);   // 让 Render 帧跑（布局+圆圈动画）
                lastSlice = sw.ElapsedMilliseconds;
            }
        }

        Thumbnails.Clear();   // 虚拟化面板，成本低，同步即可

        for (int i = 0; i < thumbSizes.Length; i++)
        {
            Thumbnails.Add(new ThumbnailModel { PageIndex = i, Width = thumbSizes[i].Width, Height = thumbSizes[i].Height });
        }

        // 缩略图顶部 DIP 前缀和先建：页面分批期间缩略图侧状态保持完整一致（ThumbnailScrollViewer_ScrollChanged 安全）。
        _thumbTopDips = new double[Thumbnails.Count + 1];
        double acc = 0;
        for (int i = 0; i < Thumbnails.Count; i++)
        {
            _thumbTopDips[i] = acc;
            acc += Thumbnails[i].Height;
        }
        _thumbTopDips[Thumbnails.Count] = acc;

        // ② 分批填充新页容器：容器在布局帧内实现——批间让帧 = 每帧只实现一批（主修复）。
        for (int i = 0; i < pageSizes.Length; i++)
        {
            Pages.Add(new PageModel { PageIndex = i, Width = pageSizes[i].Width, Height = pageSizes[i].Height });
            if (sw.ElapsedMilliseconds - lastSlice >= RebuildPageSliceMs)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                lastSlice = sw.ElapsedMilliseconds;
            }
        }

        // 构建页面顶部 DIP 前缀和：可见页查找由 O(N) 遍历降为 O(logN) 二分（页面高度固定，缩放仅整体乘 ZoomLevel）。
        // 必须在 _isRebuildingPages=false 之前完成，保证守卫解除时状态已一致。
        _pageTopDips = new double[Pages.Count + 1];
        acc = 0;
        for (int i = 0; i < Pages.Count; i++)
        {
            _pageTopDips[i] = acc;
            acc += Pages[i].Height;
        }
        _pageTopDips[Pages.Count] = acc;

        _isRebuildingPages = false;

        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(CurrentPage));

        InkTool.ClearHistory();   // 新文档：旧命令引用的 PageModel 已销毁，清空撤销/重做栈

        UpdateThumbnailHighlight();   // 初始高亮当前页（首次 first==0 不触发 UpdateVisiblePages 的变更分支）

        if (_lastViewportHeight > 0)
            UpdateVisiblePages(_lastVerticalOffset, _lastViewportHeight);
        else
            LoadPageImages(0, Math.Min(PageBufferSize, Pages.Count - 1), needHiInViewport: false);   // 初始只补 base
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

    /// <summary>把页码输入框文本同步为当前页。</summary>
    private void SyncPageNumberText()
    {
        if (PageNumberText != CurrentPage.ToString())
            PageNumberText = CurrentPage.ToString();
    }

    /// <summary>更新缩略图当前页高亮。</summary>
    private void UpdateThumbnailHighlight()
    {
        for (int i = 0; i < Thumbnails.Count; i++)
            Thumbnails[i].IsCurrent = i == _firstVisiblePageIndex;
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

    /// <summary>按固定倍率缩放（自动夹在 0.15x~5x）。返回是否发生实际变化（clamp 边界时为 false）。</summary>
    public bool ZoomBy(double factor)
    {
        double next = Math.Clamp(_zoomLevel * factor, 0.15, 5.0);
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
        // 渲染宽度乘 DPI 系数：高 DPI 屏物理像素充足，缩略图不发糊（仍为低分辨率小图，内存受控）。
        int thumbW = (int)Math.Round(ThumbnailWidth * Math.Max(1.0, _dpiScale));
        for (int i = startIndex; i <= endIndex; i++)
        {
            var thumb = Thumbnails[i];
            if (thumb.Image != null || thumb.IsLoading)
                continue;
            thumb.IsLoading = true;
            int index = i;
            _pdf.RenderThumbnailAsync(index, thumbW).ContinueWith(t =>
            {
                var img = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    thumb.IsLoading = false;
                    // ReloadPages 可能已重建集合：仅当模型仍在集合中才更新（旧对象更新无意义）。
                    if (img != null && index < Thumbnails.Count && ReferenceEquals(Thumbnails[index], thumb))
                        thumb.Image = img;
                }));
            });
        }

        _thumbLoadedStartIndex = startIndex;
        _thumbLoadedEndIndex = endIndex;
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
                    Pages[i].Image = null;
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
    /// 完成回调的期望宽度重算兜底（尺寸过期自动按当前期望重发），此处直接跳过。</summary>
    private void StartPageRender(int index, bool wantHi)
    {
        if (index < 0 || index >= Pages.Count || _pendingPageRenders.Contains(index))
            return;
        var page = Pages[index];
        _pendingPageRenders.Add(index);
        Task<ImageSource> task = wantHi
            ? _pdf.RenderPageAsync(index, GetRenderWidth(page.Width), page.Width, page.Height)
            : _pdf.RenderPageBaseAsync(index, GetBaseWidth(page.Width), page.Width, page.Height);
        task.ContinueWith(t => Application.Current?.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => CompletePageRender(index, page, t))));
    }

    /// <summary>后台渲染完成回填（UI 线程，Background 优先级——不与输入/湿墨抢帧）：
    /// 校验页模型仍有效后按当前期望宽度决定：赋值 / 丢弃（尺寸过期且已滚出缓冲）/ 按当前期望重发。</summary>
    private void CompletePageRender(int index, PageModel page, Task<ImageSource> task)
    {
        _pendingPageRenders.Remove(index);
        if (index >= Pages.Count || !ReferenceEquals(Pages[index], page))
            return;   // 文档已重建，丢弃
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
