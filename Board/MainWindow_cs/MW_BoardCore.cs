#nullable disable warnings

using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InkCanvasForPDF.Controls;
using iNKORE.UI.WPF.Modern.Controls;
using iNKORE.UI.WPF.Modern;
using Newtonsoft.Json;

namespace InkCanvasForPDF.Board
{
    /// <summary>BoardView 共享字段与基础处理器（移植自 ICA MainWindow.xaml.cs / MW_FloatBarIcons.cs，
    /// 浮动栏 / PPT 引用按画板独立运行适配）。</summary>
    public partial class BoardView
    {
        #region 共享字段（ICA 定义位置）

        public static Settings Settings = new Settings();          // ICA MainWindow.xaml.cs:167
        public static string settingsFileName = "Settings.json";   // ICA MainWindow.xaml.cs:168
        public bool isLoaded = false;                              // ICA MainWindow.xaml.cs:169
        public int currentMode = 0;                                // ICA MW_FloatBarIcons.cs:1029
        public bool forceEraser = false;                           // ICA MW_FloatBarIcons.cs:971
        public bool isDisplayingOrHidingBlackboard = false;        // ICA MW_FloatBarIcons.cs:312
        public bool isStopInkReplay = true;                        // ICA MW_FloatBarIcons.cs
        /// <summary>接触面积阈值：手掌擦除关闭或笔尖模式关闭时用手指档，笔尖模式开启时用笔尖档
        /// （值来自共享 AppSettings：nibModeBoundsWidth=10 / fingerModeBoundsWidth=30，与 PDF 批注侧一致）。</summary>
        public int BoundsWidth
        {
            get
            {
                var s = Vm?.Settings;
                if (s == null || !s.IsPalmEraseEnabled) return s?.FingerModeBoundsWidth ?? 30;
                return s.IsNibModeEnabled ? s.NibModeBoundsWidth : s.FingerModeBoundsWidth;
            }
        }
        public bool isHidingSubPanelsWhenInking = false;           // ICA MW_Timer.cs:17
        private int _drawingShapeMode = 0;
        /// <summary>当前形状绘制模式（0=非形状）。setter 统一驱动图形面板选中态高亮
        /// （照抄 PDF 由 VM ShapeDrawMode PropertyChanged 驱动的做法，11 处置 0 点零改动自动复位）。
        /// 注意：字段初始化早于 InitializeComponent，setter 内有空引用守卫。</summary>
        public int drawingShapeMode
        {
            get => _drawingShapeMode;
            set
            {
                _drawingShapeMode = value;
                UpdateBoardShapeItemHighlight(value);
            }
        }
        public bool isLongPressSelected = false;                   // ICA MW_ShapeDrawing.cs:37
        private DrawingAttributes drawingAttributes;               // ICA MainWindow.xaml.cs:101

        /// <summary>画板工具条底色。必须用控件级查找：Light-Board.xaml 只合并进 BoardView 的
        /// UserControl.Resources，Application 级没有该字典，用 Application.Current.FindResource 必抛
        /// ResourceReferenceKeyNotFoundException（历史踩坑）。兜底白色与 Light-Board 定义视觉等价。</summary>
        private Brush BoardBarBackgroundBrush =>
            TryFindResource("BoardBarBackground") as Brush ?? Brushes.White;

        #endregion

        #region 初始化（ICA ctor / loadPenCanvas / Window_Loaded 的画板部分）

        private void InitBoardCore()
        {
            timeMachine.OnRedoStateChanged += TimeMachine_OnRedoStateChanged;
            timeMachine.OnUndoStateChanged += TimeMachine_OnUndoStateChanged;
            inkCanvas.Strokes.StrokesChanged += StrokesOnStrokesChanged;

            // 形状面板固定状态不持久化：默认不置顶（Pin 切换仅本次会话有效，glyph 由 XAML 默认 E77A）

            // 时间笔锋：生长叠加层挂 Main_Grid（插在 inkCanvas 之后 = 干墨之上、工具条之下），
            // 不放 inkCanvas.Children——"点击清屏"会 inkCanvas.Children.Clear()（MW_BoardCompat BtnClear_Click）
            Main_Grid.Children.Insert(Math.Max(0, Main_Grid.Children.IndexOf(inkCanvas) + 1), inkCanvas.Controller.Overlay);
            // 时间笔锋自绘笔画提交（多触摸模式：触摸设备由叠加层绘制、由控制器自建 Stroke 入集合）
            inkCanvas.Controller.StrokeReady += (stroke, deviceId) =>
            {
                inkCanvas.Strokes.Add(stroke);
                inkCanvas_StrokeCollected(inkCanvas, new InkCanvasStrokeCollectedEventArgs(stroke));
            };
            RefreshTimePressureArmed();
        }

