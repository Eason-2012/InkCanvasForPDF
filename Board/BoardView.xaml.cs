#nullable disable warnings

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using InkCanvasForPDF.Controls;
using InkCanvasForPDF.Services;
using InkCanvasForPDF.ViewModels;

namespace InkCanvasForPDF.Board
{
    /// <summary>画板视图：整块移植自 Ink-Canvas-Artistry 4.9.4 的画板区段。
    /// 元素名保留 ICA 原名，Board/MainWindow_cs/*.cs 以 partial BoardView 形式直接引用。</summary>
    public partial class BoardView : UserControl
    {
        /// <summary>请求退出画板（ImageBlackboard_Click 触发，由 MainWindow 订阅退出画板模式）。</summary>
        public event EventHandler? ExitRequested;

        /// <summary>橡皮直径（DIP）：由 MainWindow 从 InkCanvasForPDF 设置（EraserSizeSetting 经 15+3.5x 换算）注入，
        /// 替代 ICA 的 EraserSize 0~4 档 × 90，使画板与 PDF 批注的橡皮粗细一致。</summary>
        public static double EraserDiameter { get; set; } = 50;

        public BoardView()
        {
            InitializeComponent();
            // 墨迹纠正引擎（识别后端 WinRT）。字段初始化器不能引用实例字段，故在构造函数内装配；
            // 开关初值由 MainWindow 注入（ApplyInkToShape），默认关闭与 AppSettings 的启动同步一致。
            _inkToShape = new InkToShapeEngine(() => inkCanvas.DefaultDrawingAttributes, ApplyShapeReplacement);
            // 墨迹菜单"点外关闭"：PreviewMouseDown + handledEventsToo 覆盖一切点击（含已被处理的）；
            // 点在菜单面板内或对应按钮上不关（按钮留给各自的 Click 处理器自己 toggle）。
            // 必须显式转 MouseButtonEventHandler：方法组会被推断为 Action<,>，AddHandler 运行时抛"处理程序类型不匹配"。
            AddHandler(UIElement.PreviewMouseDownEvent, (MouseButtonEventHandler)BoardOutsideCloseHandler, true);

            // FlyIn 在 Popup.Opened 后经 Dispatcher（Loaded 优先级）再启动：Popup 首次打开时子树尚未完成
            // 加载/呈现，若在 IsOpen=true 同帧启动 Storyboard，150ms 时钟已走，等首帧呈现动画已过半。
            // 初态（暗+偏下）在 PenIcon_Click / TwoFingerGestureBorder_Click 打开前预设。
            BoardPenPalettePopup.Opened += (_, _) =>
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FlyInBoardPalette));
            BoardGesturePopup.Opened += (_, _) =>
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FlyInGesturePanel));

            // 图形面板 Popup 定位（照抄 PDF ShapePalette 回调）：水平居中于图形按钮、底缘悬于其上方 8px；
            // PopupPrimaryAxis.Vertical 让屏幕边缘自动翻转。FlyIn 启动时机同 PenPalette（Opened 经 Dispatcher）。
            BoardShapePalette.CustomPopupPlacementCallback =
                (popupSize, targetSize, offset) => new[]
                {
                    new CustomPopupPlacement(
                        new Point(targetSize.Width / 2 - popupSize.Width / 2, -popupSize.Height - 8),
                        PopupPrimaryAxis.Vertical)
                };
            BoardShapePalette.Opened += (_, _) =>
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FlyInBoardShapePalette));

            // 橡皮面板 Popup 定位（照抄 BoardShapePalette / PDF EraserPalette 回调）：水平居中于橡皮按钮、底缘悬于其上方 8px；
            // PopupPrimaryAxis.Vertical 让屏幕边缘自动翻转。FlyIn 启动时机同其余面板（Opened 经 Dispatcher）。
            BoardEraserPalette.CustomPopupPlacementCallback =
                (popupSize, targetSize, offset) => new[]
                {
                    new CustomPopupPlacement(
                        new Point(targetSize.Width / 2 - popupSize.Width / 2, -popupSize.Height - 8),
                        PopupPrimaryAxis.Vertical)
                };
            BoardEraserPalette.Opened += (_, _) =>
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FlyInBoardEraserPalette));
            // 滑动清空滑块回弹变换（拖动/回弹动画统一作用于该 Transform，照 PDF InkToolBar 构造器）
            BoardClearInkThumb.RenderTransform = _boardClearInkTransform;
            // 橡皮菜单页签回填：恢复持久化的面积擦/墨迹擦（DataContext 由 MainWindow 继承，构造期间尚为 null，
            // 故放 Loaded；isLoaded 此前已在构造器末尾置 true）
            Loaded += (_, _) =>
            {
                if (Vm is not { } vm) return;
                forcePointEraser = !vm.InkTool.IsStrokeEraser;
                BoardPivotEraser.SelectedIndex = vm.InkTool.IsStrokeEraser ? 1 : 0;
            };
            // 图形面板提示文字与选中高亮随主题刷新；Loaded 先按当前主题设一次初值
            Loaded += (_, _) => ApplyShapeHintColor();
            ThemeService.ThemeChanged += () =>
            {
                ApplyShapeHintColor();
                UpdateBoardShapeItemHighlight(drawingShapeMode);
            };

            // ICA MainWindow 构造器的画板相关初始化（浮动栏/PPT/工具面板不适用；
            // 手势面板已是 Popup，IsOpen 默认 False，无需显式 Collapsed）
            GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;

            // 对齐 ICA Window_Loaded：先备画笔属性 → 读设置 → 放行 isLoaded 守卫的处理器
            loadPenCanvas();
            LoadSettings(true);
            // 恢复手势开关（保存随 Toggled 已落盘；此处仅回读。isLoaded 尚为 false，Toggled 处理器 no-op）
            ToggleSwitchEnableMultiTouchMode.IsOn = Settings.Gesture.IsEnableMultiTouchMode;
            ToggleSwitchEnableTwoFingerZoom.IsOn = Settings.Gesture.IsEnableTwoFingerZoom;
            ToggleSwitchEnableTwoFingerTranslate.IsOn = Settings.Gesture.IsEnableTwoFingerTranslate;
            ToggleSwitchEnableTwoFingerRotation.IsOn = Settings.Gesture.IsEnableTwoFingerRotation;
            // 粗细/不透明度滑块回读（ICA MW_SettingsToLoad.cs:398-401 同款；粗细滑块显示值 = InkWidth ×2）。
            // isLoaded 尚为 false，ValueChanged 处理器 no-op，不会回写/落盘。
            BoardInkWidthSlider.Value = Math.Clamp(Settings.Canvas.InkWidth * 2, 1, 30);
            BoardInkAlphaSlider.Value = Math.Clamp(Settings.Canvas.InkAlpha, 20, 240);
            // 墨迹菜单初始页签：恢复上次启用的硬笔/软笔（荧光笔不记录；isLoaded 前 SelectionChanged 处理器 no-op）
            boardInkType = Math.Clamp(Settings.Canvas.LastPenKind, 0, 1);
            BoardPivotInk.SelectedIndex = boardInkType;
            isLoaded = true;
            // 多指书写运行时接线（事件订阅 + isInMultiTouchMode）按保存值重建；
            // !isInMultiTouchMode 幂等守卫：防回填/其它路径已启用后被翻转语义重复 toggle 关掉
            if (Settings.Gesture.IsEnableMultiTouchMode && !isInMultiTouchMode)
                BorderMultiTouchMode_MouseUp(null, null);
            CheckEnableTwoFingerGestureBtnColorPrompt();   // ICA MW_SettingsToLoad.cs:404 同款调用

            InitBoardCore();   // MW_BoardCore：撤销/重做状态订阅 + 笔画历史跟踪
            InitializePauseInkToShape();   // MW_InkToShape：停顿强制识别追踪器挂到 inkCanvas（配置由 MainWindow 推送）
        }

        /// <summary>窗口失去焦点时关闭画板墨迹菜单、手势菜单、图形菜单与橡皮菜单（MainWindow.Deactivated 调用；
        /// 播 150ms 滑出动画后真正关闭）。</summary>
        public void CloseMenusOnDeactivate()
        {
            PopupFlyAnimator.FlyOut(BoardPenPalettePopup, BoardPenPalette);
            PopupFlyAnimator.FlyOut(BoardGesturePopup, BoardTwoFingerGestureBorder);
            PopupFlyAnimator.FlyOut(BoardShapePalette, BoardBorderDrawShape);
            PopupFlyAnimator.FlyOut(BoardEraserPalette, BoardEraserPaletteRoot);
        }

        /// <summary>墨迹/手势菜单"点外关闭"处理器（对齐 PDF OutsideCloseHandler）：点击源在面板内或
        /// 对应按钮上豁免（按钮留给各自 Click 处理器 toggle，否则按下即关、Click 又开，开关失效），
        /// 其余一律关闭。Popup 内容事件沿逻辑树隧道经过 BoardView，必须保留面板豁免。</summary>
        private void BoardOutsideCloseHandler(object sender, MouseButtonEventArgs e)
        {
            if (BoardPenPalettePopup.IsOpen
                && e.OriginalSource is DependencyObject d
                && !IsVisualDescendantOf(d, BoardPenPalette)
                && !IsVisualDescendantOf(d, BoardPen))
            {
                PopupFlyAnimator.FlyOut(BoardPenPalettePopup, BoardPenPalette);   // 滑出动画幂等，重复点外不叠加
            }
            if (BoardGesturePopup.IsOpen
                && e.OriginalSource is DependencyObject g
                && !IsVisualDescendantOf(g, BoardTwoFingerGestureBorder)
                && !IsVisualDescendantOf(g, BoardGestureBtn))
            {
                PopupFlyAnimator.FlyOut(BoardGesturePopup, BoardTwoFingerGestureBorder);
            }
            if (BoardEraserPalette.IsOpen
                && e.OriginalSource is DependencyObject s
                && !IsVisualDescendantOf(s, BoardEraserPaletteRoot)
                && !IsVisualDescendantOf(s, BoardEraser))
            {
                PopupFlyAnimator.FlyOut(BoardEraserPalette, BoardEraserPaletteRoot);   // 滑出动画幂等，重复点外不叠加
            }
        }

        /// <summary>画板主视图的数据上下文（MainWindow.DataContext=MainViewModel 自动继承；
        /// 构造期间为 null，仅在事件处理器/Loaded 内使用）。</summary>
        private MainViewModel? Vm => DataContext as MainViewModel;

        /// <summary>视觉树向上查找祖先。</summary>
        private static bool IsVisualDescendantOf(DependencyObject? node, DependencyObject ancestor)
        {
            while (node != null)
            {
                if (ReferenceEquals(node, ancestor))
                    return true;
                node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }
            return false;
        }

        /// <summary>墨迹菜单 Popup 定位回调（照抄 PDF PenPalette.CustomPopupPlacementCallback）：
        /// 水平居中于墨迹按钮、底缘悬于其上方 8px；PopupPrimaryAxis.Vertical 让屏幕边缘自动翻转。</summary>
        private CustomPopupPlacement[] BoardPenPalettePopup_CustomPopupPlacement(Size popupSize, Size targetSize, Point offset)
            => new[]
            {
                new CustomPopupPlacement(
                    new Point(targetSize.Width / 2 - popupSize.Width / 2, -popupSize.Height - 8),
                    PopupPrimaryAxis.Vertical)
            };

        /// <summary>面板滑入动画（纯滑入：整块从下方滑入，150ms CircleEase EaseOut 由快到慢，无淡入）。
        /// 由 BoardPenPalettePopup.Opened 经 Dispatcher（Loaded 优先级）调用：Popup 子树就绪后动画才启动，
        /// 首次打开播放完整（初态已在打开前预设）。动画统一实现在 PopupFlyAnimator。</summary>
        private void FlyInBoardPalette()
            => PopupFlyAnimator.FlyIn(BoardPenPalettePopup, BoardPenPalette);

        /// <summary>手势菜单 Popup 定位回调（同墨迹菜单：水平居中于手势按钮、底缘悬于其上方 8px；
        /// PopupPrimaryAxis.Vertical 让屏幕边缘自动翻转）。</summary>
        private CustomPopupPlacement[] BoardGesturePopup_CustomPopupPlacement(Size popupSize, Size targetSize, Point offset)
            => new[]
            {
                new CustomPopupPlacement(
                    new Point(targetSize.Width / 2 - popupSize.Width / 2, -popupSize.Height - 8),
                    PopupPrimaryAxis.Vertical)
            };

        /// <summary>手势面板滑入动画（纯滑入：整块从下方滑入，150ms CircleEase EaseOut 由快到慢，无淡入）。
        /// 由 BoardGesturePopup.Opened 经 Dispatcher（Loaded 优先级）调用，初态已在打开前预设。
        /// 动画统一实现在 PopupFlyAnimator。</summary>
        private void FlyInGesturePanel()
            => PopupFlyAnimator.FlyIn(BoardGesturePopup, BoardTwoFingerGestureBorder);

        /// <summary>橡皮面板滑入动画（同 FlyInBoardShapePalette：由 Opened 经 Dispatcher（Loaded 优先级）调用，
        /// 初态已在打开前预设）。动画统一实现在 PopupFlyAnimator。</summary>
        private void FlyInBoardEraserPalette()
            => PopupFlyAnimator.FlyIn(BoardEraserPalette, BoardEraserPaletteRoot);

        /// <summary>进入画板：显示黑板底 + 三条 BoardBar，还原上次画板墨迹，建立画笔模式。
        /// 对齐 ICA PenIcon_Click 的笔模式建立 + BtnSwitch_Click case 1。
        /// 注：不做 ICA 的 SaveStrokes(true)/RestoreStrokes(true) 桌面交接——InkCanvasForPDF 的 PDF 批注画布
        /// 与画板画布互相独立，没有"桌面批注层"，画板墨迹只在本槽位（CurrentWhiteboardIndex）存取。</summary>
        public void EnterBoard()
        {
            currentMode = 1;

            // PenIcon_Click 笔模式建立的等价操作（BoardPen 是 Border 且 XAML 已设 Background，
            // 非 null → 直接调 PenIcon_Click 只会弹调色板，不会建立画笔模式）
            Main_Grid.Background = new SolidColorBrush(StringToColor("#01FFFFFF"));
            inkCanvas.IsHitTestVisible = true;
            inkCanvas.Visibility = Visibility.Visible;
            GridBackgroundCoverHolder.Visibility = Visibility.Visible;
            GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;

            // BtnSwitch_Click case 1：显示黑板底与三条工具条
            GridBackgroundCover.Visibility = Visibility.Visible;
            Helpers.AnimationsHelper.ShowWithSlideFromBottomAndFade(BlackboardLeftSide);
            Helpers.AnimationsHelper.ShowWithSlideFromBottomAndFade(BlackboardCenterSide);
            Helpers.AnimationsHelper.ShowWithSlideFromBottomAndFade(BlackboardRightSide);

            RestoreStrokes();      // 还原本槽位上次退出时保存的墨迹
            // 恢复上次启用的硬笔/软笔及其粗细（荧光笔不记录）；颜色按画布：黑底白笔、白底黑笔；
            // 压感随硬/软笔联动（复用现有 ColorSwitchCheck/CheckColorTheme 刷新，逻辑保留）
            ApplyBoardInkType(Math.Clamp(Settings.Canvas.LastPenKind, 0, 1), canvasColorOverride: true);
            ColorSwitchCheck();    // 内含 EditingMode=Ink + CheckColorTheme + HideSubPanels("color")
            HideSubPanels("pen", true);

            // ICA ImageBlackboard_Click 进入分支：自动关闭多指书写、开启双指移动
            if (Settings.Gesture.AutoSwitchTwoFingerGesture)
            {
                ToggleSwitchEnableTwoFingerTranslate.IsOn = true;
                if (isInMultiTouchMode) ToggleSwitchEnableMultiTouchMode.IsOn = false;
            }

            CheckColorTheme(true);   // ICA 进入画板末尾调用：定黑板底色 + 亮系色板
            SyncBoardInkControls();  // 回填墨迹菜单滑块/下拉框（按恢复的类型记忆值）
        }

        /// <summary>退出画板：本槽位墨迹存档并清屏，收起三条 BoardBar 与黑板底。</summary>
        public void ExitBoard()
        {
            currentMode = 0;

            if (inkCanvas.EditingMode == InkCanvasEditingMode.Select)
            {
                inkCanvas.Select(new StrokeCollection());
                GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;
            }

            SaveStrokes();         // 本页墨迹 → TimeMachineHistories[CurrentWhiteboardIndex]
            ClearStrokes(true);
            HideSubPanelsImmediately();
            PopupFlyAnimator.FlyOut(BoardShapePalette, BoardBorderDrawShape);   // Popup 不随按钮条隐藏，退出画板显式关（含图形菜单，滑出动画）
            PopupFlyAnimator.FlyOut(BoardEraserPalette, BoardEraserPaletteRoot);   // 橡皮菜单同理显式关

            GridBackgroundCover.Visibility = Visibility.Collapsed;
            Helpers.AnimationsHelper.HideWithSlideAndFade(BlackboardLeftSide);
            Helpers.AnimationsHelper.HideWithSlideAndFade(BlackboardCenterSide);
            Helpers.AnimationsHelper.HideWithSlideAndFade(BlackboardRightSide);
            ResetToolIcons();
        }

        /// <summary>画板栏缩放/不透明度缓存（ApplyBoardBarLayout 写入）：
        /// 图形面板 Popup 化后不在 Viewbox 内，打开时用缩放缓存自行应用（不套 PDF 的 0.65）；
        /// 选中态高亮块透明度用不透明度缓存。</summary>
        private double _boardBarScale = 1;
        private double _boardBarOpacity = 0.75;

        /// <summary>画板墨迹栏布局（设置：缩放 + 底部边距 + 透明度）：三组 ScaleTransform 同步缩放；
        /// 三条 BoardBar 的 Viewbox 只改 Margin 底部分量（保留 Left/Top/Right）；三条 Grid 统一 Opacity。</summary>
        public void ApplyBoardBarLayout(double scale, double bottomMargin, double opacity)
        {
            _boardBarScale = scale;
            _boardBarOpacity = opacity;
            ViewboxBlackboardLeftSideScaleTransform.ScaleX = scale;
            ViewboxBlackboardLeftSideScaleTransform.ScaleY = scale;
            ViewboxBlackboardCenterSideScaleTransform.ScaleX = scale;
            ViewboxBlackboardCenterSideScaleTransform.ScaleY = scale;
            ViewboxBlackboardRightSideScaleTransform.ScaleX = scale;
            ViewboxBlackboardRightSideScaleTransform.ScaleY = scale;
            foreach (var vb in new[] { ViewboxBlackboardLeftSide, ViewboxBlackboardCenterSide, ViewboxBlackboardRightSide })
            {
                var m = vb.Margin;
                vb.Margin = new Thickness(m.Left, m.Top, m.Right, bottomMargin);
            }
            var clamped = Math.Clamp(opacity, 0, 1);
            // 不透明度只作用于三条按钮条；弹窗（墨迹选项/点击清屏/手势面板）在 Grid 内但不在按钮条里，恒不透明（对齐 PDF 侧）
            BlackboardLeftBarPanel.Opacity = clamped;
            BlackboardCenterBarPanel.Opacity = clamped;
            BlackboardRightBarPanel.Opacity = clamped;
        }

        /// <summary>画笔光标开关（主设置 isShowCursor；由 MainWindow 注入，替代黑板本地 ICA Settings.Canvas.IsShowCursor）。</summary>
        public bool PenCursorEnabled { get; set; } = false;

        /// <summary>按当前开关与编辑模式重放光标（设置变更即时生效）。</summary>
        public void ApplyPenCursor() =>
            inkCanvas.ForceCursor = PenCursorEnabled && inkCanvas.EditingMode == InkCanvasEditingMode.Ink;

        private InkCircleCursor? _eraserCursor;        // 黑板擦光标缓存（尺寸没变则复用）
        private double _eraserCursorWidth = -1;

        /// <summary>按 EraserDiameter 重建黑板擦光标并按编辑模式应用：面积擦显示自定义黑板擦（38:56，
        /// 与 PDF 批注侧同款），其余模式交还 WPF 内置（Cursor="Pen"）。橡皮粗细设置变化时由 MainWindow 调用。</summary>
        public void ApplyEraserCursor()
        {
            double width = EraserDiameter;
            if (_eraserCursor == null || Math.Abs(_eraserCursorWidth - width) >= 1.0)
            {
                double dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                var created = InkCircleCursor.CreateEraserBoard(width, dpi);
                if (created != null)
                {
                    var old = _eraserCursor;
                    _eraserCursor = created;   // 先赋新再释放旧：避免销毁仍被画布引用的光标句柄
                    _eraserCursorWidth = width;
                    old?.Dispose();
                }
            }

            if (inkCanvas.EditingMode == InkCanvasEditingMode.EraseByPoint && _eraserCursor != null)
            {
                inkCanvas.UseCustomCursor = true;   // UseCustomCursor=true 时 InkCanvas 不再拦截 QueryCursor
                inkCanvas.Cursor = _eraserCursor.Cursor;
            }
            else
            {
                inkCanvas.UseCustomCursor = false;
                inkCanvas.Cursor = Cursors.Pen;   // 还原 XAML 默认（照 MainWindow.UpdateInkCursor 同款修复）：
                                                  // 面积擦曾把 Cursor 覆盖为黑板擦位图，残留会让切到墨迹擦/笔后仍显示旧橡皮光标
            }
        }

        /// <summary>橡皮粗细变化时按 EraserDiameter 重建面积擦 EraserShape（光标由 ApplyEraserCursor 同步；
        /// 仅面积擦激活时应用，墨迹擦保持 5×5，与 EraserIcon_Click / ApplyBoardEraserMode 同款 38:56 黑板擦矩形）。</summary>
        public void ApplyEraserShape()
        {
            if (forcePointEraser && inkCanvas.EditingMode == InkCanvasEditingMode.EraseByPoint)
                inkCanvas.EraserShape = new RectangleStylusShape(EraserDiameter, InkCircleCursor.EraserBoardHeight(EraserDiameter));
        }

        /// <summary>墨迹平滑（FitToCurve，主设置 isFitToCurve；由 MainWindow 注入，作用于新笔画）。</summary>
        public void ApplyFitToCurve(bool fitToCurve) =>
            inkCanvas.DefaultDrawingAttributes.FitToCurve = fitToCurve;

        private bool _pressureEnabled = true;

        /// <summary>启用压感（主设置 isPressureEnabled；由 MainWindow 注入，作用于新笔画）。</summary>
        public void ApplyPressureEnabled(bool pressureEnabled)
        {
            _pressureEnabled = pressureEnabled;
            RefreshTimePressureArmed();
        }

        /// <summary>时间笔锋墨迹点长满时间（ms）：与 PDF 批注侧共享同一设置，由 MainWindow 注入。</summary>
        public double TimePenGrowMs { get; set; } = 100.0;

        /// <summary>注入长满时间并即时同步控制器（MainWindow 在启动与设置变更时调用）。</summary>
        public void ApplyTimePenGrowMs(double ms)
        {
            TimePenGrowMs = ms;
            RefreshTimePressureArmed();
        }

        /// <summary>画板主题切换：在 UserControl.Resources.MergedDictionaries 中原地替换 Light-Board/Dark-Board
        /// （移植 ICA SetBoardTheme；保留本地合并——MW_BoardCore 的 BoardBarBackgroundBrush 依赖控件级查找）。</summary>
        public void SetBoardTheme(bool dark)
        {
            string target = dark ? "Dark-Board.xaml" : "Light-Board.xaml";
            string other = dark ? "Light-Board.xaml" : "Dark-Board.xaml";
            var dictionaries = Resources.MergedDictionaries;
            if (dictionaries.Any(d => d.Source?.OriginalString.EndsWith(target, StringComparison.Ordinal) == true))
                return;   // 已是目标主题，幂等返回
            var uri = new Uri($"pack://application:,,,/InkCanvasForPDF;component/Board/Resources/Styles/{target}");
            var old = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.EndsWith(other, StringComparison.Ordinal) == true);
            if (old != null)
                dictionaries.Remove(old);
            dictionaries.Add(new ResourceDictionary { Source = uri });
        }
    }
}
