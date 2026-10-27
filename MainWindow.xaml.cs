using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using InkCanvasForPDF.Board;
using InkCanvasForPDF.Board.Helpers;
using InkCanvasForPDF.Controls;
using InkCanvasForPDF.Models;
using InkCanvasForPDF.Services;
using InkCanvasForPDF.ViewModels;

namespace InkCanvasForPDF;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private ScrollViewer? _pdfScrollViewer;
    private ScrollViewer? _thumbScrollViewer;
    private bool _pendingThumbnailScrollToCurrent;   // 面板打开后待滚到当前页（布局完成消费一次）
    private bool _thumbnailClosing;                  // 缩略图面板滑出动画进行中标志
    private bool _isTouchZooming;
    // 双指手势类型锁存（改动A）：用累计量判定 pan/zoom 并锁存一次手势，防单帧抖动把平移误判成缩放而掐掉平移惯性
    private enum TouchGestureKind { Undecided, Pan, Zoom }
    private TouchGestureKind _touchGesture = TouchGestureKind.Undecided;
    // 触摸计数 + 墨迹抑制（改动B）：非鼠标状态（IsInkMode）下第二指落下即停墨，让双指走平移/缩放
    private readonly HashSet<int> _pdfActiveTouchIds = new();   // PDF 视图当前按下的触摸 id
    private bool _inkGestureSuppressed;                          // 双指手势期间已压低墨迹
    // 触摸平移/缩放全部由本类接管（PanningMode=None）：ScrollViewer 内置平移不看手指数，单指会抢书写，
    // 且换指瞬间质心跳变会把一帧大位移一次性吃进去 → 视图突跳；改为按手指数门控 + 实时质心锚点。
    private readonly Dictionary<int, Point> _pdfTouchPoints = new();   // 活动手指在 PagesScrollViewer 坐标系的位置（算实时质心）
    private bool _pdfTouchSetChanged;   // 本帧手指集合发生变化（加/抬指）→ 跳过一帧，防质心/平移量突变跳视图
    private bool _pdfPanAllowed;        // 本手势已由本类接管平移（供惯性判定；手指全抬后计数已清零，必须另存）
    private const double PanInertiaDeceleration = 0.00096;   // 双指平移甩动惯性减速度（DIP/ms²，WPF 惯例值 10×96/1e6：约 1 DIP/ms 初速 ~1s 停下）
    private readonly Dictionary<InkCanvas, Binding> _suppressedEditingBindings = new();   // 抑制前保存的 EditingMode 绑定（恢复时接回）
    private readonly Dictionary<InkCanvas, int> _suppressedStrokeBase = new();   // 手势抑制开始时的笔画基线（恢复时删掉之后新增的）
    private bool _pendingInkRestore;          // 手势结束后的恢复已排队（防重复调度）
    private bool _wasHidden;          // 窗口曾最小化：恢复前台时用多帧 Opacity 遮罩掩盖位图纹理重传白闪
    private EventHandler? _restoreFrameHandler;   // 多帧遮罩的渲染帧订阅
    private DispatcherTimer? _restoreMaskTimer;   // 兜底：防帧事件异常导致窗口长期透明
    private const int RestoreMaskFrames = 10;       // 约 165ms（60fps）：渲染目标重建+全量重测+纹理重传
    private const int RestoreMaskTimeoutMs = 400;   // 兜底时限：超过则强制恢复可见
    private const int InkRestoreSliceMs = 8;        // 墨迹恢复时间片：每片让帧一次，圆圈动画不冻结

    // ══════════ 墨迹批注：笔画跟踪 + 面积擦批处理（撤销/提交逐行照抄画板 MW_TimeMachine / MW_BoardCore）══════════
    private enum CommitReason
    {
        UserInput,
        CodeInput,
        ShapeDrawing,   // 形状手绘临时笔画（ICA MW_TimeMachine 同名门：预览擦写不入撤销，抬笔定型后统一提交）
        ShapeRecognition   // 墨迹纠正替换（ICA 同名门：移除手绘 + 加回生成笔画合成一条 ShapeRecognition 历史）
    }

    private CommitReason _currentCommitType = CommitReason.UserInput;
    private StrokeCollection? ReplacedStroke;       // 面积擦原笔画累加器（画板同名字段等价）
    private StrokeCollection? AddedStroke;          // 面积擦拆分笔画累加器
    private StrokeCollection? _eraseTargetStrokes;  // 本批所属页集合
    private readonly Dictionary<StrokeCollection, int> _trackedStrokePages = new();
    private readonly List<InkCanvas> _pageInkCanvases = new();                              // 页面画布（切橡皮时统一设置 EraserShape，保证黑板擦指针首显）
    private readonly HashSet<InkCanvas> _palmEraseCanvases = new();                          // 正在做手掌擦除的页面画布（面积擦历史需走平铺累加器，抬手统一恢复绑定）
    private readonly Dictionary<InkCanvas, PauseInkToShapeTracker> _pauseTrackers = new();   // 停顿强制识别追踪器（每页画布一个；Unloaded 时释放）
    private readonly Dictionary<PageInkCanvas, Action<Stroke, int>> _selfDrawHandlers = new();   // 时间笔锋自绘笔画提交处理程序（Unloaded 时退订）
    // ══ 墨迹持久化：标脏 + 300ms 去抖（D2/D3/D4）══
    private readonly HashSet<int> _dirtyPdfPages = new();                 // 待写 PDF 页号（0-based）
    private bool _dirtyBoardInk;                                          // 画板画布待写
    private readonly Dictionary<Stroke, int> _strokePage = new();         // 笔画→页（点变换/属性改标脏用）
    private DispatcherTimer? _inkSaveTimer;
    private InkCircleCursor? _inkCursor;                                                    // 自定义墨迹光标（橡皮=黑板擦 / 批注笔尖=圆点，消除"小一圈"）
    private double _inkCursorDiameter;                                                      // 当前光标宽（屏幕 DIP），变化 ≥1 才重建
    private Color _inkCursorColor;                                                          // 当前光标颜色（缓存键之一：换色/荧光笔 alpha 变化立即重建）
    private bool _inkCursorIsEraser;                                                        // 当前光标是否橡皮样式（缓存键之一）

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        // 窗口失去焦点：关闭 PDF 墨迹/图形菜单与画板墨迹/图形菜单
        Deactivated += (_, _) =>
        {
            InkBar.CloseMenusOnDeactivate();
            Board.CloseMenusOnDeactivate();
        };
        // 墨迹纠正引擎（识别后端 WinRT）。字段初始化器不能引用实例字段，故在构造函数内装配。
        _inkToShape = new InkToShapeEngine(() => _viewModel.InkTool.DrawingAttributes, ApplyShapeReplacement);
        // 设置覆盖层"浮动工具栏显示文字注释"变更 → 同步浮动栏文字；初始也按已保存设置应用一次。
        InkBar.Loaded += (_, _) => InkBar.ApplyFloatBarText(_viewModel.Settings.IsEnableDisPlayFloatBarText);
        SettingsView.FloatBarTextChanged += show => InkBar.ApplyFloatBarText(show);
        // 全应用颜色主题（抄 ICA MW_AutoTheme）：启动应用 + 跟随系统监听；画板随动换 Light/Dark-Board 字典。
        Services.ThemeService.ThemeChanged += () => Board.SetBoardTheme(Services.ThemeService.IsDark);
        Services.ThemeService.Initialize(_viewModel.Settings.Theme, this);
        Services.ThemeService.ApplyAccent(_viewModel.Settings.AccentColor);   // 强调色随启动恢复（设置页改色即时生效）
        // 墨迹栏布局设置（缩放/边距/背景色）：启动应用一次，设置页变更（BarLayoutChanged）实时重放。
        SettingsView.BarLayoutChanged += ApplyBarLayouts;
        ApplyBarLayouts();
        // 显示画笔光标：同步黑板开关（黑板原读独立 ICA Settings.json 导致开关无效）；PDF 侧走 InkTool 绑定即时生效。
        SettingsView.PenCursorChanged += show =>
        {
            Board.PenCursorEnabled = show;
            Board.ApplyPenCursor();
        };
        Board.PenCursorEnabled = _viewModel.Settings.IsShowCursor;
        Board.ApplyPenCursor();
        // 墨迹平滑：同步黑板 FitToCurve（黑板原从不读取该设置导致平滑无效）；PDF 侧走 InkTool 绑定即时生效。
        SettingsView.InkSmoothChanged += show => Board.ApplyFitToCurve(show);
        Board.ApplyFitToCurve(_viewModel.Settings.IsFitToCurve);
        // 压感不再有设置项：PDF 侧跟随墨迹菜单软/硬笔（InkTool.PressureEnabled 计算属性），
        // 画板压感由画板墨迹菜单自管（EnterBoard/页签切换时设置 _pressureEnabled）。
        SettingsView.TimePenGrowMsChanged += _ =>   // 长满时间即时同步 PDF 页面控制器与画板
        {
            RefreshTimePressureArmed();
            Board.ApplyTimePenGrowMs(_viewModel.Settings.TimePenGrowMs);
        };
        Board.ApplyTimePenGrowMs(_viewModel.Settings.TimePenGrowMs);   // 启动即同步一次
        SettingsView.HighlighterPressureChanged += enabled =>          // 荧光笔压力感应即时同步 PDF 与画板
        {
            RefreshTimePressureArmed();
            Board.ApplyHighlighterPressureEnabled(enabled);
        };
        Board.ApplyHighlighterPressureEnabled(_viewModel.Settings.HighlighterPressureEnabled);   // 启动即同步一次
        SettingsView.PenStyleChanged += style =>   // 笔锋样式全局唯一：设置页改 → 画板即时跟随
        {
            BoardView.Settings.Canvas.InkStyle = style;
            Board.RefreshTimePressureArmed();
            BoardView.SaveSettingsToFile();
        };
        // 启动即同步一次：PDF 设置为笔锋唯一来源，画板跟随（笔锋下拉框已从两处墨迹菜单移除）
        BoardView.Settings.Canvas.InkStyle = _viewModel.Settings.InkStyle;
        Board.RefreshTimePressureArmed();
        // 墨迹识别灵敏度：滑块百分比 = 规范图形与原墨迹的相似度门槛（越大越严格），PDF/画板两侧引擎同步。
        SettingsView.InkToShapeSensitivityChanged += value =>
        {
            double threshold = value / 100.0;
            _inkToShape.SimilarityThreshold = threshold;
            Board.ApplyInkToShapeSensitivity(threshold);
        };
        double inkToShapeThreshold = _viewModel.Settings.InkToShapeSensitivity / 100.0;   // 启动即同步一次
        _inkToShape.SimilarityThreshold = inkToShapeThreshold;
        Board.ApplyInkToShapeSensitivity(inkToShapeThreshold);
        // 停顿强制识别（独立开关，不受墨迹纠正总开关影响）：更新 PDF 引擎门槛 + 重推画板；
        // PDF 侧追踪器经活值 lambda 读设置，无需额外推送。
        SettingsView.PauseInkToShapeChanged += () =>
        {
            _inkToShape.PauseSimilarityThreshold = _viewModel.Settings.PauseInkToShapeSensitivity / 100.0;
            PushPauseInkToShapeToBoard();
        };
        _inkToShape.PauseSimilarityThreshold = _viewModel.Settings.PauseInkToShapeSensitivity / 100.0;   // 启动即同步一次
        PushPauseInkToShapeToBoard();
        _viewModel.RequestScrollToPage = ScrollToPage;
        // 撤销回放（画板 MW_TimeMachine.ApplyHistoryToCanvas 逐行等价：UserInput/Clear 两类，
        // 直改条目 TargetStrokes——发生页的笔画集合实例；首尾设置/还原 CodeInput 门）
        _viewModel.InkTool.ApplyHistoryToCanvas = item =>
        {
            _currentCommitType = CommitReason.CodeInput;
            try
            {
                var strokes = item.TargetStrokes;
                if (strokes == null)
                    return;
                if (item.CommitType == TimeMachineHistoryType.UserInput)
                {
                    if (!item.StrokeHasBeenCleared)
                    {
                        foreach (var s in item.CurrentStroke)
                            if (!strokes.Contains(s)) strokes.Add(s);
                    }
                    else
                    {
                        foreach (var s in item.CurrentStroke)
                            if (strokes.Contains(s)) strokes.Remove(s);
                    }
                }
                else if (item.CommitType == TimeMachineHistoryType.Clear)
                {
                    if (!item.StrokeHasBeenCleared)
                    {
                        // 撤销面积擦：加回原笔（ReplacedStroke）、移除拆分（CurrentStroke）
                        if (item.CurrentStroke != null)
                            foreach (var s in item.CurrentStroke)
                                if (!strokes.Contains(s)) strokes.Add(s);
                        if (item.ReplacedStroke != null)
                            foreach (var s in item.ReplacedStroke)
                                if (strokes.Contains(s)) strokes.Remove(s);
                    }
                    else
                    {
                        // 重做面积擦：移除原笔、加回拆分
                        if (item.ReplacedStroke != null)
                            foreach (var s in item.ReplacedStroke)
                                if (!strokes.Contains(s)) strokes.Add(s);
                        if (item.CurrentStroke != null)
                            foreach (var s in item.CurrentStroke)
                                if (strokes.Contains(s)) strokes.Remove(s);
                    }
                }
                else if (item.CommitType == TimeMachineHistoryType.ShapeRecognition)
                {
                    // 墨迹纠正（画板 ApplyHistoryToCanvas 同名分支等价）：撤销=移除生成图形、加回手绘；重做反向
                    if (item.StrokeHasBeenCleared)
                    {
                        foreach (var s in item.CurrentStroke)
                            if (strokes.Contains(s)) strokes.Remove(s);
                        if (item.ReplacedStroke != null)
                            foreach (var s in item.ReplacedStroke)
                                if (!strokes.Contains(s)) strokes.Add(s);
                    }
                    else
                    {
                        foreach (var s in item.CurrentStroke)
                            if (!strokes.Contains(s)) strokes.Add(s);
                        if (item.ReplacedStroke != null)
                            foreach (var s in item.ReplacedStroke)
                                if (strokes.Contains(s)) strokes.Remove(s);
                    }
                }
                else if (item.CommitType == TimeMachineHistoryType.Manipulation)
                {
                    // 选择变换（画板 ApplyHistoryToCanvas Manipulation 分支等价）：撤销=回原始点集、重做=变换后点集。
                    // 条目不带 TargetStrokes，按笔画反查所属页集合（笔画只存在于一个页集合）
                    foreach (var currentStroke in item.StylusPointDictionary)
                    {
                        var target = ResolveStrokeCollection(currentStroke.Key);
                        if (target == null || !target.Contains(currentStroke.Key))
                            continue;
                        currentStroke.Key.StylusPoints = item.StrokeHasBeenCleared
                            ? currentStroke.Value.Item1
                            : currentStroke.Value.Item2;
                    }
                }
                else if (item.CommitType == TimeMachineHistoryType.DrawingAttributes)
                {
                    // 粗细/改色（画板 ApplyHistoryToCanvas DrawingAttributes 分支等价）：撤销=旧属性、重做=新属性
                    foreach (var currentStroke in item.DrawingAttributes)
                    {
                        var target = ResolveStrokeCollection(currentStroke.Key);
                        if (target == null || !target.Contains(currentStroke.Key))
                            continue;
                        currentStroke.Key.DrawingAttributes = item.StrokeHasBeenCleared
                            ? currentStroke.Value.Item1
                            : currentStroke.Value.Item2;
                    }
                }
            }
            finally
            {
                _currentCommitType = CommitReason.UserInput;
            }
        };
        _viewModel.RequestScrollToOffset = offset =>
        {
            if (_pdfScrollViewer == null)
                return;
            // 同帧完成新布局再滚动：配合 VM 同步调用，避免宽度重建后异步恢复导致的位置跳变闪烁。
            _pdfScrollViewer.UpdateLayout();
            ScrollToOffsetClamped(_pdfScrollViewer, offset);
        };
        // 墨迹持久化接线（回调每次执行时现取 InkStore，库切换后自动指向新库，无需重挂）
        _viewModel.DocumentClosing += FlushInkStore;
        _viewModel.DocumentOpened += RestoreInkData;
        _viewModel.RequestRestoreScroll = (offset, page) =>
        {
            if (_pdfScrollViewer == null) return;
            _pdfScrollViewer.UpdateLayout();
            double max = _pdfScrollViewer.ExtentHeight - _pdfScrollViewer.ViewportHeight;
            if (offset > 0 && offset <= max + 2) ScrollToOffsetClamped(_pdfScrollViewer, offset);
            else ScrollToPage(page);   // 跨窗口尺寸 offset 失效 → 回退页码
        };
        Board.PersistSink = (slot, strokes) => _viewModel.InkStore?.SaveBoardSlot(slot, strokes);   // D2：同步序列化，引用直传
        Board.BoardSlotInserted = newCurrent => _viewModel.InkStore?.ShiftBoardSlots(newCurrent, +1);   // 中间加页库侧让位
        Board.BoardSlotDeleted = removed => _viewModel.InkStore?.DeleteBoardSlot(removed, removed + 1); // 删页库侧行删除+位移
        Board.InkChanged = () =>
        {
            if (_viewModel.InkStore != null)
            {
                _dirtyBoardInk = true;
                RestartInkSaveTimer();
            }
        };
        // 触摸手势（对齐 KillerPDF）：handledEventsToo=true——祖先/内建元素可能先标记 Manipulation
        // 事件已处理，普通订阅会被吞掉。ScrollViewer 内置触摸平移已关（PanningMode=None），
        // 平移/缩放/惯性全部在下面这套处理器里按手指数门控自行实现。
        PagesScrollViewer.AddHandler(ManipulationStartingEvent,
            new EventHandler<ManipulationStartingEventArgs>(PagesControl_ManipulationStarting), true);
        PagesScrollViewer.AddHandler(ManipulationDeltaEvent,
            new EventHandler<ManipulationDeltaEventArgs>(PagesControl_ManipulationDelta), true);
        PagesScrollViewer.AddHandler(ManipulationInertiaStartingEvent,
            new EventHandler<ManipulationInertiaStartingEventArgs>(PagesControl_ManipulationInertiaStarting), true);
        PagesScrollViewer.AddHandler(ManipulationCompletedEvent,
            new EventHandler<ManipulationCompletedEventArgs>(PagesControl_ManipulationCompleted), true);
        // 触摸计数（Preview 隧道必经 ScrollViewer，InkCanvas 捕获触摸时也能收到；handledEventsToo 防被吞）：
        // 非鼠标状态第二指落下 → 停墨进入双指手势，抬完所有指恢复（改动B）。
        // Move 同挂一条：记录各指实时位置，缩放锚点取当前多指质心（而非手势起点 ManipulationOrigin）。
        PagesScrollViewer.AddHandler(UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(PdfView_PreviewTouchDown), true);
        PagesScrollViewer.AddHandler(UIElement.PreviewTouchMoveEvent,
            new EventHandler<TouchEventArgs>(PdfView_PreviewTouchMove), true);
        PagesScrollViewer.AddHandler(UIElement.PreviewTouchUpEvent,
            new EventHandler<TouchEventArgs>(PdfView_PreviewTouchUp), true);
        // 缩放（整层 ScaleTransform）变化时同步给墨迹工具：画笔粗细按 DisplayScale 抵消，保证"粗细"设几屏幕就是几。
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.DisplayScale))
            {
                _viewModel.InkTool.DisplayScale = _viewModel.DisplayScale;
                UpdateInkCursor();   // 光标直径 = 实际粗细 × DisplayScale，缩放/缩略图面板开合时实时跟随
                UpdateEraserShapes();   // 缩放变化时 EraserShape 页面坐标需反向补偿，保持屏幕擦除洞恒定
                RefreshTimePressureArmed();   // 时间笔锋针尖宽度按 DisplayScale 换算，缩放时同步
                _inkToShape.DisplayScale = _viewModel.DisplayScale;   // 墨迹纠正尺寸阈值随缩放换算
            }
            else if (e.PropertyName == nameof(MainViewModel.IsSettingsVisible))
            {
                SlideOverlay(SettingsView, _viewModel.IsSettingsVisible);   // 设置页从右滑入/向左滑出（与画板同款）
            }
            else if (e.PropertyName == nameof(MainViewModel.IsOpeningPdf))
            {
                ApplyOpeningBackdrop();   // 加载页全透明：切换下方内容显隐以呈现纯云母
            }
        };
        _viewModel.InkTool.DisplayScale = _viewModel.DisplayScale;   // 初始同步
        _inkToShape.DisplayScale = _viewModel.DisplayScale;          // 墨迹纠正初始同步
        // 墨迹纠正全局开关：单一真源 = Data\Settings.json（AppSettings.IsInkToShapeEnabled）。
        // 画板墨迹面板的开关改动经事件回流到 VM，再经 ApplyInkToShapeEnabled 推回画板（同值赋值不回环）。
        Board.InkToShapeChanged += enabled =>
        {
            _viewModel.InkTool.IsInkToShapeEnabled = enabled;
            _viewModel.Settings.IsInkToShapeEnabled = enabled;
            SettingsService.Save(_viewModel.Settings);
        };
        ApplyInkToShapeEnabled(_viewModel.InkTool.IsInkToShapeEnabled);   // 启动即同步一次
        // 画板停顿纠正开关 → 回写主设置并推回画板（配置由 PushPauseInkToShapeToBoard 统一推送；
        // 启动回填走构造处已有的 PushPauseInkToShapeToBoard → ApplyPauseInkToShape 的 UI 镜像）
        Board.PauseInkToShapeChanged += enabled =>
        {
            _viewModel.Settings.IsPauseInkToShapeEnabled = enabled;
            SettingsService.Save(_viewModel.Settings);
            PushPauseInkToShapeToBoard();
        };
        // 打开新文档（Pages 重建 Reset）时解除旧页 Strokes 订阅并清批处理。
        _viewModel.Pages.CollectionChanged += Pages_CollectionChanged;
        // 切到橡皮的瞬间统一设置所有页面画布的 EraserShape，保证黑板擦指针首显正确。
        _viewModel.InkTool.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InkToolViewModel.ActiveTool))
            {
                if (_viewModel.InkTool.ActiveTool == InkToolMode.Eraser)
                    UpdateEraserShapes();
                // 选择工具手势守卫（ICA BtnSelect_Click 语义）：选择模式关闭页面画布 Manipulation，防双指手势打架；
                // 切出时恢复 Manipulation 并清空选区/隐藏选择 UI（ICA 切工具即退出选择）
                if (_viewModel.InkTool.ActiveTool == InkToolMode.Select)
                {
                    foreach (var pageCanvas in _pageInkCanvases)
                        pageCanvas.IsManipulationEnabled = false;
                    // 页面外层的 ScrollViewer 也在做触摸 Manipulation：单指触摸会被它捕获，
                    // InkCanvas 内建套索拿不到输入（画板没有这层父级，所以画板正常）→ 选择期间一并关闭
                    PagesScrollViewer.IsManipulationEnabled = false;
                    SyncPageEditingModes();   // 进入选择也确定性同步（不依赖绑定时序）
                }
                else
                {
                    ClearPdfSelection();
                    foreach (var pageCanvas in _pageInkCanvases)
                        pageCanvas.IsManipulationEnabled = true;
                    PagesScrollViewer.IsManipulationEnabled = true;
                    SyncPageEditingModes();   // 退出选择确定性离开 Select（不依赖绑定时序）
                }
            }
            else if (e.PropertyName == nameof(InkToolViewModel.EraserSize))
            {
                UpdateEraserShapes();
                // 画板面积擦 EraserShape 随滑块同步（先更新直径再应用，保证读到新值；光标由下方 ApplyEraserCursor 刷）
                BoardView.EraserDiameter = _viewModel.InkTool.EraserSize;
                Board.ApplyEraserShape();
            }
            // 选形状/退出形状时确定性同步所有页面画布 EditingMode（不依赖绑定时序，防个别页脱钩残留）
            else if (e.PropertyName == nameof(InkToolViewModel.ShapeDrawMode))
                SyncPageEditingModes();
            // 墨迹纠正全局开关：PDF 引擎 + 画板墨迹面板镜像（画板开关改动也经此路径回流）
            else if (e.PropertyName == nameof(InkToolViewModel.IsInkToShapeEnabled))
                ApplyInkToShapeEnabled(_viewModel.InkTool.IsInkToShapeEnabled);
            // 擦除模式变更（面积擦/墨迹擦）兜底同步 EraserShape：PDF Pivot 已"先形状后模式"预设（幂等），
            // 此分支覆盖画板侧改共享 IsStrokeEraser 与启动恢复等路径（画板文件不动）
            else if (e.PropertyName == nameof(InkToolViewModel.EditingMode))
            {
                UpdateEraserShapes();
                // 确定性同步页面画布 EditingMode（不依赖绑定时序/完整性：个别页绑定被摘后脱钩时也能立即生效）
                SyncPageEditingModes();
            }
            // 工具切换 / 橡皮粗细 / 画笔粗细 / 画笔光标开关（ShowPenCursor 变更会通知 ForceCursor）变化时刷新自定义光标。
            UpdateInkCursor();
            // 画板橡皮直径跟随 InkCanvasForPDF 设置（与 PDF 批注侧同一来源，保证两侧粗细一致）。
            BoardView.EraserDiameter = _viewModel.InkTool.EraserSize;
            Board.ApplyEraserCursor();   // 橡皮粗细变化时画板黑板擦光标同步重建
            RefreshTimePressureArmed();   // 笔锋样式/工具/颜色变化时重算时间笔锋武装状态
        };
        BoardView.EraserDiameter = _viewModel.InkTool.EraserSize;   // 启动即同步一次
        // 最小化/恢复：StateChanged 必定触发（IsVisibleChanged 对最小化不可靠）。
        // 最小化时暂停渲染/释放定时器；恢复前台用多帧 Opacity 遮罩：让 WPF 渲染目标重建
        // 与全部位图纹理重传发生在不可见帧，随后一次完整显示，掩盖"所有 PDF 位图闪白"。
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                _wasHidden = true;
                _viewModel.OnWindowVisibilityChanged(false);
            }
            else if (_wasHidden)
            {
                _wasHidden = false;
                BeginRestoreMask();
                _viewModel.OnWindowVisibilityChanged(true);
            }
        };
        // 画板（移植 ICA）：入口在墨迹浮动栏最右侧，画板内"退出"按钮请求退出
        InkBar.BoardRequested += (_, _) => SetBoardMode(true);
        Board.ExitRequested += (_, _) => SetBoardMode(false);
        // 设置页首次布局预热（NavigationView 模板化代价大，避免第一次打开设置滑入动画被吃掉）
        Loaded += (_, _) => WarmUpSettingsView();
        // 关联双击启动：构造期（窗口显示前）先立起加载页，首帧直接呈现"正在打开"，
        // 欢迎空态页（HasPdf=false 可见）不露出一帧；OpenPdfFile 内同值重设幂等。
        if (App.StartupFilePath != null)
            _viewModel.IsOpeningPdf = true;
        // 启动参数带入的 PDF：等首帧布局完成（ViewportWidth 已知，宽度适配才准）再打开；
        // 先摘路径保证只消费一次（Loaded 可能因可见性变化再次触发）
        Loaded += (_, _) =>
        {
            var path = App.StartupFilePath;
            App.StartupFilePath = null;
            if (path != null)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                    new Action(() => _ = _viewModel.OpenPdfFile(path)));
        };
        // 应用更新启动流程（仅一次）：标记自清理 → 状态初始化 → 自动检查并下载 → 更新后首启弹更新日志
        Loaded += (_, _) =>
        {
            if (_updateStartupDone)
                return;
            _updateStartupDone = true;
            RunUpdateStartup();
        };
        // 启动自动清理超期墨迹数据（仅一次）：窗口出现后转后台线程执行，不阻塞启动
        Loaded += (_, _) =>
        {
            if (_cleanStartupDone)
                return;
            _cleanStartupDone = true;
            StartAutoCleanInkBackground();
        };
    }

    private bool _cleanStartupDone;   // 启动清理只跑一次（Loaded 可能因可见性变化再次触发）

    /// <summary>启动自动清理超期墨迹数据（"数据存储"设置，开关关则跳过）：
    /// 窗口出现后在后台线程执行，不阻塞启动；明细日志由 CleanExpired 内部
    /// [自动删除墨迹数据] 输出，此处异常记 error.log。</summary>
    private void StartAutoCleanInkBackground()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var s = SettingsService.Load();
                if (s.IsAutoCleanInk)
                    InkStoreService.CleanExpired(s.AutoCleanInkDays);
            }
            catch (Exception ex)
            {
                App.Log("Startup.CleanInk", ex);
            }
        });
    }

    private bool _updateStartupDone;   // 更新启动流程只跑一次（Loaded 可能因可见性变化再次触发）

    /// <summary>加载页全透明呈现纯云母：打开 PDF 期间把下方内容（阅读区/缩略图/欢迎页、墨迹栏、
    /// 选择工具栏）Opacity 置 0——用 Opacity 不动布局，避免 SizeChanged 重排与空白帧问题；
    /// 顶栏由 IsToolbarVisible（已加 !IsOpeningPdf）负责。恢复时原样显示。</summary>
    private void ApplyOpeningBackdrop()
    {
        double o = _viewModel.IsOpeningPdf ? 0 : 1;
        ContentAreaGrid.Opacity = o;
        InkBar.Opacity = o;
        BorderStrokeSelectionControl.Opacity = o;
    }

    /// <summary>启动更新流程：清理已装完的下载标记 → 初始化状态机 → 开启自动检查时后台检查并下载
    /// （失败静默记日志，仅手动检查弹通知）→ 更新完成后首次启动弹更新日志。</summary>
    private void RunUpdateStartup()
    {
        var s = _viewModel.Settings;
        UpdateService.CleanupInstalledMarker(s);
        UpdateService.Initialize(s);
        ConsoleLog.Write("应用更新", $"启动流程: 自动检查={s.IsAutoCheckUpdate}, 初始状态={UpdateService.State}");
        if (s.IsAutoCheckUpdate)
            _ = UpdateService.RunCheckAndDownloadAsync(s, manual: false, ShowFloatingTip);
        _ = ShowChangelogAfterUpdateAsync(s);
    }

    /// <summary>更新完成后首次启动：首次使用（设置文件不存在）不弹直接记为已展示；
    /// 其余情况当前版本与已展示日志版本不同且存在对应 Release 时，
    /// 拉取正文（Markdown）用 iuwm ContentDialog + MdXaml 展示并记为已展示；
    /// 无匹配 Release（Dev 版）/网络失败静默记日志。</summary>
    private async Task ShowChangelogAfterUpdateAsync(Models.AppSettings s)
    {
        var current = UpdateService.CurrentVersion;
        ConsoleLog.Write("应用更新", $"更新日志: 当前={current}, 已展示={s.ShownChangelogVersion}");
        if (!SettingsService.LoadedFromFile)
        {
            // 首次使用（非更新后）：不弹，直接记为已展示
            ConsoleLog.Write("应用更新", $"更新日志: 首次使用, 不弹, 版本={current}");
            s.ShownChangelogVersion = current;
            SettingsService.Save(s);
            return;
        }
        if (s.ShownChangelogVersion == current)
            return;
        try
        {
            var notes = await UpdateService.GetReleaseNotesAsync(s, current);
            if (notes == null)
            {
                ConsoleLog.Write("应用更新", "更新日志: 无对应Release, 不弹");
                return;   // 当前版本无对应 Release：不弹
            }
            ConsoleLog.Write("应用更新", $"更新日志: 弹出, 版本={current}");
            // MarkdownScrollViewer 自带滚动，直接作为弹窗内容
            var body = new MdXaml.MarkdownScrollViewer
            {
                Markdown = string.IsNullOrWhiteSpace(notes) ? "当前版本只有滚木日志😁" : notes.Trim(),
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            body.SetResourceReference(MdXaml.MarkdownScrollViewer.ForegroundProperty, "ToolbarForegroundBrush");
            var dialog = new iNKORE.UI.WPF.Modern.Controls.ContentDialog
            {
                Title = $"更新日志（v{current}）",
                Content = body,
                CloseButtonText = "关闭",
                Owner = this,
            };
            // 关闭按钮用强调样式（与设置页「查看更新日志」弹窗一致）
            dialog.SetResourceReference(iNKORE.UI.WPF.Modern.Controls.ContentDialog.CloseButtonStyleProperty,
                iNKORE.UI.WPF.Modern.ThemeKeys.AccentButtonStyleKey);
            await dialog.ShowAsync();

            s.ShownChangelogVersion = current;
            SettingsService.Save(s);
        }
        catch (Exception ex)
        {
            App.Log("Update.Changelog", ex);   // 失败不弹，下次启动重试
        }
    }

    // ══════════ 画板模式（移植 Ink-Canvas-Artistry 4.9.4）══════════

    private bool _isBoardMode;

    // ══════════ 墨迹栏布局设置（v0.13 移植 ICA：缩放/边距/背景色）══════════

    /// <summary>按设置应用 PDF 墨迹栏与黑板墨迹栏布局：Margin（右/底边距）+ ScaleTransform（缩放）+ 背景色/透明度。</summary>
    private void ApplyBarLayouts()
    {
        var s = _viewModel.Settings;
        InkBar.ViewboxFloatingBarScaleTransform.ScaleX = s.FloatingBarScale;
        InkBar.ViewboxFloatingBarScaleTransform.ScaleY = s.FloatingBarScale;
        InkBar.ApplyPlacement(s.FloatingBarRightMargin, s.FloatingBarBottomMargin);
        InkBar.ApplyBarBackground(s.IsColorfulViewboxFloatingBar, s.FloatingBarOpacity);
        Board.ApplyBoardBarLayout(s.BlackboardBarScale, s.BlackboardBarBottomMargin, s.BlackboardBarOpacity);
    }

    /// <summary>进入/退出画板：BoardView 顶到标题栏覆盖全窗口（墨迹栏被覆盖不可见），滑入/滑出动画。
    /// 退出时存档/清屏（ExitBoard）延后到滑出后执行，避免重活抢动画帧。</summary>
    private void SetBoardMode(bool enter)
    {
        if (_isBoardMode == enter) return;
        _isBoardMode = enter;
        if (enter)
        {
            Board.EnterBoard();
            SlideOverlay(Board, true);
        }
        else
        {
            // 滑出动画播完后再收黑板底/存档（ExitBoard 会收起 GridBackgroundCover，
            // 提前执行会导致滑出过程中黑板背景消失透出 PDF）
            SlideOverlay(Board, false, completed: Board.ExitBoard);
        }
    }

    /// <summary>整页覆盖层滑入/滑出：进入从右往左（width→0），退出从右往左（0→-width，向左滑出屏幕）。
    /// 宽度用窗口 ActualWidth 兜底——覆盖层初始 Collapsed 从未布局时 ActualWidth=0，
    /// 会导致 from=to=0 动画无效（画板"没动画"的根因）。200ms CircleEase，Fluent 滑入规范。
    /// 滑入前先 UpdateLayout 强制完成首次布局/模板化（NavigationView 等重活），否则会吃掉动画帧导致首开无动画。</summary>
    private void SlideOverlay(FrameworkElement overlay, bool slideIn, Action? completed = null)
    {
        double width = overlay.ActualWidth;
        if (width < 1)
            width = ActualWidth;                     // 未布局过（Collapsed）：用窗口宽度兜底
        double from = slideIn ? width : 0;
        double to = slideIn ? 0 : -width;            // 退出：从右往左（向左滑出屏幕）
        var tt = new TranslateTransform(from, 0);
        overlay.RenderTransform = tt;
        overlay.Visibility = Visibility.Visible;
        if (slideIn && overlay.ActualWidth < 1)
            overlay.UpdateLayout();                  // 首次显示：同步完成布局/模板化，重活不落在动画帧里
        var anim = new System.Windows.Media.Animation.DoubleAnimation(from, to, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) =>
        {
            if (!slideIn)
                overlay.Visibility = Visibility.Collapsed;
            overlay.RenderTransform = null;
            completed?.Invoke();                     // 动画播完再执行收尾（如 ExitBoard）
        };
        tt.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    /// <summary>启动预热：设置页首次布局（NavigationView 模板化等）代价大，提前在隐藏态做一次，
    /// 保证第一次打开设置也有完整滑入动画（启动 Loaded 时执行一次）。</summary>
    private void WarmUpSettingsView()
    {
        if (SettingsView.Visibility == Visibility.Visible)
            return;
        double opacity = SettingsView.Opacity;
        SettingsView.Opacity = 0;
        SettingsView.Visibility = Visibility.Visible;
        SettingsView.UpdateLayout();                 // 强制完成测量/排列/模板化
        SettingsView.Visibility = Visibility.Collapsed;
        SettingsView.Opacity = opacity;
        SettingsView.RenderTransform = null;
    }

    /// <summary>恢复前台的多帧 Opacity 遮罩：Opacity=0 保持约 N 帧（渲染目标重建+位图纹理重传完成），再恢复为 1。</summary>
    private void BeginRestoreMask()
    {
        ClearRestoreMask();          // 上一次遮罩若未结束（例如快速二次最小化），先清理旧订阅
        Opacity = 0;
        // 非虚拟化 StackPanel 全量重测在不可见帧同步完成，避免遮罩提前抬起露出抖动帧。
        UpdateLayout();
        int frames = 0;
        _restoreFrameHandler = (_, _) =>
        {
            frames++;
            if (frames >= RestoreMaskFrames)
                EndRestoreMask(this, EventArgs.Empty);
        };
        CompositionTarget.Rendering += _restoreFrameHandler;

        // 兜底：若渲染帧事件迟迟未触发（渲染线程异常等），到时强制恢复可见，避免窗口卡透明。
        _restoreMaskTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RestoreMaskTimeoutMs) };
        _restoreMaskTimer.Tick += EndRestoreMask;
        _restoreMaskTimer.Start();
    }

    private void EndRestoreMask(object? sender, EventArgs e)
    {
        ClearRestoreMask();
        Opacity = 1;
    }

    private void ClearRestoreMask()
    {
        if (_restoreFrameHandler != null)
        {
            CompositionTarget.Rendering -= _restoreFrameHandler;
            _restoreFrameHandler = null;
        }
        if (_restoreMaskTimer != null)
        {
            _restoreMaskTimer.Stop();
            _restoreMaskTimer.Tick -= EndRestoreMask;
            _restoreMaskTimer = null;
        }
    }

    /// <summary>跳页：先按模型估算滚到目标页附近生成容器，再用目标页容器的真实布局位置精确对齐页顶
    /// （借鉴 KillerPDF：用 TransformToVisual 取真实坐标，规避模型高度累加与虚拟化布局的偏差）。</summary>
    private void ScrollToPage(int page1Based)
    {
        var sv = _pdfScrollViewer;
        if (sv == null)
            return;

        int target = Math.Clamp(page1Based, 1, _viewModel.TotalPages);
        int targetIndex = target - 1;

        // 第一步：按模型估算滚到目标页附近，让虚拟化生成目标页容器。
        ScrollToOffsetClamped(sv, _viewModel.GetPageVerticalOffset(targetIndex));

        Dispatcher.BeginInvoke(() =>
        {
            sv.UpdateLayout();

            // 第二步：用目标页容器的真实布局位置精确对齐页顶。
            if (PagesItemsControl.ItemContainerGenerator.ContainerFromIndex(targetIndex) is ContentPresenter item
                && sv.Content is FrameworkElement content)
            {
                double realTop = item.TransformToVisual(content).Transform(new Point(0, 0)).Y;
                ScrollToOffsetClamped(sv, realTop);
                sv.UpdateLayout();
            }

            if (_viewModel.CurrentPage != target)
                App.Log("ScrollToPage",
                    new Exception($"expected p{target}, got p{_viewModel.CurrentPage}, offset={sv.VerticalOffset:F2}"));
        }, DispatcherPriority.Loaded);
    }

    /// <summary>滚动到指定偏移，限制在可滚动范围内（最后一页较矮时可精确页顶对齐）。</summary>
    private static void ScrollToOffsetClamped(ScrollViewer sv, double offset)
    {
        double max = sv.ScrollableHeight;
        sv.ScrollToVerticalOffset(Math.Clamp(offset, 0, max));
    }

    /// <summary>缩略图面板出现时整个白色容器（背景+边框+列表）从左向右 150ms EaseOut 由快到慢滑入
    /// （列宽一步到位，避免动画列宽连续触发 ReloadPages 卡顿）。
    /// 延迟到布局完成后取实际宽度：IsVisibleChanged 触发时面板尚未布局，ActualWidth 仍为 0。</summary>
    private void ThumbnailPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Grid || e.NewValue is not true || ThumbnailPanelHost == null)
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            double w = ThumbnailPanelHost.ActualWidth;
            if (w <= 0)
                return;
            var tt = new TranslateTransform(-w, 0);
            ThumbnailPanelHost.RenderTransform = tt;
            var a = new DoubleAnimation(-w, 0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }   // 由快到慢（与菜单滑入同款）
            };
            a.Completed += (_, _) => ThumbnailPanelHost.RenderTransform = null;
            tt.BeginAnimation(TranslateTransform.XProperty, a);
        }));
    }

    /// <summary>缩略图开关：打开走 IsThumbnailPanelVisible（滑入由 ThumbnailPanel_IsVisibleChanged 播）；
    /// 关闭先播 150ms EaseIn 滑出再置 false（绑定即时折叠，无法在折叠后动画）；
    /// 滑出中再点 = 中止滑出恢复可见（对齐弹出菜单 AbortFlyOut 语义）。</summary>
    private void ThumbnailPanelToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_thumbnailClosing)
        {
            // 中止滑出：移除动画时钟（Completed 不再触发）、清 Transform，面板保持可见
            (ThumbnailPanelHost.RenderTransform as TranslateTransform)?.BeginAnimation(TranslateTransform.XProperty, null);
            ThumbnailPanelHost.RenderTransform = null;
            _thumbnailClosing = false;
            return;
        }
        if (!_viewModel.IsThumbnailPanelVisible)
        {
            _viewModel.IsThumbnailPanelVisible = true;   // 打开：滑入动画由 IsVisibleChanged 处理
            return;
        }
        ThumbnailPanelSlideOut();
    }

    /// <summary>缩略图面板滑出：从当前位置 150ms CircleEase EaseIn（由慢到快）滑到左侧栏外，
    /// Completed 后才置 IsThumbnailPanelVisible=false（外层 Grid 随即折叠，ClipToBounds 裁剪）。</summary>
    private void ThumbnailPanelSlideOut()
    {
        double w = ThumbnailPanelHost.ActualWidth;
        if (w <= 0)
        {
            _viewModel.IsThumbnailPanelVisible = false;   // 未布局：直接关
            return;
        }
        // 起点 = 当前位置（滑入未播完即关：取动画当前值；否则 0）
        double from;
        TranslateTransform tt;
        if (ThumbnailPanelHost.RenderTransform is TranslateTransform existing)
        {
            tt = existing;
            from = existing.X;
        }
        else
        {
            tt = new TranslateTransform();
            from = 0;
        }
        tt.BeginAnimation(TranslateTransform.XProperty, null);   // 清掉残留滑入时钟，防两条动画打架
        ThumbnailPanelHost.RenderTransform = tt;

        var a = new DoubleAnimation(from, -w, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseIn }   // 由慢到快（与菜单滑出同款）
        };
        a.Completed += (_, _) =>
        {
            _thumbnailClosing = false;
            ThumbnailPanelHost.RenderTransform = null;
            _viewModel.IsThumbnailPanelVisible = false;   // 动画播完才真正隐藏
        };
        _thumbnailClosing = true;
        tt.BeginAnimation(TranslateTransform.XProperty, a);
    }

    /// <summary>按实际尺寸裁剪缩略图外框为圆角（Border 的 CornerRadius 不裁剪子元素）。</summary>
    private void ThumbnailFrame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Border b && b.ActualWidth > 0 && b.ActualHeight > 0)
            b.Clip = new RectangleGeometry(new Rect(0, 0, b.ActualWidth, b.ActualHeight), 6, 6);
    }

    /// <summary>缩略图面板加载后查找内部 ScrollViewer，用于缩略图懒加载。</summary>
    private void ThumbnailList_Loaded(object sender, RoutedEventArgs e)
    {
        if (_thumbScrollViewer != null)
            _thumbScrollViewer.ScrollChanged -= ThumbnailScrollViewer_ScrollChanged;

        _thumbScrollViewer = FindVisualChildren<ScrollViewer>(ThumbnailList).FirstOrDefault();
        if (_thumbScrollViewer != null)
        {
            _thumbScrollViewer.ScrollChanged += ThumbnailScrollViewer_ScrollChanged;
            _viewModel.UpdateVisibleThumbnails(_thumbScrollViewer.VerticalOffset, _thumbScrollViewer.ViewportHeight, _thumbScrollViewer.ExtentHeight);
        }
    }

    /// <summary>缩略图面板变为可见时刷新一次（延迟到 Background：展开瞬间布局未完成，ViewportHeight 为 0 会被跳过），
    /// 并自动滚动缩略图列表到当前页（标记置位；ViewportHeight 未就绪则留待 SizeChanged 消费）。</summary>
    private void ThumbnailList_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || _thumbScrollViewer == null)
            return;
        _pendingThumbnailScrollToCurrent = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _viewModel.UpdateVisibleThumbnails(_thumbScrollViewer.VerticalOffset, _thumbScrollViewer.ViewportHeight, _thumbScrollViewer.ExtentHeight);
            if (_pendingThumbnailScrollToCurrent && _thumbScrollViewer.ViewportHeight > 0)
                ScrollThumbnailsToCurrentPage();
        }));
    }

    /// <summary>面板展开布局完成后：消费待处理标记，滚动缩略图到当前页并加载可见缩略图
    /// （窗口缩放时标记已清，不会反复滚动）。</summary>
    private void ThumbnailList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_thumbScrollViewer == null)
            return;
        if (_pendingThumbnailScrollToCurrent && _thumbScrollViewer.ViewportHeight > 0)
            ScrollThumbnailsToCurrentPage();
        _viewModel.UpdateVisibleThumbnails(_thumbScrollViewer.VerticalOffset, _thumbScrollViewer.ViewportHeight, _thumbScrollViewer.ExtentHeight);
    }

    /// <summary>面板展开后滚动缩略图列表到当前页（虚拟化列表下用 ScrollIntoView，BringIndexIntoView 按需生成容器）。</summary>
    private void ScrollThumbnailsToCurrentPage()
    {
        _pendingThumbnailScrollToCurrent = false;
        var thumbs = _viewModel.Thumbnails;
        int index = _viewModel.CurrentPage - 1;
        if (thumbs.Count == 0 || index < 0 || index >= thumbs.Count)
            return;
        ThumbnailList.ScrollIntoView(thumbs[index]);
    }

    /// <summary>缩略图滚动时加载可见缩略图。</summary>
    private void ThumbnailScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_thumbScrollViewer == null)
            return;

        _viewModel.UpdateVisibleThumbnails(_thumbScrollViewer.VerticalOffset, _thumbScrollViewer.ViewportHeight, _thumbScrollViewer.ExtentHeight);
    }

    /// <summary>点击缩略图跳页。</summary>
    private void Thumbnail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is ThumbnailModel thumb)
            _viewModel.GoToPage(thumb.PageIndex + 1);
    }

    private void PageNumberBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.CommitPageNumber();
            Keyboard.ClearFocus();   // 提交后移除焦点，避免再次触发 LostFocus 重复提交
        }
    }

    private void PageNumberBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _viewModel.CommitPageNumber();
    }

    /// <summary>单击页码框：光标直接定位到文本最右侧（不按点击位置定位，每次单击均生效）。</summary>
    private void PageNumberBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox tb)
            return;
        if (!tb.IsKeyboardFocused)
            tb.Focus();
        e.Handled = true;                          // 阻止默认命中测试定位光标
        tb.CaretIndex = tb.Text?.Length ?? 0;      // 光标放到文本末尾
    }

    /// <summary>触摸手势开始：平移/缩放/惯性全部由本类接管（ScrollViewer PanningMode=None），
    /// 是否可用由 IsPdfGestureAllowed 按「工具模式 × 手指数 × 多指书写」门控。</summary>
    private void PagesControl_ManipulationStarting(object? sender, ManipulationStartingEventArgs e)
    {
        _isTouchZooming = false;
        _touchGesture = TouchGestureKind.Undecided;   // 每次手势重新判定 pan/zoom（改动A）
        _pdfPanAllowed = false;                       // 新一笔手势：未接管过平移前不得惯性
        e.ManipulationContainer = PagesScrollViewer;
        e.Mode = ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.Scale;
    }

    /// <summary>平移 + 双指缩放，统一补偿公式 offset' = (offset + v − t) × ratio − v
    /// （v = 当前多指质心，t = 本帧质心平移量，ratio = 本帧缩放倍率，纯平移时 = 1）：
    /// ratio=1 化简为 offset−t（内容跟手），t=0 化简为以 v 为中心缩放，且纯平移帧质心跳变在式中自然抵消。
    /// 手势类型按累计量锁存（改动A）：只决定本帧是否缩放、抬指后是否允许惯性；平移不再交给 ScrollViewer。</summary>
    private void PagesControl_ManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
    {
        // 有页面选区遮罩可见时双指让位给选区变换（ICA 选择语义：双指=移动/旋转/缩放选区），不触发捏合缩放
        if (IsAnySelectionCoverVisible)
            return;

        // 惯性阶段（手指已全抬，InertiaStarting 未被掐才会有）：只按甩动位移滚动，不进门/不取质心/不缩放
        if (e.IsInertial)
        {
            var inertiaSv = _pdfScrollViewer;
            if (inertiaSv == null)
                return;
            e.Handled = true;
            System.Windows.Vector inertiaT = e.DeltaManipulation.Translation;
            inertiaSv.ScrollToHorizontalOffset(inertiaSv.HorizontalOffset - inertiaT.X);
            inertiaSv.ScrollToVerticalOffset(inertiaSv.VerticalOffset - inertiaT.Y);
            return;
        }

        // 加/抬指的那一帧不应用任何平移/缩放：换指瞬间质心与平移量会突变，应用即视图突跳（跳左上角的直接防线）
        if (_pdfTouchSetChanged)
        {
            _pdfTouchSetChanged = false;
            return;
        }

        // 手指数/多指书写门：批注模式单指、多指书写开启时整帧不介入（内置平移已关 → 墨迹照常，页面不动）
        if (!IsPdfGestureAllowed)
            return;

        // 手势锁存判定（累计量，非单帧增量）
        if (_touchGesture == TouchGestureKind.Undecided)
        {
            double cumScale = e.CumulativeManipulation.Scale.X;
            System.Windows.Vector cumTranslation = e.CumulativeManipulation.Translation;
            if (Math.Abs(cumScale - 1.0) >= 0.03)
                _touchGesture = TouchGestureKind.Zoom;
            else if (cumTranslation.Length >= 20)
                _touchGesture = TouchGestureKind.Pan;
            // 未决：不缩放（ratio=1），但平移照常由本类应用
        }

        var sv = _pdfScrollViewer;
        if (sv == null)
        {
            if (_touchGesture == TouchGestureKind.Zoom)
                _viewModel.ZoomBy(e.DeltaManipulation.Scale.X);   // 无 ScrollViewer：只改缩放，不平移
            return;
        }

        System.Windows.Vector t = e.DeltaManipulation.Translation;
        double oldOffsetX = sv.HorizontalOffset;   // 布局未变时读取（ZoomBy 只改 ZoomLevel，不动 offset）
        double oldOffsetY = sv.VerticalOffset;

        double ratio = 1.0;
        if (_touchGesture == TouchGestureKind.Zoom)
        {
            double scale = e.DeltaManipulation.Scale.X;
            if (Math.Abs(scale - 1.0) > 0.005)
            {
                _isTouchZooming = true;
                double oldZoom = _viewModel.ZoomLevel;
                // 已在 clamp 边界（ZoomLevel 未变化）：ratio 保持 1，只平移不缩放，避免无效重排。
                if (_viewModel.ZoomBy(scale))
                    ratio = _viewModel.ZoomLevel / oldZoom;
            }
        }

        _pdfPanAllowed = true;   // 本手势已由本类接管平移 → 抬指后可走甩动惯性
        e.Handled = true;

        // 锚点 = 当前实时多指质心（跟踪缺失时回退 WPF 手势起点）；ratio=1 时 v 在式中约掉，平移不受锚点影响
        System.Windows.Point pos = CurrentTouchCentroid() ?? PagesScrollViewer.TranslatePoint(e.ManipulationOrigin, sv);

        if (ratio != 1.0)
            sv.UpdateLayout();   // 只有缩放才需要先跑布局（沿用同帧防闪思路）
        sv.ScrollToHorizontalOffset((oldOffsetX + pos.X - t.X) * ratio - pos.X);
        sv.ScrollToVerticalOffset((oldOffsetY + pos.Y - t.Y) * ratio - pos.Y);
    }

    /// <summary>惯性起点：缩放手势、或本手势未接管过平移（批注模式单指等）一律无惯性；
    /// 平移手势给定甩动减速度，惯性阶段的滚动由 PagesControl_ManipulationDelta 的 IsInertial 分支落地
    /// （WPF 惯性位移走 ManipulationDelta(IsInertial=true)，无独立事件）。</summary>
    private void PagesControl_ManipulationInertiaStarting(object? sender, ManipulationInertiaStartingEventArgs e)
    {
        if (_touchGesture == TouchGestureKind.Zoom || _isTouchZooming || !_pdfPanAllowed)
        {
            e.Handled = true;
            return;
        }
        if (e.TranslationBehavior != null)
            e.TranslationBehavior.DesiredDeceleration = PanInertiaDeceleration;
    }

    private void PagesControl_ManipulationCompleted(object? sender, ManipulationCompletedEventArgs e)
    {
        _isTouchZooming = false;
        _touchGesture = TouchGestureKind.Undecided;
        _pdfPanAllowed = false;
    }

    // ══════════ 双指手势：触摸计数 + 非鼠标状态墨迹抑制（改动B）══════════

    /// <summary>是否允许本帧 PDF 触摸手势（平移/缩放）：鼠标模式（非 IsInkMode）任何手指数都允许——它就是用来移动页面的；
    /// 批注模式（笔/橡皮/选择/形状）要求 ≥2 指且「多指书写」未开——单指必须留给书写/擦除/选择。
    /// 手指数与停墨逻辑同源（_pdfActiveTouchIds，IsPalmTouch 手掌不计入）。</summary>
    private bool IsPdfGestureAllowed =>
        !_viewModel.InkTool.IsInkMode
        || (!_viewModel.InkTool.IsMultiFingerInk && _pdfActiveTouchIds.Count >= 2);

    /// <summary>当前活动手指的实时质心（PagesScrollViewer 视口坐标，缩放锚点用）；无跟踪点时返回 null。</summary>
    private System.Windows.Point? CurrentTouchCentroid()
    {
        if (_pdfTouchPoints.Count == 0)
            return null;
        double sx = 0, sy = 0;
        foreach (var p in _pdfTouchPoints.Values)
        {
            sx += p.X;
            sy += p.Y;
        }
        return new System.Windows.Point(sx / _pdfTouchPoints.Count, sy / _pdfTouchPoints.Count);
    }

    /// <summary>第二指落下：非鼠标状态（笔/橡皮/形状/无选区的选择）停墨，让双指走本类接管的平移 + 捏合缩放。
    /// 选区遮罩可见时不抑制——双指继续变换选区（ICA 语义）。</summary>
    private void PdfView_PreviewTouchDown(object? sender, TouchEventArgs e)
    {
        if (IsPalmTouch(e))
            return;   // 手掌擦除：手掌接触不计入第 2 指（否则会误触发停墨），也不进 _pdfActiveTouchIds
        _pdfActiveTouchIds.Add(e.TouchDevice.Id);
        _pdfTouchPoints[e.TouchDevice.Id] = e.TouchDevice.GetTouchPoint(PagesScrollViewer).Position;   // 位置跟踪：缩放锚点取实时质心
        _pdfTouchSetChanged = true;   // 换指帧：本帧不应用平移/缩放，防质心/平移量突变跳视图
        if (_viewModel.InkTool.IsMultiFingerInk)
            return;   // 多指书写开：不做第 2 指停墨，各指同时落笔成墨
        if (_pdfActiveTouchIds.Count < 2 || _inkGestureSuppressed)
            return;
        if (!_viewModel.InkTool.IsInkMode || IsAnySelectionCoverVisible)
            return;

        // 橡皮 + 双指手势成立：先回滚本触摸已擦内容（须在记基线之前，保证基线为回滚后状态）
        RevertEraserBatchForGesture();

        foreach (var canvas in _pageInkCanvases)
        {
            if (canvas is PageInkCanvas pc)
                pc.Controller.AbortStroke();   // 中止进行中的湿墨/时间笔锋（无笔画时是空操作）

            // EditingMode 是 OneWay 绑定，裸赋值会清绑定：摘绑定→置 None→（恢复时接回），同 MainWindow_ShapeDrawing 手法
            var modeProperty = InkCanvas.EditingModeProperty;
            var binding = BindingOperations.GetBindingExpression(canvas, modeProperty);
            if (binding == null)
                continue;
            int strokesBefore = canvas.Strokes.Count;
            if (!_suppressedStrokeBase.ContainsKey(canvas))
                _suppressedStrokeBase[canvas] = strokesBefore;   // 基线只记一次：恢复时删掉手势期间新增的全部笔画
            _suppressedEditingBindings[canvas] = binding.ParentBinding;
            BindingOperations.ClearBinding(canvas, modeProperty);
            canvas.EditingMode = InkCanvasEditingMode.None;
            // 第 1 指落下时 InkCanvas 已开始一笔；切 None 时 WPF 可能把它提交而不是丢弃 →
            // 手势结束后会残留一小截墨迹。这里把本次输入新增的笔画移除（CodeInput 门：不写撤销历史；
            // PageStrokes_StrokesChanged 的笔画级订阅/_strokePage 簿记仍随事件正常回收）。
            if (canvas.Strokes.Count > strokesBefore)
            {
                var interrupted = new StrokeCollection();
                for (int i = strokesBefore; i < canvas.Strokes.Count; i++)
                    interrupted.Add(canvas.Strokes[i]);
                var savedCommitType = _currentCommitType;
                _currentCommitType = CommitReason.CodeInput;
                try
                {
                    canvas.Strokes.Remove(interrupted);
                }
                finally
                {
                    _currentCommitType = savedCommitType;
                }
            }
        }
        FlushEraserBatch();   // 橡皮批处理手势打断时先落账，防累加器滞留
        _inkGestureSuppressed = true;
    }

    /// <summary>实时手指位置跟踪（缩放锚点=当前质心）：Preview 隧道 + handledEventsToo，InkCanvas 捕获触摸也能收到。</summary>
    private void PdfView_PreviewTouchMove(object? sender, TouchEventArgs e)
    {
        if (_pdfTouchPoints.ContainsKey(e.TouchDevice.Id))
            _pdfTouchPoints[e.TouchDevice.Id] = e.TouchDevice.GetTouchPoint(PagesScrollViewer).Position;
    }

    /// <summary>抬指：触摸数归零才恢复墨迹（中途抬一指不恢复，防残留手指误画）。</summary>
    private void PdfView_PreviewTouchUp(object? sender, TouchEventArgs e)
    {
        if (_pdfActiveTouchIds.Remove(e.TouchDevice.Id))
        {
            _pdfTouchPoints.Remove(e.TouchDevice.Id);   // 位置跟踪同步
            _pdfTouchSetChanged = true;                 // 换指帧：本帧不应用平移/缩放，防质心/平移量突变跳视图
        }
        if (_pdfActiveTouchIds.Count != 0)
            return;

        // 抬笔丢失兜底：所有手指已抬起，仍有未收笔会话则按最后一点补收笔（防僵尸湿墨/整笔丢失）。
        // 与手势抑制无关，始终执行；延迟到 Background，等原生笔画与抬笔处理先完成。
        foreach (var pageCanvas in _pageInkCanvases)
        {
            if (pageCanvas is not PageInkCanvas pc)
                continue;
            pc.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    // 期间已开始下一笔（触点重新按下）就不补收笔，避免误杀新会话
                    if (_pdfActiveTouchIds.Count == 0)
                        pc.Controller.FinalizeOrphanSessions();
                }));
        }

        if (!_inkGestureSuppressed)
            return;

        // 延迟到 Background 再恢复：被中断的那一笔可能在抬指后才由 InkCanvas 提交，
        // 立即恢复会漏删 → 抬手后残留一小截墨迹。此时已全部抬手，不会误删合法新笔。
        if (_pendingInkRestore)
            return;
        _pendingInkRestore = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RestoreSuppressedInk));
    }

    /// <summary>双指手势结束恢复墨迹：先删掉手势期间新增的笔画（含抬指后才提交的那一截），再接回 EditingMode 绑定。
    /// 删除走 CodeInput 门，不进撤销历史。</summary>
    private void RestoreSuppressedInk()
    {
        _pendingInkRestore = false;
        if (_pdfActiveTouchIds.Count != 0)
        {
            // 期间又按下新手指：本次不恢复；下一次全部抬指时 PreviewTouchUp 会重新排队。
            // 不在这里反复排队，避免按住两根手指期间每帧都跑一次回调。
            return;
        }

        var prev = _currentCommitType;
        _currentCommitType = CommitReason.CodeInput;
        try
        {
            foreach (var (canvas, baseline) in _suppressedStrokeBase)
            {
                int removeFrom = Math.Clamp(baseline, 0, canvas.Strokes.Count);
                if (canvas.Strokes.Count <= removeFrom)
                    continue;
                var extra = new StrokeCollection();
                for (int i = removeFrom; i < canvas.Strokes.Count; i++)
                    extra.Add(canvas.Strokes[i]);
                canvas.Strokes.Remove(extra);
            }
        }
        finally
        {
            _currentCommitType = prev;
            _suppressedStrokeBase.Clear();
        }

        foreach (var (canvas, binding) in _suppressedEditingBindings)
            BindingOperations.SetBinding(canvas, InkCanvas.EditingModeProperty, binding);   // 接回绑定即自动求值回当前工具
        _suppressedEditingBindings.Clear();
        _inkGestureSuppressed = false;
    }

    /// <summary>Ctrl + 滚轮缩放：以鼠标指针位置为锚点，同步布局后补偿偏移，避免异步补偿造成的闪烁。</summary>
    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            // Shift + 滚轮：横向滚动（滚轮向上 Delta>0 → 向左）；Shift+Ctrl 落入下方缩放分支
            e.Handled = true;
            _pdfScrollViewer?.ScrollToHorizontalOffset(_pdfScrollViewer.HorizontalOffset - e.Delta);
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        e.Handled = true;

        var sv = _pdfScrollViewer;
        // 按 delta 比例缩放（对齐 KillerPDF）：高精度滚轮/触控板的小 delta 产生连续平滑的倍率。
        double factor = Math.Pow(MainViewModel.ZoomStepFactor, e.Delta / 120.0);
        if (sv == null)
        {
            _viewModel.ZoomBy(factor);
            return;
        }

        double oldZoom = _viewModel.ZoomLevel;
        System.Windows.Point pos = e.GetPosition(sv);
        double oldOffsetX = sv.HorizontalOffset;
        double oldOffsetY = sv.VerticalOffset;

        // 已在 clamp 边界（ZoomLevel 未变化）：跳过布局与补偿，避免无效重排。
        if (!_viewModel.ZoomBy(factor))
            return;
        double ratio = _viewModel.ZoomLevel / oldZoom;

        // 强制同一帧内完成新布局，再基于缩放前的偏移补偿，
        // 使鼠标指向的内容点留在原地且不出现中间帧跳变。
        sv.UpdateLayout();
        sv.ScrollToHorizontalOffset((oldOffsetX + pos.X) * ratio - pos.X);
        sv.ScrollToVerticalOffset((oldOffsetY + pos.Y) * ratio - pos.Y);
    }

    /// <summary>工具栏放大/缩小：以视口中心为锚点（复用 Ctrl+滚轮已验证的补偿公式），避免以内容左上角为中心放大时视图"漂走"。</summary>
    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => ZoomAnchored(MainViewModel.ZoomStepFactor);

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => ZoomAnchored(1.0 / MainViewModel.ZoomStepFactor);

    private void ZoomAnchored(double factor)
    {
        if (_pdfScrollViewer is not { } sv)
        {
            _viewModel.ZoomBy(factor);
            return;
        }
        double oldZoom = _viewModel.ZoomLevel;
        var center = new System.Windows.Point(sv.ViewportWidth / 2, sv.ViewportHeight / 2);
        double oldOffsetX = sv.HorizontalOffset;
        double oldOffsetY = sv.VerticalOffset;

        if (!_viewModel.ZoomBy(factor))
            return;   // 已在 clamp 边界（ZoomLevel 未变化）：跳过布局与补偿
        double ratio = _viewModel.ZoomLevel / oldZoom;

        sv.UpdateLayout();   // 同帧完成新布局再补偿，与滚轮路径同款防闪烁
        sv.ScrollToHorizontalOffset((oldOffsetX + center.X) * ratio - center.X);
        sv.ScrollToVerticalOffset((oldOffsetY + center.Y) * ratio - center.Y);
    }

    /// <summary>视口宽度变化时按新宽度重新渲染页面。</summary>
    private void ScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _viewModel.ViewportWidth = (int)e.NewSize.Width;
    }

    /// <summary>页面容器加载后找到内部 ScrollViewer，用于懒加载；并注入高 DPI 缩放系数。</summary>
    private void PagesControl_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateDpiScale();

        if (_pdfScrollViewer != null)
            _pdfScrollViewer.ScrollChanged -= ScrollViewer_ScrollChanged;

        _pdfScrollViewer = PagesScrollViewer;
        _pdfScrollViewer.ScrollChanged += ScrollViewer_ScrollChanged;
        _viewModel.UpdateVisiblePages(_pdfScrollViewer.VerticalOffset, _pdfScrollViewer.ViewportHeight);
    }

    /// <summary>窗口移到不同 DPI 显示器时更新渲染缩放系数，并触发去抖重渲染保持清晰。</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateDpiScale();
    }

    /// <summary>把窗口 DPI 缩放注入 ViewModel（用于计算渲染分辨率）。</summary>
    private void UpdateDpiScale()
    {
        _viewModel.DpiScale = Math.Max(1.0, VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    /// <summary>滚动时加载可见页。</summary>
    private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_pdfScrollViewer == null)
            return;

        _viewModel.UpdateVisiblePages(_pdfScrollViewer.VerticalOffset, _pdfScrollViewer.ViewportHeight);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t)
                yield return t;
            foreach (var sub in FindVisualChildren<T>(child))
                yield return sub;
        }
    }

    // ══════════ 墨迹批注：页面 InkCanvas 跟踪 / 面积擦批处理 / 撤销注册 / 墨迹选项菜单 ══════════

    /// <summary>清空当前显示页的墨迹（橡皮菜单滑动清空调用）：绕开面积擦累加器后整体移除，并登记一条可撤销的擦除历史。
    /// 返回是否成功清空（页码非法或本就无墨迹=false，调用方据此决定是否切换模式/关菜单）。</summary>
    public bool ClearCurrentPageInk()
    {
        if (_viewModel.CurrentPage < 1 || _viewModel.CurrentPage > _viewModel.Pages.Count)
            return false;
        var page = _viewModel.Pages[_viewModel.CurrentPage - 1];
        var removed = new StrokeCollection(page.Strokes);
        if (removed.Count == 0)
            return false;
        // 橡皮激活时 StrokesChanged 会入面积擦累加器（只在页面 MouseUp/TouchUp 提交，Popup 内不会触发）→
        // 用 CodeInput 门早退绕开（笔画订阅簿记在其之前仍会完成），清完手动提交同款擦除历史（可撤销）
        var prev = _currentCommitType;
        _currentCommitType = CommitReason.CodeInput;
        try
        {
            page.Strokes.Clear();
        }
        finally
        {
            _currentCommitType = prev;
        }
        _viewModel.InkTool.TimeMachine.CommitStrokeEraseHistory(removed, targetStrokes: page.Strokes);
        return true;
    }


    /// <summary>页面 InkCanvas 加载：跟踪该页 StrokeCollection 的变更（供撤销注册），并登记到画布列表。</summary>
    private void InkCanvas_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not InkCanvas inkCanvas || inkCanvas.DataContext is not PageModel page)
            return;
        TrackStrokeCollection(page);
        if (!_pageInkCanvases.Contains(inkCanvas))
            _pageInkCanvases.Add(inkCanvas);
        // 手掌擦除：Loaded 可能多次触发，先摘后挂防重复订阅（橡皮菜单「手掌擦除」开关的落笔入口）
        inkCanvas.TouchDown -= PageCanvas_PalmTouchDown;
        inkCanvas.TouchDown += PageCanvas_PalmTouchDown;
        // 选择工具激活时新页同步关闭 Manipulation（ICA BtnSelect_Click 语义，与工具切换守卫一致）
        if (_viewModel.InkTool.ActiveTool == InkToolMode.Select)
            inkCanvas.IsManipulationEnabled = false;
        // 无条件设置黑板擦 EraserShape：避免首次悬停落到 InkCanvas 默认的方形 StylusShape（尺寸/比例都与设置不符）。
        UpdateEraserShapes();
        UpdateInkCursor();   // 新页面画布加载后立即应用当前自定义光标

        // 时间笔锋：把生长叠加层挂到页面 Grid（InkCanvas 之后 → 覆盖在干墨之上），并同步武装状态与缩放
        if (inkCanvas is PageInkCanvas pageCanvas)
        {
            if (pageCanvas.Controller.Overlay.Parent is null && inkCanvas.Parent is Panel host)
                host.Children.Add(pageCanvas.Controller.Overlay);
            // 自绘笔画提交（多指副设备 / 硬笔屏蔽模式）：Loaded 可能多次触发，先退后挂防重复订阅
            if (_selfDrawHandlers.Remove(pageCanvas, out var oldHandler))
                pageCanvas.Controller.StrokeReady -= oldHandler;
            Action<Stroke, int> selfDrawHandler = (stroke, deviceId) => CommitSelfDrawnStroke(pageCanvas, stroke, deviceId);
            _selfDrawHandlers[pageCanvas] = selfDrawHandler;
            pageCanvas.Controller.StrokeReady += selfDrawHandler;
            RefreshTimePressureArmed();
        }

        // 形状手绘兜底：新页加载时若正处于形状模式，显式置 None（绑定已传播，防首帧竞态）。
        // EditingMode 是 OneWay 绑定，直接本地赋值会清掉绑定，之后该页不再随工具切换同步；
        // 故先摘绑定、赋值、再把原绑定接回（同 CommitPendingPdfStroke 手法）。
        if (_viewModel.InkTool.ShapeDrawMode != 0)
        {
            var modeProperty = InkCanvas.EditingModeProperty;
            var binding = BindingOperations.GetBindingExpression(inkCanvas, modeProperty);
            if (binding != null)
                BindingOperations.ClearBinding(inkCanvas, modeProperty);
            inkCanvas.EditingMode = InkCanvasEditingMode.None;
            if (binding != null)
                BindingOperations.SetBinding(inkCanvas, modeProperty, binding.ParentBinding);
        }

        // 停顿强制识别：每页画布挂一个追踪器（配置经活值 lambda 读设置，修改即时生效；Unloaded 时释放）。
        // 流程为"先试后断"：停顿时用临时笔画试识别，命中才断笔提交替换，未命中不断笔。
        if (!_pauseTrackers.ContainsKey(inkCanvas))
        {
            var pageCanvasForPause = inkCanvas;
            _pauseTrackers[inkCanvas] = new PauseInkToShapeTracker(
                inkCanvas,
                isEnabled: () => _viewModel.Settings.IsPauseInkToShapeEnabled,
                getDelayMs: () => _viewModel.Settings.PauseInkToShapeDelayMs,
                getTolerancePx: () => _viewModel.Settings.PauseInkToShapeTolerancePx,
                canTrigger: () => CanPauseTriggerPdf(pageCanvasForPause),
                tryPauseRecognizeAsync: (deviceId, pending) =>
                    TryPauseRecognizeProvisionalPdfAsync(pageCanvasForPause, deviceId, pending));
        }
    }

    /// <summary>停顿强制识别触发门（PDF 宿主）：画笔工具、非荧光笔、非形状模式、内建墨迹模式。
    /// 多指书写开启时停用（对齐画板多触摸模式：断笔切 EditingMode 会丢多指湿墨）。</summary>
    private bool CanPauseTriggerPdf(InkCanvas canvas)
        => _viewModel.InkTool.ActiveTool == InkToolMode.Pen
           && !_viewModel.InkTool.DrawingAttributes.IsHighlighter
           && _viewModel.InkTool.ShapeDrawMode == 0
           && !_viewModel.InkTool.IsMultiFingerInk
           && canvas.EditingMode == InkCanvasEditingMode.Ink;

    /// <summary>停顿强制识别·试识别（PDF 宿主）：用在绘点集构建的临时笔画（不入画布）先判定，
    /// 命中才断笔提交 <see cref="CommitPendingPdfStroke"/> 并替换图形；未命中什么都不发生，笔迹不断。
    /// 命中回调里复查触发门与落笔会话（停顿后已抬笔/切工具则放弃，抬笔走正常收笔识别兜底）。</summary>
    private async Task<bool> TryPauseRecognizeProvisionalPdfAsync(
        InkCanvas canvas, int deviceId, IReadOnlyList<System.Windows.Input.StylusPoint> pending)
    {
        if (pending.Count < 2)
            return false;
        var provisional = new Stroke(new StylusPointCollection(pending))
        {
            DrawingAttributes = canvas.DefaultDrawingAttributes.Clone()
        };
        return await _inkToShape.TryPauseCorrectProvisionalAsync(provisional, canvas.Strokes,
            () => CanPauseTriggerPdf(canvas)
                  && _pauseTrackers.TryGetValue(canvas, out var tracker) && tracker.IsTracking(deviceId)
                ? CommitPendingPdfStroke(canvas, pending)
                : null);
    }

    /// <summary>提交页面画布上正在绘制的笔画（停顿强制识别用）。瞬时切换 EditingMode 让 InkCanvas 结束本次
    /// 在绘输入（WPF 语义是<b>丢弃</b>湿墨而非提交，实测已确认），故切换后按笔画数判断：确实提交了就取新笔画，
    /// 否则用追踪器采集的点重建一条补回画布，保证墨迹不会凭空消失。时间笔锋叠加层随后中断清理。
    /// EditingMode 在 XAML 上是 OneWay 绑定，直接本地赋值会把绑定清掉（之后切工具不再同步），
    /// 故先摘绑定、切换模式、再把原绑定接回。</summary>
    private Stroke? CommitPendingPdfStroke(InkCanvas canvas, IReadOnlyList<StylusPoint> pending)
    {
        var modeProperty = InkCanvas.EditingModeProperty;
        var binding = BindingOperations.GetBindingExpression(canvas, modeProperty);
        if (binding != null)
            BindingOperations.ClearBinding(canvas, modeProperty);

        int before = canvas.Strokes.Count;
        var originalMode = canvas.EditingMode;
        canvas.EditingMode = InkCanvasEditingMode.None;
        canvas.EditingMode = originalMode;   // 结束在绘输入并恢复原模式，减少副作用

        if (binding != null)
            BindingOperations.SetBinding(canvas, modeProperty, binding.ParentBinding);

        bool committedByCanvas = canvas.Strokes.Count > before;
        var stroke = committedByCanvas ? canvas.Strokes[^1] : RebuildPendingStroke(canvas, pending);
        if (canvas is PageInkCanvas pageCanvas)
            pageCanvas.Controller.AbortStroke();   // 清时间笔锋湿墨叠加层，防残留

        return stroke;
    }

    /// <summary>用追踪器采集的点重建在绘笔画并补入画布：模式切换会丢弃湿墨，必须自己补回，否则墨迹直接消失。
    /// 补入后走既有收笔处理（伪压力 / 时间笔锋定格 + 正常识别排队），与正常收笔完全一致。</summary>
    private Stroke? RebuildPendingStroke(InkCanvas canvas, IReadOnlyList<StylusPoint> pending)
    {
        if (pending.Count < 2)
            return null;
        var stroke = new Stroke(new StylusPointCollection(pending))
        {
            DrawingAttributes = canvas.DefaultDrawingAttributes.Clone()
        };
        canvas.Strokes.Add(stroke);   // 墨迹固化（StrokesChanged → 记入撤销历史）
        PageInkCanvas_StrokeCollected(canvas, new InkCanvasStrokeCollectedEventArgs(stroke));
        return stroke;
    }

    /// <summary>页面 InkCanvas 卸载：移除时间笔锋叠加层并中断正在进行的生长（新文档重建容器时防视觉树与帧订阅泄漏）。</summary>
    private void InkCanvas_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not PageInkCanvas pageCanvas)
            return;
        // 卸载页若为当前选择页：清引用并隐藏窗口级选择工具栏（防悬空引用与幽灵工具栏）
        if (_selectionInkCanvas == pageCanvas)
        {
            _selectionInkCanvas = null;
            BorderStrokeSelectionControl.Visibility = Visibility.Collapsed;
            StrokeManipulationHistory = null;
        }
        pageCanvas.Controller.AbortStroke();
        // 自绘笔画提交处理程序退订（控制器随画布重建时防旧页处理器残留）
        if (_selfDrawHandlers.Remove(pageCanvas, out var selfDrawHandler))
            pageCanvas.Controller.StrokeReady -= selfDrawHandler;
        if (pageCanvas.Controller.Overlay.Parent is Panel host)
            host.Children.Remove(pageCanvas.Controller.Overlay);
        // 手掌擦除：画布卸载即解除订阅；若卸载时正处在手掌擦除中，把 EditingMode 绑定还原，防绑定丢失
        pageCanvas.TouchDown -= PageCanvas_PalmTouchDown;
        if (_palmEraseCanvases.Remove(pageCanvas))
        {
            var modeProperty = InkCanvas.EditingModeProperty;
            var binding = BindingOperations.GetBindingExpression(pageCanvas, modeProperty);
            if (binding != null)
                BindingOperations.SetBinding(pageCanvas, modeProperty, binding.ParentBinding);
            UpdateInkCursor();   // 手掌擦除中的画布卸载：光标按当前工具恢复（归零→笔光标）
        }
        // 停顿强制识别追踪器随页面释放（新文档重建容器时防定时器与事件订阅泄漏）
        if (_pauseTrackers.Remove(pageCanvas, out var tracker))
            tracker.Dispose();
    }

    /// <summary>把「时间笔锋是否可用」（时间笔锋 + 画笔模式 + 非荧光笔）与缩放/压感开关同步给所有页面控制器。
    /// 失去武装时控制器会立即中断当前笔画并清空叠加层，避免模式切换残留湿墨。</summary>
    private void RefreshTimePressureArmed()
    {
        // 形状手绘模式下必须解除武装：形状模式 ActiveTool 仍是 Pen，插件会在笔线程继续采集并绘制湿墨叠加层，
        // 表现为"绘制几何时墨迹线与图形同时出现"，抬笔/下次输入才随 AbortStroke 清空（"墨迹线消失"）。
        bool armed = _viewModel.InkTool.InkStyle == 3
                     && _viewModel.InkTool.ActiveTool == InkToolMode.Pen
                     && !_viewModel.InkTool.DrawingAttributes.IsHighlighter
                     && _viewModel.InkTool.ShapeDrawMode == 0;
        // 屏蔽模式（0/1 笔锋 + 硬笔）：屏蔽默认湿墨、叠加层等宽自绘（与时间笔锋互斥，依 InkStyle 分流）
        bool shield = _viewModel.InkTool.InkStyle is 0 or 1
                      && !_viewModel.InkTool.PressureEnabled
                      && _viewModel.InkTool.ActiveTool == InkToolMode.Pen
                      && !_viewModel.InkTool.DrawingAttributes.IsHighlighter
                      && _viewModel.InkTool.ShapeDrawMode == 0;
        // 等宽：默认（开关关）荧光笔忽略硬件压力，开关开时荧光笔与软笔同权吃压；关闭笔锋(2)+硬笔同样走 WPF 渲染层开关（不再抬笔归一）
        bool ignorePressure = (_viewModel.InkTool.DrawingAttributes.IsHighlighter
                               && !_viewModel.Settings.HighlighterPressureEnabled)
                              || (_viewModel.InkTool.InkStyle == 2 && !_viewModel.InkTool.PressureEnabled);
        _viewModel.InkTool.DrawingAttributes.IgnorePressure = ignorePressure;   // 放循环前：ApplyCurrentColor 以 Clone 重建，克隆链继承
        foreach (var canvas in _pageInkCanvases)
        {
            if (canvas is not PageInkCanvas pageCanvas)
                continue;
            pageCanvas.Controller.DisplayScale = _viewModel.DisplayScale;
            pageCanvas.Controller.PressureEnabled = _viewModel.InkTool.PressureEnabled;
            pageCanvas.Controller.GrowMs = _viewModel.Settings.TimePenGrowMs;
            pageCanvas.Controller.HighlighterPressureEnabled = _viewModel.Settings.HighlighterPressureEnabled;
            pageCanvas.Controller.SetCollectArmed(armed);
            pageCanvas.Controller.SetShieldArmed(shield);
            // 多指书写：副设备由叠加层自绘（主设备仍走原生管线，保持原生命中湿墨/收笔路径不变）。
            // 必须带工具门：橡皮/选择/形状下不能采集副设备，否则会边擦边写。
            pageCanvas.Controller.MultiDeviceEnabled =
                _viewModel.InkTool.IsMultiFingerInk
                && _viewModel.InkTool.ActiveTool == InkToolMode.Pen
                && _viewModel.InkTool.ShapeDrawMode == 0;
            pageCanvas.Controller.SelfDrawAllDevices = false;
            // 触摸湿墨改由叠加层绘制（原生触摸湿墨在部分触摸屏上会出"刺"）：只采集触摸设备，
            // 干墨仍由原生笔画提交；笔/鼠标湿墨保持原生。橡皮/选择/形状不采集（防边擦边写）。
            pageCanvas.Controller.CollectTouchDevices =
                _viewModel.InkTool.ActiveTool == InkToolMode.Pen
                && _viewModel.InkTool.ShapeDrawMode == 0;
            pageCanvas.DefaultDrawingAttributes.IgnorePressure = ignorePressure;   // 各页共享 VM DA 实例，幂等直改（对齐 ApplyEraserShapeForMode 写法）
        }
    }

    /// <summary>按擦除模式同步全部页面画布 EraserShape（照画板 EraserIcon_Click / EraserIconByStrokes_Click）：
    /// 墨迹擦=EllipseStylusShape(5,5)（形状不参与整笔擦命中，仅避免面积擦点擦光标残留）；
    /// 面积擦=按 EraserSize 的 38:56 黑板擦矩形，宽 = EraserSize ÷ DisplayScale（页面坐标，整层
    /// ScaleTransform 放大后屏幕恒为 EraserSize DIP，与画板粗细一致）。须在 EditingMode 变更之前调用（先形状后模式）。</summary>
    public void ApplyEraserShapeForMode(bool strokeEraser)
    {
        if (strokeEraser)
        {
            foreach (var canvas in _pageInkCanvases)
                canvas.EraserShape = new EllipseStylusShape(5, 5);
            return;
        }
        double size = _viewModel.InkTool.EraserSize / _viewModel.DisplayScale;
        foreach (var canvas in _pageInkCanvases)
            canvas.EraserShape = new RectangleStylusShape(size, InkCircleCursor.EraserBoardHeight(size));
    }

    private void UpdateEraserShapes()
        => ApplyEraserShapeForMode(_viewModel.InkTool.IsStrokeEraser);

    /// <summary>更新页面画布光标（消除"小一圈"）：WPF 内置光标不乘祖先的整层 ScaleTransform
    /// （dotnet/wpf EditBehavior.GetElementTransformMatrix 只读 InkCanvas 自身变换），DisplayScale≠1 时
    /// 内置橡皮/笔尖光标必然比实际擦除区域/笔迹小一圈。故橡皮与"关画笔光标的批注"改用自定义光标
    /// （橡皮 = 黑板擦位图，笔尖 = 圆点），尺寸 = 实际尺寸的屏幕大小（任何缩放下与实际严格等大）；
    /// "开画笔光标的批注"与浏览模式保持 WPF 内置。
    /// EraserShape / DrawingAttributes / ForceCursor 等粗细逻辑零改动。</summary>
    private void UpdateInkCursor()
    {
        var tool = _viewModel.InkTool.ActiveTool;
        // 面积擦与手掌擦除中都用黑板擦自定义光标（尺寸=擦除洞）；墨迹擦参考 ICA：不设任何自定义光标，
        // 落入下方 else 还原 XAML 默认（Cursor="Pen" + ForceCursor=false，与 ICA 擦除模式配置一致）
        bool palmErasing = _palmEraseCanvases.Count > 0;
        bool customEraser = palmErasing || (tool == InkToolMode.Eraser && !_viewModel.InkTool.IsStrokeEraser);
        double diameter;
        if (customEraser)
        {
            // 屏幕擦除宽已恒为 EraserSize（EraserShape 已按 DisplayScale 反向补偿），光标宽直接等于设置值（高 = 宽×56/38）
            diameter = _viewModel.InkTool.EraserSize;
        }
        else if (tool == InkToolMode.Pen && !_viewModel.InkTool.ShowPenCursor)
        {
            // 笔迹屏幕宽度 = DrawingAttributes 宽 × 整层 DisplayScale（InkWidth/荧光笔加粗实时反映）
            diameter = _viewModel.InkTool.DrawingAttributes.Width * _viewModel.DisplayScale;
        }
        else
        {
            // 开画笔光标的批注（Cursor="Pen" + ForceCursor，保持现状）、墨迹擦与浏览模式：交还 WPF 内置光标
            foreach (var canvas in _pageInkCanvases)
            {
                canvas.UseCustomCursor = false;
                canvas.Cursor = Cursors.Pen;   // 还原 XAML 默认：橡皮/笔尖分支曾覆盖为自定义光标，残留会让 ForceCursor 显示旧光标（橡皮→墨迹卡橡皮样式）
            }
            return;
        }

        diameter = Math.Clamp(diameter, 4, 128);
        bool isEraser = customEraser;
        var color = isEraser ? Colors.White : _viewModel.InkTool.DrawingAttributes.Color;
        // 直径变化 ≥1 DIP 或 颜色/样式变化 才重建（面板开合动画逐帧触发时，光标肉眼无差则复用旧光标）
        if (_inkCursor == null || Math.Abs(_inkCursorDiameter - diameter) >= 1.0 ||
            _inkCursorColor != color || _inkCursorIsEraser != isEraser)
        {
            double dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var created = isEraser
                ? InkCircleCursor.CreateEraserBoard(diameter, dpi)                               // 黑板擦位图（38:56）= 擦除矩形
                : InkCircleCursor.CreatePenDot(diameter, dpi, color);                            // 同色实心圆点（荧光笔 alpha 原样）= 笔迹
            if (created == null)
                return;
            var old = _inkCursor;
            _inkCursor = created;            // 先赋新再释放旧：避免销毁仍被画布引用的光标句柄
            _inkCursorDiameter = diameter;
            _inkCursorColor = color;
            _inkCursorIsEraser = isEraser;
            old?.Dispose();
        }

        foreach (var canvas in _pageInkCanvases)
        {
            canvas.UseCustomCursor = true;   // UseCustomCursor=true 时 InkCanvas 不再拦截 QueryCursor，Cursor 直接生效
            canvas.Cursor = _inkCursor.Cursor;
        }
    }

    private void TrackStrokeCollection(PageModel page)
    {
        if (_trackedStrokePages.ContainsKey(page.Strokes))
            return;

        _trackedStrokePages[page.Strokes] = page.PageIndex;
        page.Strokes.StrokesChanged += PageStrokes_StrokesChanged;
    }

    private void UntrackStrokeCollection(StrokeCollection strokes)
    {
        if (!_trackedStrokePages.Remove(strokes))
            return;

        strokes.StrokesChanged -= PageStrokes_StrokesChanged;
    }

    /// <summary>新文档（Pages 集合 Reset）时解除旧页订阅并清空橡皮批处理。</summary>
    private void Pages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Reset)
            return;

        foreach (var strokes in _trackedStrokePages.Keys.ToList())
            UntrackStrokeCollection(strokes);
        _strokePage.Clear();   // 与 _trackedStrokePages 同步清（换文档防串页）
        AddedStroke = null;
        ReplacedStroke = null;
        _eraseTargetStrokes = null;
        _pageInkCanvases.Clear();
    }

    // ────────── 批注笔锋（ICA MW_SimulatePressureAndInkToShape.cs 的伪压力移植）──────────
    // 仅处理"无硬件压力"的输入（鼠标/手指所有点压力恒 0.5）；真压感笔保留原样，荧光笔不参与。
    // InkStyle：0=点集笔锋（收笔出锋），1=速率笔锋（快细慢粗），2=关闭笔锋，3=时间笔锋（落笔 250ms 内实时变粗）。

    /// <summary>时间笔锋自绘笔画提交（多指副设备 / 硬笔屏蔽模式）：先入集合（触发撤销与脏页标记），
    /// 再走与原生收笔同一套处理（冻结/笔锋/墨迹纠正）。控制器在此之后自行清场。</summary>
    private void CommitSelfDrawnStroke(PageInkCanvas canvas, Stroke stroke, int deviceId)
    {
        canvas.Strokes.Add(stroke);
        PageInkCanvas_StrokeCollected(canvas, new InkCanvasStrokeCollectedEventArgs(stroke));
    }

    /// <summary>批注笔画收集：按笔锋设置改写 StylusPoints 的假压力（与画板 inkCanvas_StrokeCollected 同款）。
    /// 时间笔锋（case 3）在下方提前处理：其封顶正是逐点硬件压力，必须先于真压感判定与笔锋改写返回。</summary>
    private void PageInkCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        try
        {
            // 双指手势抑制期间：原生管线偶尔仍会把被中断的笔画提交出来 → 整笔移除（CodeInput 门，不写撤销历史）。
            // 自绘笔画（多指副设备）不在此列。
            if (_inkGestureSuppressed && !e.Stroke.ContainsPropertyData(TimePressureController.SelfDrawnDeviceGuid)
                && sender is InkCanvas gestureCanvas && gestureCanvas.Strokes.Contains(e.Stroke))
            {
                var savedCommitType = _currentCommitType;
                _currentCommitType = CommitReason.CodeInput;
                try { gestureCanvas.Strokes.Remove(e.Stroke); }
                finally { _currentCommitType = savedCommitType; }
                return;
            }

            // 采样链路加固（只作用于本条笔画）：先修"跳出去又回来"的极端离群点，
            // 再对稀疏笔画单独关闭平滑，避免 FitToCurve 在稀疏/急变处过冲成"飞线"。
            StrokeSampleRepair.Apply(e.Stroke);
            StrokeSmoothingGuard.Apply(e.Stroke);

            // 荧光笔不参与笔锋（画板以 inkColor>100 判别，批注侧等价判据是 IsHighlighter）
            if (e.Stroke.DrawingAttributes.IsHighlighter)
                return;

            // 墨迹纠正（ICA InkToShape 同位置：荧光笔排除之后、压感/笔锋改写之前）：命中则整笔换成规范图形
            if (sender is PageInkCanvas shapeCanvas && shapeCanvas.DataContext is PageModel shapePage)
                _inkToShape.OnStrokeCollected(e.Stroke, shapePage.Strokes);

            // 时间笔锋：湿墨阶段已由叠加层按时间生长实时呈现，此处只需把各点最终压力定格写回
            if (_viewModel.InkTool.InkStyle == 3)
            {
                if (sender is PageInkCanvas timeCanvas)
                {
                    // 自绘笔画（多指副设备）按它自己的设备会话定格；原生笔画走主设备会话
                    if (e.Stroke.ContainsPropertyData(TimePressureController.SelfDrawnDeviceGuid))
                        timeCanvas.Controller.FreezeFinalize(
                            (int)e.Stroke.GetPropertyData(TimePressureController.SelfDrawnDeviceGuid),
                            e.Stroke, _viewModel.InkTool.PressureEnabled);
                    else
                        timeCanvas.Controller.FreezeFinalize(e.Stroke, _viewModel.InkTool.PressureEnabled);
                }
                return;
            }

            // 原生收墨笔画（主设备）的会话由控制器在抬笔后按 Background 优先级清场；
            // 多指书写时不能整页中止（会误杀其他手指的湿墨）。

            // 真压感判定（与压感归一合并为单次路径）：压感开启且存在真实压感输入（任一点压力≠0.5 且 ≠0）
            // → 保留硬件压力，不做假笔锋。压感关闭时跳过判定：下方笔锋公式输出只由位置/速度决定
            // （不读输入压力），原"先整笔 flatten 归一 0.5 → 再检测 → 再重建"的前置 flatten 对输出
            // 无贡献，省去一笔全量 StylusPointCollection 重建拷贝（case 0 的 n==1 例外在分支内处理）。
            if (_viewModel.InkTool.PressureEnabled)
            {
                foreach (var stylusPoint in e.Stroke.StylusPoints)
                {
                    if ((stylusPoint.PressureFactor > 0.501 || stylusPoint.PressureFactor < 0.5) && stylusPoint.PressureFactor != 0)
                        return;
                }
            }

            switch (_viewModel.InkTool.InkStyle)
            {
                case 1: // 速率笔锋
                    {
                        StylusPointCollection stylusPoints = new StylusPointCollection();
                        int n = e.Stroke.StylusPoints.Count - 1;

                        for (int i = 0; i <= n; i++)
                        {
                            double speed = GetAnnotationPointSpeed(
                                e.Stroke.StylusPoints[Math.Max(i - 1, 0)].ToPoint(),
                                e.Stroke.StylusPoints[i].ToPoint(),
                                e.Stroke.StylusPoints[Math.Min(i + 1, n)].ToPoint());
                            StylusPoint point = new StylusPoint();
                            if (speed >= 0.25)
                                point.PressureFactor = (float)(0.5 - 0.3 * (Math.Min(speed, 1.5) - 0.3) / 1.2);
                            else if (speed >= 0.05)
                                point.PressureFactor = (float)0.5;
                            else
                                point.PressureFactor = (float)(0.5 + 0.4 * (0.05 - speed) / 0.05);
                            point.X = e.Stroke.StylusPoints[i].X;
                            point.Y = e.Stroke.StylusPoints[i].Y;
                            stylusPoints.Add(point);
                        }
                        e.Stroke.StylusPoints = stylusPoints;
                    }
                    break;
                case 0: // 点集笔锋
                    {
                        StylusPointCollection stylusPoints = new StylusPointCollection();
                        int n = e.Stroke.StylusPoints.Count - 1;
                        double pressure = 0.1;
                        int x = 10;
                        if (n == 1)
                        {
                            // 2 点笔画不改写；压感关闭时仍需归一 0.5（等价原"flatten 先行"语义，
                            // 其余点数由下方重建统一覆盖，无需前置 flatten）
                            if (!_viewModel.InkTool.PressureEnabled)
                            {
                                var flattened2 = new StylusPointCollection();
                                foreach (var stylusPoint2 in e.Stroke.StylusPoints)
                                    flattened2.Add(new StylusPoint(stylusPoint2.X, stylusPoint2.Y, 0.5f));
                                e.Stroke.StylusPoints = flattened2;
                            }
                            return;
                        }
                        if (n >= x)
                        {
                            for (int i = 0; i < n - x; i++)
                            {
                                StylusPoint point = new StylusPoint
                                {
                                    PressureFactor = (float)0.5,
                                    X = e.Stroke.StylusPoints[i].X,
                                    Y = e.Stroke.StylusPoints[i].Y
                                };
                                stylusPoints.Add(point);
                            }
                            for (int i = n - x; i <= n; i++)
                            {
                                StylusPoint point = new StylusPoint
                                {
                                    PressureFactor = (float)((0.5 - pressure) * (n - i) / x + pressure),
                                    X = e.Stroke.StylusPoints[i].X,
                                    Y = e.Stroke.StylusPoints[i].Y
                                };
                                stylusPoints.Add(point);
                            }
                        }
                        else
                        {
                            for (int i = 0; i <= n; i++)
                            {
                                StylusPoint point = new StylusPoint
                                {
                                    PressureFactor = (float)(0.4 * (n - i) / n + pressure),
                                    X = e.Stroke.StylusPoints[i].X,
                                    Y = e.Stroke.StylusPoints[i].Y
                                };
                                stylusPoints.Add(point);
                            }
                        }
                        e.Stroke.StylusPoints = stylusPoints;
                    }
                    break;
                case 2: // 关闭笔锋：不改写；等宽由硬笔 IgnorePressure 在渲染层实现（RefreshTimePressureArmed 同步），不再抬笔归一
                    break;
            }
        }
        catch
        {
            // 笔锋属视觉增强，任何异常直接放弃改写，保留原笔画
        }
    }

    /// <summary>IUWM InfoBar 浮动通知（中下侧、默认成功样式）：新通知插到旧通知上方，滑入显示，3 秒后滑出消失。</summary>
    private const double FloatingTipSlideOffset = 30;

    public void ShowFloatingTip(string message,
        iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity severity = iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity.Success)
    {
        var bar = new iNKORE.UI.WPF.Modern.Controls.InfoBar
        {
            Severity = severity,
            Message = message,
            IsOpen = true,
            Margin = new Thickness(0, 0, 0, 8),   // 通知间距
            HorizontalAlignment = HorizontalAlignment.Center,   // 各条按自身内容独立宽度，且逐条居中
            MaxWidth = 700,                                     // 通知最宽 700px，超出换行
            RenderTransform = new TranslateTransform(),
        };
        FloatingTipPanel.Children.Insert(0, bar);   // 新通知在旧通知上方

        // 滑入：从下方浮起（Y +30→0）+ 淡入
        var transform = (TranslateTransform)bar.RenderTransform;
        var showDuration = new Duration(TimeSpan.FromMilliseconds(200));
        var showEase = new CircleEase();
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(FloatingTipSlideOffset, 0, showDuration) { EasingFunction = showEase });
        bar.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, showDuration) { EasingFunction = showEase });

        // 3 秒后滑出消失（每条通知独立计时）
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            AnimateFloatingTipClose(bar);
        };
        timer.Start();
    }

    /// <summary>单条通知消失动画：淡出 + 高度收合（底部间距同步收合）——上方通知随之平滑下落补位，200ms CircleEase；
    /// 结束后收起并移出容器。</summary>
    private void AnimateFloatingTipClose(iNKORE.UI.WPF.Modern.Controls.InfoBar bar)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(200));
        var ease = new CircleEase();
        var opacityAnim = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
        bar.BeginAnimation(OpacityProperty, opacityAnim);

        if (bar.ActualHeight < 1)
        {
            // 尚未完成布局（异常路径）：直接移除
            bar.IsOpen = false;
            FloatingTipPanel.Children.Remove(bar);
            return;
        }

        // 高度从当前值收合到 0、底部间距同步到 0：重排由布局驱动，上方通知下落过程平滑
        bar.BeginAnimation(HeightProperty,
            new DoubleAnimation(bar.ActualHeight, 0, duration) { EasingFunction = ease });
        bar.BeginAnimation(MarginProperty,
            new ThicknessAnimation(new Thickness(0, 0, 0, 8), new Thickness(0), duration) { EasingFunction = ease });

        var finished = false;
        void Finish()
        {
            if (finished) return;
            finished = true;
            bar.IsOpen = false;
            FloatingTipPanel.Children.Remove(bar);
        }
        opacityAnim.Completed += (_, _) => Finish();
    }

    /// <summary>相邻三点平均移动速度（ICA GetPointSpeed 原样）。</summary>
    private static double GetAnnotationPointSpeed(System.Windows.Point point1, System.Windows.Point point2, System.Windows.Point point3)
    {
        return (Math.Sqrt((point1.X - point2.X) * (point1.X - point2.X) + (point1.Y - point2.Y) * (point1.Y - point2.Y))
            + Math.Sqrt((point3.X - point2.X) * (point3.X - point2.X) + (point3.Y - point2.Y) * (point3.Y - point2.Y)))
            / 20;
    }

    /// <summary>面积擦按下：仅按橡皮大小设置 EraserShape（直径直接=EraserSize，对齐 ICA）。
    /// 手掌擦除中形状已由 PageCanvas_PalmTouchDown 按触点接触面积设好，不被橡皮粗细覆盖。
    /// 批处理累加器不在此预建——画板样式为 StrokesChanged 首事件自建（预建会在拖拽于画布外结束时滞留脏批次）。</summary>
    private void InkCanvas_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not InkCanvas inkCanvas)
            return;

        // 手掌擦除中：形状已由 PageCanvas_PalmTouchDown 按触点接触面积设好，不要被橡皮粗细覆盖
        if (_palmEraseCanvases.Contains(inkCanvas))
            return;

        if (_viewModel.InkTool.ActiveTool == InkToolMode.Eraser && !_viewModel.InkTool.IsStrokeEraser)
        {
            // 与 UpdateEraserShapes 同款补偿：页面坐标 ÷ DisplayScale，屏幕擦除洞恒为 EraserSize
            double size = _viewModel.InkTool.EraserSize / _viewModel.DisplayScale;
            inkCanvas.EraserShape = new RectangleStylusShape(size, InkCircleCursor.EraserBoardHeight(size));
        }
        // 墨迹擦：不重设形状（保持 5×5，画板同款——按下重设会把面积擦大矩形写回，点擦光标白方块复现）
    }

    /// <summary>是否手掌接触：橡皮菜单「手掌擦除」开关开、处于批注书写模式、非形状/选择遮罩、
    /// 接触宽度超阈值（笔尖档 10 / 手指档 30，由「笔尖模式」开关选择）。橡皮工具与其它模式一律不介入。</summary>
    private bool IsPalmTouch(TouchEventArgs e)
        => _viewModel.Settings.IsPalmEraseEnabled
           && _viewModel.InkTool.ActiveTool == InkToolMode.Pen
           && _viewModel.InkTool.ShapeDrawMode == 0
           && !IsAnySelectionCoverVisible
           // 反向笔尖无需特判：其接触面积远小于 10/30 阈值，天然不会命中（TouchEventArgs 无 StylusDevice 可查）
           && PalmEraseMetrics.IsTouchEraseAllowed()
           && PalmEraseMetrics.GetTouchBoundWidth(e)
              > (_viewModel.Settings.IsNibModeEnabled
                 ? _viewModel.Settings.NibModeBoundsWidth
                 : _viewModel.Settings.FingerModeBoundsWidth);

    /// <summary>手掌擦除（仅书写模式，画板 Main_Grid_TouchDown 等价）：摘 EditingMode 绑定 → EraseByPoint，
    /// 尺寸 = 触点接触面积 √(W×H) ÷DisplayScale 抵消整层缩放（判定仍按触点宽度阈值），
    /// 并把光标切为橡皮（UpdateInkCursor 按 _palmEraseCanvases 状态）。
    /// EditingMode 是 OneWay 绑定，只摘不接（接回会求值回 Ink 覆盖本地值），恢复的唯一出口是 InkCanvas_TouchUp。</summary>
    private void PageCanvas_PalmTouchDown(object? sender, TouchEventArgs e)
    {
        if (sender is not InkCanvas canvas || !_viewModel.InkTool.IsInkMode)
            return;
        if (canvas.EditingMode != InkCanvasEditingMode.Ink)
            return;                                   // 橡皮/选择/形状模式不介入
        if (_inkGestureSuppressed || !IsPalmTouch(e))
            return;                                   // 双指手势进行中让位

        // 手掌不是书写设备：丢弃它可能已建立的时间笔锋会话，防止手掌湿墨被画出来
        if (canvas is PageInkCanvas palmCanvas)
            palmCanvas.Controller.AbortStroke(e.TouchDevice.Id);

        // 手掌擦除尺寸 = 触点接触面积（对齐 ICA：仅 TouchDown 设一次，不随 TouchMove 逐帧更新）
        double size = PalmEraseMetrics.GetTouchFootprintSize(e) / _viewModel.DisplayScale;   // 页面坐标抵消缩放
        canvas.EraserShape = new RectangleStylusShape(size, InkCircleCursor.EraserBoardHeight(size));

        var modeProperty = InkCanvas.EditingModeProperty;                        // 照 CommitPendingPdfStroke 手法
        var binding = BindingOperations.GetBindingExpression(canvas, modeProperty);
        if (binding != null)
            BindingOperations.ClearBinding(canvas, modeProperty);
        canvas.EditingMode = InkCanvasEditingMode.EraseByPoint;

        _palmEraseCanvases.Add(canvas);
        UpdateInkCursor();   // 手掌擦除期间光标切为橡皮（尺寸=EraserSize），抬手/卸载恢复
    }

    /// <summary>面积擦抬起（鼠标）：提交并复位擦除批处理。</summary>
    private void InkCanvas_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not InkCanvas inkCanvas || inkCanvas.DataContext is not PageModel)
            return;

        FlushEraserBatch();
    }

    /// <summary>面积擦抬手（触摸/手写笔）：画板 Main_Grid_TouchUp 等价——提升的 MouseUp 时机不可靠，
    /// InkCanvas 收尾的 StrokesChanged 可能落在其后，必须显式调用同一提交入口。
    /// 手掌擦除中的画布另需先落账再把 EditingMode 绑定接回（恢复工具同步的唯一出口）。</summary>
    private void InkCanvas_TouchUp(object sender, TouchEventArgs e)
    {
        if (sender is not InkCanvas inkCanvas || inkCanvas.DataContext is not PageModel)
            return;

        FlushEraserBatch();

        if (_palmEraseCanvases.Remove(inkCanvas))
        {
            var modeProperty = InkCanvas.EditingModeProperty;
            var binding = BindingOperations.GetBindingExpression(inkCanvas, modeProperty);
            if (binding != null)
                BindingOperations.ClearBinding(inkCanvas, modeProperty);
            inkCanvas.EditingMode = _viewModel.InkTool.EditingMode;   // 先回显当前工具模式
            if (binding != null)
                BindingOperations.SetBinding(inkCanvas, modeProperty, binding.ParentBinding);   // 接回即恢复工具同步
            UpdateInkCursor();   // 手掌擦除结束：光标按当前工具恢复（归零→笔光标）
        }
    }

    /// <summary>累加器存在即无条件提交并复位（画板 inkCanvas_MouseUp L258-266 逐行等价）。</summary>
    private void FlushEraserBatch()
    {
        if (ReplacedStroke != null || AddedStroke != null)
        {
            _viewModel.InkTool.TimeMachine.CommitStrokeEraseHistory(ReplacedStroke!, AddedStroke!, _eraseTargetStrokes);
            AddedStroke = null;
            ReplacedStroke = null;
            _eraseTargetStrokes = null;
        }
    }

    /// <summary>双指手势成立时回滚本触摸的橡皮批处理（CodeInput 门，不入撤销历史）：
    /// 橡皮工具 + 双指缩放不应留下任何擦除痕迹（含第 1 指先落的窗口）。仅橡皮工具批处理，手掌擦除批处理不回滚。</summary>
    private void RevertEraserBatchForGesture()
    {
        if (_viewModel.InkTool.ActiveTool != InkToolMode.Eraser
            || _eraseTargetStrokes == null || (ReplacedStroke == null && AddedStroke == null))
            return;
        var savedCommitType = _currentCommitType;
        _currentCommitType = CommitReason.CodeInput;
        try
        {
            if (AddedStroke is { Count: > 0 }) _eraseTargetStrokes.Remove(AddedStroke);    // 先撤拆分片段
            if (ReplacedStroke is { Count: > 0 }) _eraseTargetStrokes.Add(ReplacedStroke); // 再回补原笔画
        }
        finally
        {
            _currentCommitType = savedCommitType;
            AddedStroke = null;
            ReplacedStroke = null;
            _eraseTargetStrokes = null;
        }
    }

    /// <summary>页面笔画变更（画板 MW_TimeMachine.StrokesOnStrokesChanged 逐行等价：UserInput / Clear / ShapeRecognition）：
    /// 回放门在最顶（CodeInput 等价，必须在批处理分支之前）；面积擦平铺累加器；绘制逐事件提交；墨迹纠正合成单条历史。</summary>
    private void PageStrokes_StrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        if (sender is not StrokeCollection strokes || !_trackedStrokePages.ContainsKey(strokes))
            return;
        // 抬笔/擦除/纠正/撤销回放全部标脏（置于回放门之前，去抖吸收中间态；D3）
        MarkPdfDirty(_trackedStrokePages[strokes]);
        // 选择撤销追踪：笔画级事件订阅（画板 StrokesOnStrokesChanged L216-229 等价，须在回放门之前登记，
        // 回放期间的 Added/Removed 也要完成订阅簿记）
        foreach (var stroke in e.Removed)
        {
            stroke.StylusPointsChanged -= Stroke_StylusPointsChanged;
            stroke.StylusPointsReplaced -= Stroke_StylusPointsReplaced;
            stroke.DrawingAttributesChanged -= Stroke_DrawingAttributesChanged;
            StrokeInitialHistory.Remove(stroke);
            _strokePage.Remove(stroke);
        }
        foreach (var stroke in e.Added)
        {
            stroke.StylusPointsChanged += Stroke_StylusPointsChanged;
            stroke.StylusPointsReplaced += Stroke_StylusPointsReplaced;
            stroke.DrawingAttributesChanged += Stroke_DrawingAttributesChanged;
            StrokeInitialHistory[stroke] = stroke.StylusPoints.Clone();
            _strokePage[stroke] = _trackedStrokePages[strokes];
        }
        if (_currentCommitType == CommitReason.CodeInput || _currentCommitType == CommitReason.ShapeDrawing)
            return;   // 画板 L228 等价：回放门 + 形状预览门

        // 面积擦：原笔 Removed + 拆分 Added 都记入平铺累加器，MouseUp/TouchUp 统一提交（画板 L229-236 等价）；
        // 手掌擦除虽在书写模式下触发，但同样是 EraseByPoint 拆分，必须走同一累加器否则撤销错乱
        if ((e.Added.Count != 0 || e.Removed.Count != 0)
            && (_viewModel.InkTool.ActiveTool == InkToolMode.Eraser || _palmEraseCanvases.Count > 0))
        {
            AddedStroke ??= new StrokeCollection();
            ReplacedStroke ??= new StrokeCollection();
            _eraseTargetStrokes ??= strokes;
            AddedStroke.Add(e.Added);
            ReplacedStroke.Add(e.Removed);
            return;
        }

        if (e.Added.Count != 0)
        {
            if (_currentCommitType == CommitReason.ShapeRecognition)
            {
                // 墨迹纠正：移除（暂存 ReplacedStroke）+ 添加（生成图形）合成一条 ShapeRecognition 历史（画板 L239-244 等价）
                _viewModel.InkTool.TimeMachine.CommitStrokeShapeHistory(_shapeRecognitionReplaced!, e.Added, strokes);
                _shapeRecognitionReplaced = null;
            }
            else
            {
                _viewModel.InkTool.TimeMachine.CommitStrokeUserInputHistory(e.Added, strokes);
            }
        }
        else if (e.Removed.Count != 0)
        {
            if (_currentCommitType == CommitReason.ShapeRecognition)
            {
                _shapeRecognitionReplaced = e.Removed;   // 等随后的 Added 一起提交（画板 L254-257 等价）
                return;
            }
            _viewModel.InkTool.TimeMachine.CommitStrokeEraseHistory(e.Removed, targetStrokes: strokes);
        }
    }

    // ══ 墨迹持久化：标脏去抖 / 恢复 / 关闭 flush ══

    /// <summary>标脏一页 PDF 墨迹（300ms 去抖后写库；无库不标）。</summary>
    private void MarkPdfDirty(int page)
    {
        if (_viewModel.InkStore == null || page < 0 || page >= _viewModel.Pages.Count)
            return;
        _dirtyPdfPages.Add(page);
        RestartInkSaveTimer();
    }

    /// <summary>重启 300ms 去抖（连续书写合并为一次序列化）。</summary>
    private void RestartInkSaveTimer()
    {
        _inkSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _inkSaveTimer.Stop();
        _inkSaveTimer.Tick -= OnInkSaveTick;
        _inkSaveTimer.Tick += OnInkSaveTick;
        _inkSaveTimer.Start();
    }

    /// <summary>去抖到点：UI 线程同步序列化脏页/画板当前槽位并入队（SQL 后台任务链执行）。</summary>
    private void OnInkSaveTick(object? sender, EventArgs e)
    {
        _inkSaveTimer!.Stop();
        var store = _viewModel.InkStore;
        if (store == null)
        {
            _dirtyPdfPages.Clear();
            _dirtyBoardInk = false;
            return;
        }
        foreach (var page in _dirtyPdfPages)
            store.SavePage(page, _viewModel.Pages[page].Strokes);   // UI 线程同步序列化（D2）
        _dirtyPdfPages.Clear();
        if (_dirtyBoardInk)
        {
            _dirtyBoardInk = false;
            Board.PersistCurrentSlot();   // 当前槽位↔当前画布配对恒正确（D4）
        }
    }

    /// <summary>文档打开恢复（DocumentOpened 订阅）：PDF 页墨迹 + 画板槽位/页签；恢复内容不进撤销历史（D7）。
    /// 单条 BLOB 损坏仅记日志跳过，不阻塞打开（对齐 InkStore 容错风格）。
    /// InkSerializer 必须 UI 线程 → 按 8ms 时间片分帧，片间让帧避免 ProgressRing 动画冻结。</summary>
    private async Task RestoreInkData(InkRestoreData data)
    {
        try
        {
            var prev = _currentCommitType;
            _currentCommitType = CommitReason.CodeInput;   // 回放门：恢复不记入撤销历史
            try
            {
                var sw = Stopwatch.StartNew();
                long lastSlice = 0;
                foreach (var (index, bytes) in data.PageInks)
                {
                    if (index < 0 || index >= _viewModel.Pages.Count)
                        continue;
                    try
                    {
                        var strokes = InkStoreService.Deserialize(bytes);
                        _viewModel.Pages[index].Strokes.Add(strokes);
                        // 早于 InkCanvas_Loaded 的笔画不触发 StrokesChanged → 手动补齐与 add 循环同款笔画级簿记
                        foreach (var stroke in strokes)
                        {
                            stroke.StylusPointsChanged += Stroke_StylusPointsChanged;
                            stroke.StylusPointsReplaced += Stroke_StylusPointsReplaced;
                            stroke.DrawingAttributesChanged += Stroke_DrawingAttributesChanged;
                            StrokeInitialHistory[stroke] = stroke.StylusPoints.Clone();
                            _strokePage[stroke] = index;
                        }
                    }
                    catch (Exception ex)
                    {
                        App.Log($"RestoreInkData p{index}", ex);   // 单条 BLOB 损坏：跳过该页，其余页/画板继续
                    }

                    if (sw.ElapsedMilliseconds - lastSlice >= InkRestoreSliceMs)
                    {
                        await Dispatcher.Yield(DispatcherPriority.Background);   // 让 Render 帧跑（圆圈动画）
                        lastSlice = sw.ElapsedMilliseconds;
                    }
                }
            }
            finally { _currentCommitType = prev; }

            Board.LoadPersistedBoard(data.BoardSlots, data.ViewState);
        }
        catch (Exception ex)
        {
            App.Log("RestoreInkData", ex);   // 恢复失败不阻塞打开（墨迹缺失可接受，读写错误不可接受）
        }
        _dirtyPdfPages.Clear();
        _dirtyBoardInk = false;
    }

    /// <summary>关闭/切换文档兜底保存（DocumentClosing 订阅 + OnClosed 调用；InkStoreService.Dispose 有守卫，重入安全）。</summary>
    private void FlushInkStore()
    {
        _inkSaveTimer?.Stop();
        var store = _viewModel.InkStore;
        if (store == null)
        {
            _dirtyPdfPages.Clear();
            _dirtyBoardInk = false;
            return;
        }

        // ① 显示位置（ZoomLevel / 滚动偏移 / 当前页 / 画板页签）
        var (bc, bt) = Board.GetBoardPageState();
        store.SaveViewState(new Dictionary<string, string>
        {
            ["pdf_zoom"] = _viewModel.ZoomLevel.ToString(CultureInfo.InvariantCulture),
            ["pdf_offset"] = (_pdfScrollViewer?.VerticalOffset ?? 0).ToString(CultureInfo.InvariantCulture),
            ["pdf_page"] = _viewModel.CurrentPage.ToString(CultureInfo.InvariantCulture),
            ["board_current"] = bc.ToString(CultureInfo.InvariantCulture),
            ["board_total"] = bt.ToString(CultureInfo.InvariantCulture),
        });
        store.TrimBoardSlots(bt);   // 清陈旧槽位（任务链排在 SaveViewState 之后，D9）

        // ② 去抖窗口内未及写入的墨迹（画布此刻即最终态）
        foreach (var page in _dirtyPdfPages)
            if (page >= 0 && page < _viewModel.Pages.Count)
                store.SavePage(page, _viewModel.Pages[page].Strokes);
        _dirtyPdfPages.Clear();
        _dirtyBoardInk = false;
        Board.PersistCurrentSlot();   // 画板模式中直接关闭窗口的兜底（内部判 currentMode==1）

        // ③ 排空 + WAL checkpoint + 关库
        store.Flush();
        store.Dispose();
    }

    protected override void OnClosed(EventArgs e)
    {
        FlushInkStore();   // 未走 GoHome/切换的直接关闭（含画板模式中关闭）
        _inkSaveTimer?.Stop();
        // 退出自动安装：开关开启且有已下载更新 → /VERYSILENT 后台安装（不显示窗口、不重启；
        // 实例等待/防多开归 .iss；「立即更新」启动过安装器则跳过，避免二次安装；
        // 其他实例已通过命名互斥体接管安装时也跳过，防止抢跑 /VERYSILENT）
        try
        {
            var s = _viewModel.Settings;
            bool autoCheck = s.IsAutoCheckUpdate;
            bool launched = UpdateService.InstallLaunched;
            bool claimedOther = UpdateService.IsInstallClaimedByOther();
            bool pending = UpdateService.HasPendingInstall(s);
            ConsoleLog.Write("应用更新", $"退出安装判定: 自动更新={autoCheck}, 已启动安装={launched}, 他方接管={claimedOther}, 待装={pending}");
            if (autoCheck && !launched && !claimedOther && pending)
                UpdateService.StartInstall(UpdateService.GetSetupPath(), restart: false);
        }
        catch (Exception ex)
        {
            App.Log("Update.ExitInstall", ex);
        }
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