        /// <summary>把「时间笔锋是否可用」（样式=时间笔锋 + 画笔档位 + 非荧光笔 + 书写模式）与压感开关/长满时间
        /// 同步给控制器。多触摸模式下触碰设备由叠加层按设备自绘（时间笔锋同样生效），数位笔仍走原生命中管线。</summary>
        public void RefreshTimePressureArmed()
        {
            var c = inkCanvas.Controller;
            c.DisplayScale = 1.0;   // 画板墨迹面无整层缩放变换
            c.PressureEnabled = _pressureEnabled;
            c.GrowMs = TimePenGrowMs;
            bool inkMode = isInMultiTouchMode || inkCanvas.EditingMode == InkCanvasEditingMode.Ink;
            bool armed = Settings.Canvas.InkStyle == 3
                         && inkColor <= 100
                         && inkMode;
            c.SetCollectArmed(armed);
            // 屏蔽模式（0/1 笔锋 + 硬笔）：屏蔽默认湿墨、叠加层等宽自绘（与时间笔锋互斥，依 InkStyle 分流）
            bool shield = Settings.Canvas.InkStyle is 0 or 1
                          && !_pressureEnabled
                          && inkColor <= 100
                          && inkMode;
            c.SetShieldArmed(shield);
            // 多触摸模式：全部设备交叠加层绘制（触摸不再走 StrokeVisual 湿墨）；数位笔抬笔仍由原生提交
            c.SelfDrawAllDevices = isInMultiTouchMode;
            c.MultiDeviceEnabled = false;
            // 关闭笔锋（InkStyle=2）+ 硬笔：等宽交 WPF 渲染层开关 IgnorePressure（不再抬笔归一）；
            // 荧光笔豁免（不参与笔锋/归一路径），软笔恒 false（真压感照旧）。
            inkCanvas.DefaultDrawingAttributes.IgnorePressure =
                Settings.Canvas.InkStyle == 2 && !_pressureEnabled && inkColor <= 100;
        }

        private void loadPenCanvas()
        {
            try
            {
                drawingAttributes = inkCanvas.DefaultDrawingAttributes;
                drawingAttributes.Color = Colors.Red;

                drawingAttributes.Height = 2.5;
                drawingAttributes.Width = 2.5;

                inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            }
            catch { }
        }

        private void inkCanvas_EditingModeChanged(object sender, RoutedEventArgs e)
        {
            // 停顿强制识别提交在绘笔画时会瞬时切模式：内部切换不触发本处理器的副作用
            if (_pauseCommitSwitchingMode) return;
            // 画笔光标跟随主设置（PenCursorEnabled 由 MainWindow 注入；原读黑板本地 ICA Settings.Canvas.IsShowCursor 导致设置页开关无效）
            ApplyPenCursor();
            ApplyEraserCursor();   // 面积擦显示黑板擦光标，切出擦除时交还内置
            // 不再在进入 Ink 时翻转 forcePointEraser（原 ICA 遗留）：该字段只由 Pivot/VM 驱动，
            // 否则"切墨迹擦→切回笔→再点橡皮"会变成面积擦，与 Pivot/UI 及 PDF 侧不一致
            RefreshTimePressureArmed();   // 编辑模式切换影响时间笔锋武装状态（仅 Ink 模式启用）
        }

        private void LoadSettings(bool isFirst)
        {
            // ICA MW_SettingsToLoad 的设置加载精简移植：仅从运行目录读取 Settings.json
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, settingsFileName);
                if (File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(path));
                    if (loaded != null) Settings = loaded;
                }
            }
            catch { }
        }

        public static void SaveSettingsToFile()
        {
            string text = JsonConvert.SerializeObject(Settings, Formatting.Indented);
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, settingsFileName), text); }
            catch { }
        }

        #endregion

        #region 撤销/重做（ICA MW_FloatBarIcons.cs:887-907）

        private void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            if (inkCanvas.GetSelectedStrokes().Count != 0)
            {
                GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;
                inkCanvas.Select(new StrokeCollection());
            }
            var item = timeMachine.Undo();
            ApplyHistoryToCanvas(item);
        }

        private void BtnRedo_Click(object sender, RoutedEventArgs e)
        {
            if (inkCanvas.GetSelectedStrokes().Count != 0)
            {
                GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;
                inkCanvas.Select(new StrokeCollection());
            }
            var item = timeMachine.Redo();
            ApplyHistoryToCanvas(item);
        }

        #endregion

        #region 二级面板隐藏（ICA MW_FloatBarIcons.cs:146-239，仅保留画板元素）

        private void HideSubPanelsImmediately()
        {
            PopupFlyAnimator.FlyOut(BoardPenPalettePopup, BoardPenPalette);   // 滑出动画（150ms）后真正关闭
            PopupFlyAnimator.FlyOut(BoardEraserPalette, BoardEraserPaletteRoot);   // 橡皮菜单同理
        }

        private async void HideSubPanels(string mode = null, bool autoAlignCenter = false)
        {
            PopupFlyAnimator.FlyOut(BoardPenPalettePopup, BoardPenPalette);   // 对齐 PDF：弹层随 Popup 关闭（点色块/换工具即关，滑出动画幂等）
            PopupFlyAnimator.FlyOut(BoardEraserPalette, BoardEraserPaletteRoot);   // 橡皮菜单同为 Popup：切笔/书写即关
            PopupFlyAnimator.FlyOut(BoardGesturePopup, BoardTwoFingerGestureBorder);   // 手势面板同为 Popup：书写/切工具即关

            if (mode != null)
            {
                Brush normal = BoardBarBackgroundBrush;
                // 选中高亮：强调色 25% 混白（浅）/混黑（深），取 iNKORE 当前强调色（与 PDF 墨迹栏同源，随主题色设置生效）
                var accent = ThemeManager.Current.AccentColor
                             ?? (Color)ColorConverter.ConvertFromString("#0078D4");
                byte Mix(byte v, bool dark) => dark ? (byte)(v * 3 / 4) : (byte)((v * 3 + 255) / 4);
                Brush checkedBrush = Services.ThemeService.IsDark
                    ? new SolidColorBrush(Color.FromRgb(Mix(accent.R, true), Mix(accent.G, true), Mix(accent.B, true)))
                    : new SolidColorBrush(Color.FromRgb(Mix(accent.R, false), Mix(accent.G, false), Mix(accent.B, false)));
                if (mode != "clear")
                {
                    BoardPen.Background = normal;
                    BoardPen.Opacity = 1;
                    BoardEraser.Background = normal;
                    BoardEraser.Opacity = 1;
                    BoardSelect.Background = normal;
                    BoardSelect.Opacity = 1;
                    BoardDrawShape.Background = normal;
                    BoardDrawShape.Opacity = 1;
                }
                if (mode == "pen" || mode == "color")
                {
                    BoardPen.Background = checkedBrush;
                    BoardPen.Opacity = 0.99;
                }
                else if (mode == "eraser")
                {
                    // 面积擦/墨迹擦共用同一橡皮按钮：两种模式都走此高亮
                    BoardEraser.Background = checkedBrush;
                    BoardEraser.Opacity = 0.99;
                }
                else if (mode == "select")
                {
                    BoardSelect.Background = checkedBrush;
                    BoardSelect.Opacity = 0.99;
                }
                else if (mode == "shape")
                {
                    BoardDrawShape.Background = checkedBrush;
                    BoardDrawShape.Opacity = 0.99;
                }
            }

            await System.Threading.Tasks.Task.Delay(150);
            isHidingSubPanelsWhenInking = false;
        }

        private void ResetToolIcons() => HideSubPanels("clear");

        #endregion

        #region 杂项（ICA MW_FloatBarIcons.cs:909-1027）

        private void Element_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!isLoaded) return;
            try
            {
                if (sender is Button button)
                {
                    ((UIElement)button.Content).Opacity = button.IsEnabled ? 1 : 0.5;
                }
                else if (sender is FontIcon fontIcon)
                {
                    fontIcon.Opacity = fontIcon.IsEnabled ? 1 : 0.5;
                }
            }
            catch { }
        }

        private void BtnFingerDragMode_Click(object sender, RoutedEventArgs e)
        {
            if (isSingleFingerDragMode)
            {
                isSingleFingerDragMode = false;
            }
            else
            {
                isSingleFingerDragMode = true;
            }
        }

        private void CancelSingleFingerDragMode()
        {
            GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;

            if (isSingleFingerDragMode)
            {
                BtnFingerDragMode_Click(null, null);
            }
            isLongPressSelected = false;
        }

        // 浮动栏专用（ICA CheckEnableTwoFingerGestureBtnVisibility）：画板常显，空实现
        private void CheckEnableTwoFingerGestureBtnVisibility(bool isVisible) { }

        /// <summary>手势提示变暗（ICA MW_FloatBarIcons.cs:40-59）：多指书写开启时整组半透明；
        /// 关闭时面板恢复，手势开关按钮按设置开关决定 1 / 0.5。操作画板本地元素（Board 前缀）。</summary>
        private void CheckEnableTwoFingerGestureBtnColorPrompt()
        {
            if (ToggleSwitchEnableMultiTouchMode.IsOn)
            {
                BoardTwoFingerGestureSimpleStackPanel.Opacity = 0.5;
                BoardEnableTwoFingerGestureBtn.Opacity = 0.5;
            }
            else
            {
                BoardTwoFingerGestureSimpleStackPanel.Opacity = 1;
                BoardEnableTwoFingerGestureBtn.Opacity = Settings.Gesture.IsEnableTwoFingerGesture ? 1 : 0.5;
            }
        }

        // ═══ 墨迹画布鼠标/触摸收尾（ICA MW_ShapeDrawing.cs L1470-1633 移植：形状逻辑已并入
        //     MW_ShapeDrawing.cs，此处 MouseUp 尾部承担 4 项必需职责，尤
        //     其"面积擦提交并复位"——缺失会导致第二次面积擦抛 ArgumentException（曾经的生产崩溃）。 ═══

        // ICA MW_ShapeDrawing.cs:1470-1477
        private void inkCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            isMouseDown = true;
            if (NeedUpdateIniP())
            {
                iniP = e.GetPosition(inkCanvas);
            }
        }

        // ICA MW_ShapeDrawing.cs:1479-1485
        private void inkCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (isMouseDown)
            {
                MouseTouchMove(e.GetPosition(inkCanvas));
            }
        }

        private void inkCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            // 形状绘制抬笔定型（ICA MW_ShapeDrawing.cs:1487-1633 形状部分；isMouseDown 复位在此完成）
            ShapeDrawing_MouseUp();

            // 面积擦提交并复位（ICA L1590-1595）：累加器必须在此清空，否则下次擦除撞“重复的笔画”
            if (ReplacedStroke != null)
            {
                timeMachine.CommitStrokeEraseHistory(ReplacedStroke, AddedStroke);
                AddedStroke = null;
                ReplacedStroke = null;
            }

            // 选择/移动笔迹提交（ICA L1615-1623）
            if (StrokeManipulationHistory?.Count > 0)
            {
                timeMachine.CommitStrokeManipulationHistory(StrokeManipulationHistory);
                foreach (var item in StrokeManipulationHistory)
                {
                    StrokeInitialHistory[item.Key] = item.Value.Item2;
                }
                StrokeManipulationHistory = null;
            }

            // 改色/改粗细提交（ICA L1624-1632）
            if (DrawingAttributesHistory.Count > 0)
            {
                timeMachine.CommitStrokeDrawingAttributesHistory(DrawingAttributesHistory);
                DrawingAttributesHistory = new Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>>();
                foreach (var item in DrawingAttributesHistoryFlag)
                {
                    item.Value.Clear();
                }
            }
        }

        // ICA L1286-1293：触摸抬手 → 走鼠标抬手收尾（触摸擦除的提交入口）；全部手指抬起后复位形状等待标志
        private void Main_Grid_TouchUp(object sender, TouchEventArgs e)
        {
            inkCanvas_MouseUp(sender, null);
            if (dec.Count == 0)
            {
                isWaitUntilNextTouchDown = false;
            }
        }

        // ICA MW_ShapeDrawing.cs:471-500（触摸移动：形状分支 + 临时笔画预览）
        private void inkCanvas_TouchMove(object sender, TouchEventArgs e)
        {
            if (isSingleFingerDragMode) return;
            if (drawingShapeMode != 0)
            {
                if (isLastTouchEraser)
                {
                    return;
                }
                if (isWaitUntilNextTouchDown) return;
                if (dec.Count > 1)
                {
                    isWaitUntilNextTouchDown = true;
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStroke);
                        inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    return;
                }
                if (inkCanvas.EditingMode != InkCanvasEditingMode.None)
                {
                    inkCanvas.EditingMode = InkCanvasEditingMode.None;
                }
            }
            MouseTouchMove(e.GetTouchPoint(inkCanvas).Position);
        }

        // ═══ 画板图标按钮杂项（ICA MW_FloatBarIcons.cs / MW_Settings.cs 移植）═══

        // ICA SymbolIconDelete_MouseUp:273-291（PPT 截图分支裁剪）
        private void SymbolIconDelete_MouseUp(object sender, RoutedEventArgs e)
        {
            if (inkCanvas.GetSelectedStrokes().Count > 0)
            {
                inkCanvas.Strokes.Remove(inkCanvas.GetSelectedStrokes());
                GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;
            }
            else if (inkCanvas.Strokes.Count > 0)
            {
                if (Settings.Automation.IsAutoSaveStrokesAtClear && inkCanvas.Strokes.Count > Settings.Automation.MinimumAutomationStrokeNumber)
                {
                    SaveScreenshot(true);
                }
                BtnClear_Click(null, null);
            }
        }

        // ICA MW_Settings.cs ComboBoxTheme_SelectionChanged：画板主题下拉（亮暗色板切换）
        private void ComboBoxTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isLoaded) return;
            try
            {
                if (sender is ComboBox combo)
                {
                    Settings.Canvas.UsingWhiteboard = combo.SelectedIndex == 1;
                }
                CheckColorTheme(true);
            }
            catch { }
        }
        // NeedUpdateIniP 已随形状绘制移植移入 MW_ShapeDrawing.cs（ICA MW_ShapeDrawing.cs:1635-1642）

        #endregion

        #region 鼠标滚轮（平移/缩放，无视双指手势开关——鼠标操作无条件生效）

        /// <summary>滚轮：上下平移；Shift+滚轮：左右平移；Ctrl+滚轮：以指针为锚点缩放。
        /// 复用双指手势的笔画矩阵变换语义（直接 Transform 笔画 + 圆圈 + 笔迹粗细同步）。</summary>
        private void inkCanvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            var mods = Keyboard.Modifiers;
            var m = new Matrix();
            double scaleX = 1, scaleY = 1;
            if (mods == ModifierKeys.Control)
            {
                double factor = Math.Pow(1.1, e.Delta / 120.0);   // 与 PDF 缩放同档
                Point center = e.GetPosition(inkCanvas);           // 指针锚点
                m.ScaleAt(factor, factor, center.X, center.Y);
                scaleX = scaleY = factor;
            }
            else if (mods == ModifierKeys.Shift)
            {
                m.Translate(e.Delta, 0);   // 与横向滚动条语义一致（向下滑 = 向右看）
            }
            else
            {
                m.Translate(0, e.Delta);   // 与纵向滚动条语义一致（向上滚 = 内容下移）
            }
            ApplyMouseMatrixToStrokes(m, scaleX, scaleY);
        }

        /// <summary>把滚轮矩阵应用到笔画（照抄 Main_Grid_ManipulationDelta 的变换体）：
        /// 有选中笔画只变换选中，否则变换全部；缩放时同步 DrawingAttributes 宽高；圆圈 R/质心随变换刷新。</summary>
        private void ApplyMouseMatrixToStrokes(Matrix m, double scaleX, double scaleY)
        {
            StrokeCollection strokes = inkCanvas.GetSelectedStrokes();
            bool zooming = scaleX != 1 || scaleY != 1;
            if (strokes.Count == 0)
                strokes = inkCanvas.Strokes;

            foreach (Stroke stroke in strokes)
            {
                stroke.Transform(m, false);
                if (zooming)
                {
                    try
                    {
                        stroke.DrawingAttributes.Width *= scaleX;
                        stroke.DrawingAttributes.Height *= scaleY;
                    }
                    catch { }
                }
            }

            foreach (Circle circle in circles)
            {
                circle.R = GetDistance(circle.Stroke.StylusPoints[0].ToPoint(), circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].ToPoint()) / 2;
                circle.Centroid = new Point(
                    (circle.Stroke.StylusPoints[0].X + circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].X) / 2,
                    (circle.Stroke.StylusPoints[0].Y + circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].Y) / 2
                );
            }
        }

        #endregion
    }
}
