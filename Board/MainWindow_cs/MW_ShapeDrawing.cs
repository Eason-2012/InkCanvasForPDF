#nullable disable warnings

using InkCanvasForPDF.Board.Helpers;
using InkCanvasForPDF.Controls;
using InkCanvasForPDF.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using iNKORE.UI.WPF.Modern;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;

namespace InkCanvasForPDF.Board
{
    /// <summary>形状手绘（自 Ink-Canvas-Artistry 4.9.4 MainWindow_cs/MW_ShapeDrawing.cs 移植，
    /// 与 BoardView 其余 partial 同源授权；InkCanvasForPDF 画板面板仅含 Board* 元素，故按钮分支只判 Board 命名）。</summary>
    public partial class BoardView
    {
        #region 面板开合 / 图钉（ICA MW_ShapeDrawing.cs:20-55 / MW_FloatBarIcons.cs:841-864）

        // 形状面板固定状态（true=固定/E718：仅图形按钮或批注按钮可关；false=自动隐藏/E77A：点形状或点外部即关）。
        // 不持久化：默认不置顶（仅本次会话有效）。
        bool _shapePanelPinned = false;

        private void BoardDrawShape_Click(object sender, RoutedEventArgs e)
        {
            if (BoardShapePalette.IsOpen && !PopupFlyAnimator.IsClosing(BoardShapePalette))
            {
                CollapseBorderDrawShape();
                return;
            }
            // 滑出动画进行中再点按钮：中止滑出立即关闭后重开（避免 150ms 内点击无反应的死点）
            if (PopupFlyAnimator.IsClosing(BoardShapePalette))
                PopupFlyAnimator.AbortFlyOut(BoardShapePalette);
            // 面板跟随画板栏缩放档（Popup 不在 Viewbox 内，需自行应用；不套 PDF 的 0.65）
            BoardBorderDrawShape.LayoutTransform = new ScaleTransform(_boardBarScale, _boardBarScale);
            BoardShapePalette.PlacementTarget = BoardDrawShape;
            // 打开前预设动画初态（纯滑入：整块位于终点下方，150ms EaseOut 由快到慢滑入）：
            // Popup 首帧呈现时即为动画起点，不闪终态；Opacity 恒 1 无淡入
            PopupFlyAnimator.EnsureMeasured(BoardBorderDrawShape);   // 首开 ActualHeight=0：先同步布局取真实高度
            BoardBorderDrawShape.RenderTransform = new TranslateTransform(0, BoardBorderDrawShape.ActualHeight);
            BoardBorderDrawShape.RenderTransformOrigin = new Point(0.5, 1);
            BoardShapePalette.IsOpen = true;   // FlyIn 由 Opened 事件经 Dispatcher 启动
        }

        private void CollapseBorderDrawShape(bool isLongPressSelected = false)
        {
            PopupFlyAnimator.FlyOut(BoardShapePalette, BoardBorderDrawShape);   // 滑出动画播完才 IsOpen=false
        }

        /// <summary>面板滑入动画（纯滑入：整块从下方滑入，150ms CircleEase EaseOut 由快到慢，无淡入）。
        /// 由 BoardShapePalette.Opened 经 Dispatcher（Loaded 优先级）调用：Popup 子树就绪后动画才启动。
        /// 动画统一实现在 PopupFlyAnimator。</summary>
        private void FlyInBoardShapePalette()
            => PopupFlyAnimator.FlyIn(BoardShapePalette, BoardBorderDrawShape);

        // 面板外点击关闭（非固定状态生效；Main_Grid PreviewMouseDown 常驻挂接，Popup 内容沿逻辑树隧道到达）
        private void Main_Grid_ShapePanelOutsideDown(object sender, MouseButtonEventArgs e)
        {
            if (_shapePanelPinned || !BoardShapePalette.IsOpen)
                return;
            if (e.OriginalSource is not DependencyObject src)
                return;
            // 点在面板内或图形入口按钮上交给各自处理器（入口按钮 Click 负责 toggle）
            if (BoardBorderDrawShape.IsAncestorOf(src) || BoardDrawShape.IsAncestorOf(src))
                return;
            CollapseBorderDrawShape();
        }

        // ICA SymbolIconPinBorderDrawShape_MouseUp:41-55（语义按用户规格重定义：固定=仅图形/批注按钮可关）
        private void SymbolIconPinBorderDrawShape_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (lastBorderMouseDownObject != sender) return;

            _shapePanelPinned = !_shapePanelPinned;
            BoardBorderDrawShapePin.Glyph = _shapePanelPinned ? "\uE718" : "\uE77a";
            // 置顶状态不持久化：默认不置顶（仅本次会话有效）
        }

        #endregion

        #region 按钮选中（照抄 PDF InkToolBar_ShapeDrawing：Tag 泛化选中，取代旧 25 个 BtnDraw*_Click）

        object lastMouseDownSender;

        /// <summary>长按保持选中（泛化）：按住 500ms 未松开即选中该图形（不限第一行）。
        /// mode 取自父 Border 的 Tag；短按（500ms 内松开）不在此处理，交给 Image_MouseUp。
        /// 右键单击等同长按：立即保持选中（不进计时、不写 lastMouseDownSender，松开时 MouseUp 直接忽略）。</summary>
        private async void Image_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Image img
                || LogicalTreeHelper.GetParent(img) is not Border { Tag: not null } b
                || !int.TryParse(b.Tag.ToString(), out var mode)) return;

            if (e.ChangedButton == MouseButton.Right)
            {
                isLongPressSelected = true;
                SelectShape(mode, true);
                // 长按保持选中提示：浮动通知（与 PDF 同款，3 秒自动关闭）
                if (Window.GetWindow(this) is MainWindow tipmw)
                    tipmw.ShowFloatingTip("已保持选中该图形");
                e.Handled = true;
                return;
            }

            lastMouseDownSender = sender;

            await Task.Delay(500);
            if (lastMouseDownSender != sender) return;   // 已松开：短按路径

            lastMouseDownSender = null;
            isLongPressSelected = true;
            SelectShape(mode, true);
            // 长按保持选中提示：浮动通知（与 PDF 同款，3 秒自动关闭）
            if (Window.GetWindow(this) is MainWindow mw)
                mw.ShowFloatingTip("已保持选中该图形");
        }

        /// <summary>短按松开选中。长按路径已在 MouseDown 里清空 lastMouseDownSender，不会重复触发。</summary>
        private void Image_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (lastMouseDownSender != sender) return;
            lastMouseDownSender = null;
            if (sender is Image img
                && LogicalTreeHelper.GetParent(img) is Border { Tag: not null } b
                && int.TryParse(b.Tag.ToString(), out var mode))
                SelectShape(mode, false);
        }

        /// <summary>选中形状（画板状态机，语义对齐旧 25 个 BtnDraw*_Click）：
        /// 高亮由 drawingShapeMode 属性 setter 统一驱动；非固定收起面板 + HideSubPanels("shape") 高亮图形按钮
        /// （原 DrawShapePromptToPen 语义）。</summary>
        private async void SelectShape(int mode, bool isLongPress)
        {
            await CheckIsDrawingShapesInMultiTouchMode();
            forceEraser = true;
            drawingShapeMode = mode;
            if (mode == 24 || mode == 25)
                drawMultiStepShapeCurrentStep = 0;   // 双曲线多步：第一笔渐近线起点
            if (mode == 9)
            {
                isFirstTouchCuboid = true;           // 立方体：正面矩形两点复位
                CuboidFrontRectIniP = new Point();
                CuboidFrontRectEndP = new Point();
            }
            inkCanvas.EditingMode = InkCanvasEditingMode.None;
            inkCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();            // 退出单指拖动（内部会清 isLongPressSelected）
            isLongPressSelected = isLongPress;       // 必须在 Cancel 之后
            if (!_shapePanelPinned)
            {
                CollapseBorderDrawShape();
            }
            HideSubPanels("shape");
        }

        // ICA BtnPen_Click:104-112（画完一次还原到笔模式的入口）
        private void BtnPen_Click(object sender, RoutedEventArgs e)
        {
            forceEraser = false;
            drawingShapeMode = 0;
            inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            inkCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
            isLongPressSelected = false;
            HideSubPanels("pen", true);
        }

        // ICA CheckIsDrawingShapesInMultiTouchMode:114-122
        private Task<bool> CheckIsDrawingShapesInMultiTouchMode()
        {
            if (isInMultiTouchMode)
            {
                ToggleSwitchEnableMultiTouchMode.IsOn = false;
                lastIsInMultiTouchMode = true;
            }
            return Task.FromResult(true);
        }

        #endregion

        #region 图形项选中态（照抄 PDF InkToolBar_ShapeDrawing：强调色圆角底 + 图标反色）═══

        /// <summary>图形项 Border 查找表（Tag = 形状编号），首次访问时构建。</summary>
        private Dictionary<int, Border>? _shapeItemBorders;

        private Dictionary<int, Border> GetShapeItemBorders()
        {
            if (_shapeItemBorders != null) return _shapeItemBorders;
            _shapeItemBorders = new Dictionary<int, Border>();
            CollectShapeItemBorders(BoardBorderDrawShape);
            return _shapeItemBorders;

            void CollectShapeItemBorders(DependencyObject parent)
            {
                int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
                for (int i = 0; i < count; i++)
                {
                    if (System.Windows.Media.VisualTreeHelper.GetChild(parent, i) is not { } child) continue;
                    if (child is Border border && border.Tag != null
                        && int.TryParse(border.Tag.ToString(), out var mode)
                        && !_shapeItemBorders!.ContainsKey(mode))
                        _shapeItemBorders[mode] = border;
                    CollectShapeItemBorders(child);
                }
            }
        }

        /// <summary>刷新图形面板选中态：选中项画强调色圆角方块（透明度跟随画板栏不透明度设置），图标反色
        /// （浅色主题显白、深色主题显黑），其余图标为当前主题色。mode=0 全部复位为主题色。
        /// 由 drawingShapeMode 属性 setter 与主题切换统一驱动。</summary>
        private void UpdateBoardShapeItemHighlight(int mode)
        {
            if (BoardBorderDrawShape is null) return;   // 属性初始化早于 InitializeComponent
            var borders = GetShapeItemBorders();
            foreach (var b in borders.Values)
                b.Background = Brushes.Transparent;

            // 图标颜色直接控制：共享 DrawingImage 内部 DynamicResource 按"定义处上下文"解析，
            // Border.Resources 本地覆盖不会命中——故改为每图标克隆独占实例直接改画刷（与 PDF 同款）。
            EnsureShapeIconClones();
            var themeColor = ((SolidColorBrush)FindResource("FloatBarForeground")).Color;
            foreach (var (m, drawing) in _shapeIconDrawings!)
            {
                var color = m == mode
                    ? (Services.ThemeService.IsDark ? Colors.Black : Colors.White)
                    : themeColor;
                ApplyDrawingColor(drawing.Drawing, color);
            }

            if (mode == 0 || !borders.TryGetValue(mode, out var hit)) return;
            // 强调色取 iNKORE 当前强调色（与画板按钮条高亮同源，随主题色设置生效）
            var accent = ThemeManager.Current.AccentColor
                ?? (Color)ColorConverter.ConvertFromString("#005FB0");
            var brush = new SolidColorBrush(accent);
            brush.Opacity = Math.Clamp(_boardBarOpacity, 0, 1);
            hit.Background = brush;
        }

        /// <summary>每图标独占的 DrawingImage 克隆（mode→drawing），首次高亮时懒初始化；
        /// CloneCurrentValue 深拷贝且 DynamicResource 已解析为普通画刷，颜色完全由代码控制。</summary>
        private Dictionary<int, DrawingImage>? _shapeIconDrawings;

        private void EnsureShapeIconClones()
        {
            if (_shapeIconDrawings != null) return;
            _shapeIconDrawings = new Dictionary<int, DrawingImage>();
            foreach (var (mode, border) in GetShapeItemBorders())
                if (border.Child is Image img && img.Source is DrawingImage src
                    && !_shapeIconDrawings.ContainsKey(mode))
                {
                    var clone = (DrawingImage)src.CloneCurrentValue();
                    img.Source = clone;   // 仅本面板改用克隆；原字典实例不动，PDF 侧引用不受影响
                    _shapeIconDrawings[mode] = clone;
                }
        }

        /// <summary>递归改写 Drawing 树内所有 SolidColorBrush 为目标色（x:Null 画刷保持空）。</summary>
        private static void ApplyDrawingColor(Drawing? d, Color color)
        {
            switch (d)
            {
                case DrawingGroup g:
                    foreach (var child in g.Children)
                        ApplyDrawingColor(child, color);
                    break;
                case GeometryDrawing gd:
                    if (gd.Brush is SolidColorBrush) gd.Brush = new SolidColorBrush(color);
                    if (gd.Pen?.Brush is SolidColorBrush) gd.Pen.Brush = new SolidColorBrush(color);
                    break;
            }
        }

        /// <summary>图形面板提示文字颜色：浅色主题 #666666 / 深色主题 #AAAAAA（Loaded 与主题切换时刷新）。</summary>
        private void ApplyShapeHintColor()
            => BoardShapeHintTextBlock.Foreground = new SolidColorBrush(
                Services.ThemeService.IsDark
                    ? Color.FromRgb(0xAA, 0xAA, 0xAA)
                    : Color.FromRgb(0x66, 0x66, 0x66));

        #endregion

        #region 触摸/鼠标事件（ICA MW_ShapeDrawing.cs:471-500, 1286-1296, 1470-1485）

        // ICA MW_TouchEvents.cs:208：上一次触摸是否进入橡皮（形状触摸移动时避免误画）
        bool isLastTouchEraser = false;

        Stroke lastTempStroke = null;
        StrokeCollection lastTempStrokeCollection = new StrokeCollection();
        bool isWaitUntilNextTouchDown = false;

        bool isMouseDown = false;
        bool isShapeFinishing = false;   // 抬笔完成流程进行中（含同步弹窗泵）：屏蔽绘制帧，防泵内 MouseMove 画幽灵帧删曲线/劫持 special

        // ICA MW_ShapeDrawing.cs:1487-1633 的形状部分（抬笔定型）；由 MW_BoardCore.inkCanvas_MouseUp 头部调用，
        // 其尾部（面积擦/移动/改色提交）保留在 MW_BoardCore 原位
        private void ShapeDrawing_MouseUp()
        {
            isShapeFinishing = true;
            isMouseDown = false;   // 提前复位（原在方法尾）：同步弹窗泵期间任何 MouseMove 立即失效
            try
            {
                if (drawingShapeMode == 5)
                {
                    if (lastTempStroke != null)
                    {
                        Circle circle = new Circle(new Point(), 0, lastTempStroke);
                        circle.R = GetDistance(circle.Stroke.StylusPoints[0].ToPoint(), circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].ToPoint()) / 2;
                        circle.Centroid = new Point((circle.Stroke.StylusPoints[0].X + circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].X) / 2,
                                                    (circle.Stroke.StylusPoints[0].Y + circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].Y) / 2);
                        circles.Add(circle);
                    }
                    if (lastIsInMultiTouchMode)
                    {
                        ToggleSwitchEnableMultiTouchMode.IsOn = true;
                        lastIsInMultiTouchMode = false;
                    }
                }
                if (drawingShapeMode != 9 && drawingShapeMode != 0 && drawingShapeMode != 24 && drawingShapeMode != 25)
                {
                    if (isLongPressSelected)
                    {

                    }
                    else
                    {
                        BtnPen_Click(null, null); //画完一次还原到笔模式

                        if (lastIsInMultiTouchMode)
                        {
                            ToggleSwitchEnableMultiTouchMode.IsOn = true;
                            lastIsInMultiTouchMode = false;
                        }
                    }
                }
                if (drawingShapeMode == 9)
                {
                    if (isFirstTouchCuboid)
                    {
                        if (CuboidStrokeCollection == null) CuboidStrokeCollection = new StrokeCollection();
                        isFirstTouchCuboid = false;
                        Point newIniP = new Point(Math.Min(CuboidFrontRectIniP.X, CuboidFrontRectEndP.X), Math.Min(CuboidFrontRectIniP.Y, CuboidFrontRectEndP.Y));
                        Point newEndP = new Point(Math.Max(CuboidFrontRectIniP.X, CuboidFrontRectEndP.X), Math.Max(CuboidFrontRectIniP.Y, CuboidFrontRectEndP.Y));
                        CuboidFrontRectIniP = newIniP;
                        CuboidFrontRectEndP = newEndP;
                        CuboidStrokeCollection.Add(lastTempStrokeCollection);
                    }
                    else
                    {
                        BtnPen_Click(null, null); //画完还原到笔模式

                        if (lastIsInMultiTouchMode)
                        {
                            ToggleSwitchEnableMultiTouchMode.IsOn = true;
                            lastIsInMultiTouchMode = false;
                        }

                        if (_currentCommitType == CommitReason.ShapeDrawing)
                        {
                            CuboidStrokeCollection.Add(lastTempStrokeCollection);
                            _currentCommitType = CommitReason.UserInput;
                            timeMachine.CommitStrokeUserInputHistory(CuboidStrokeCollection);
                            CuboidStrokeCollection = null;
                        }
                    }
                }
                if (drawingShapeMode == 24 || drawingShapeMode == 25)
                {
                    if (drawMultiStepShapeCurrentStep == 0)
                    {
                        drawMultiStepShapeCurrentStep = 1;
                    }
                    else
                    {
                        drawMultiStepShapeCurrentStep = 0;
                        var curve = lastTempStrokeCollection;   // 第二笔曲线集合快照（入口处仍在）
                        ConsoleLog.Write("双曲线绘制", $"完成(画板): 曲线笔画={curve?.Count ?? 0}, 渐近线笔画={drawMultiStepShapeSpecialStrokeCollection?.Count ?? -1}, 画布笔画={inkCanvas.Strokes.Count}");
                        if (drawMultiStepShapeSpecialStrokeCollection != null)
                        {
                            // 通用设置（AppSettings.hyperbolaAsymptoteOption，与 PDF 侧同一份）：0=保留 / 1=移除 / 2=询问（同步弹窗泵）
                            bool keep = true;
                            if (Vm is { } vm)
                            {
                                switch (vm.Settings.HyperbolaAsymptoteOption)
                                {
                                    case 1:
                                        keep = false;
                                        break;
                                    case 2:
                                        if (Window.GetWindow(this) is MainWindow mw)   // 与本文件 105/120 行同款取窗方式
                                            keep = mw.AskKeepAsymptote();
                                        break;
                                }
                            }
                            if (!keep)
                            {
                                inkCanvas.Strokes.Remove(drawMultiStepShapeSpecialStrokeCollection);
                            }
                            // 自愈：保留/移除决定后曲线若已不在画布（根因未明的移除路径），提交历史前补回
                            bool healed = false;
                            if (curve is { Count: > 0 } && !inkCanvas.Strokes.Contains(curve[0]))
                            {
                                inkCanvas.Strokes.Add(curve);
                                healed = true;
                            }
                            ConsoleLog.Write("双曲线绘制", $"结果(画板): 渐近线={(keep ? "保留" : "移除")}, 自愈={healed}, 画布笔画={inkCanvas.Strokes.Count}");
                        }
                        BtnPen_Click(null, null); //画完还原到笔模式

                        if (lastIsInMultiTouchMode)
                        {
                            ToggleSwitchEnableMultiTouchMode.IsOn = true;
                            lastIsInMultiTouchMode = false;
                        }
                    }
                }
                if (_currentCommitType == CommitReason.ShapeDrawing && drawingShapeMode != 9)
                {
                    _currentCommitType = CommitReason.UserInput;
                    StrokeCollection? collection = null;
                    if (lastTempStrokeCollection != null && lastTempStrokeCollection.Count > 0)
                    {
                        collection = lastTempStrokeCollection;
                    }
                    else if (lastTempStroke != null)
                    {
                        collection = new StrokeCollection() { lastTempStroke };
                    }
                    if (collection != null)
                    {
                        timeMachine.CommitStrokeUserInputHistory(collection);
                    }
                }
                lastTempStroke = null;
                lastTempStrokeCollection = null;
            }
            finally
            {
                isShapeFinishing = false;
            }
        }

        // ICA MW_ShapeDrawing.cs:1635-1642（画板版覆盖 MW_BoardCore 的恒 true 裁剪版）
        private bool NeedUpdateIniP()
        {
            if (drawingShapeMode == 24 || drawingShapeMode == 25)
            {
                if (drawMultiStepShapeCurrentStep == 1) return false;
            }
            return true;
        }

        #endregion

        #region 形状绘制主函数（ICA MW_ShapeDrawing.cs:502-1280 原样移植）

        int drawMultiStepShapeCurrentStep = 0; //多笔完成的图形 当前所处在的笔画
        StrokeCollection drawMultiStepShapeSpecialStrokeCollection = new StrokeCollection(); //多笔完成的图形 当前所处在的笔画
        double drawMultiStepShapeSpecialParameter3 = 0.0; //多笔完成的图形 特殊参数 通常用于表示k

        bool isFirstTouchCuboid = true;
        Point CuboidFrontRectIniP = new Point();
        Point CuboidFrontRectEndP = new Point();
        StrokeCollection CuboidStrokeCollection = null;
        bool lastIsInMultiTouchMode = false;

        /// <summary>形状笔画专用属性：Clone 自画布默认属性但强制关闭墨迹平滑。
        /// FitToCurve 会把矩形/中心矩形等少量点的闭合几何拟合成圆角曲线，导致"矩形变形"。</summary>
        private DrawingAttributes ShapeStrokeAttributes
        {
            get
            {
                var da = inkCanvas.DefaultDrawingAttributes.Clone();
                da.FitToCurve = false;
                return da;
            }
        }

        private void MouseTouchMove(Point endP)
        {
            if (isShapeFinishing) return;   // 完成流程（含同步弹窗泵）期间屏蔽一切绘制帧
            List<System.Windows.Point> pointList;
            StylusPointCollection point;
            Stroke stroke;
            StrokeCollection strokes = new StrokeCollection();
            Point newIniP = iniP;
            switch (drawingShapeMode)
            {
                case 1:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point(iniP.X, iniP.Y),
                        new System.Windows.Point(endP.X, endP.Y)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStroke);
                    }
                    catch { }
                    lastTempStroke = stroke;
                    inkCanvas.Strokes.Add(stroke);
                    break;
                case 8:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    strokes.Add(GenerateDashedLineStrokeCollection(iniP, endP));
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    inkCanvas.Strokes.Add(strokes);
                    break;
                case 18:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    strokes.Add(GenerateDotLineStrokeCollection(iniP, endP));
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    inkCanvas.Strokes.Add(strokes);
                    break;
                case 2:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    double w = 30, h = 10;
                    double theta = Math.Atan2(iniP.Y - endP.Y, iniP.X - endP.X);
                    double sint = Math.Sin(theta);
                    double cost = Math.Cos(theta);

                    pointList = new List<Point>
                    {
                        new Point(iniP.X, iniP.Y),
                        new Point(endP.X , endP.Y),
                        new Point(endP.X + (w * cost - h * sint), endP.Y + (w * sint + h * cost)),
                        new Point(endP.X,endP.Y),
                        new Point(endP.X + (w * cost + h * sint), endP.Y - (h * cost - w * sint))
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStroke);
                    }
                    catch { }
                    lastTempStroke = stroke;
                    inkCanvas.Strokes.Add(stroke);

                    break;
                case 15:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    double d = GetDistance(iniP, endP);
                    if (d == 0) return;
                    double sinTheta = (iniP.Y - endP.Y) / d;
                    double cosTheta = (endP.X - iniP.X) / d;
                    double tanTheta = Math.Abs(sinTheta / cosTheta);
                    double x = 25;
                    if (Math.Abs(tanTheta) < 1.0 / 12)
                    {
                        sinTheta = 0;
                        cosTheta = 1;
                        endP.Y = iniP.Y;
                    }
                    if (tanTheta < 0.63 && tanTheta > 0.52) //30
                    {
                        sinTheta = sinTheta / Math.Abs(sinTheta) * 0.5;
                        cosTheta = cosTheta / Math.Abs(cosTheta) * 0.866;
                        endP.Y = iniP.Y - d * sinTheta;
                        endP.X = iniP.X + d * cosTheta;
                    }
                    if (tanTheta < 1.08 && tanTheta > 0.92) //45
                    {
                        sinTheta = sinTheta / Math.Abs(sinTheta) * 0.707;
                        cosTheta = cosTheta / Math.Abs(cosTheta) * 0.707;
                        endP.Y = iniP.Y - d * sinTheta;
                        endP.X = iniP.X + d * cosTheta;
                    }
                    if (tanTheta < 1.95 && tanTheta > 1.63) //60
                    {
                        sinTheta = sinTheta / Math.Abs(sinTheta) * 0.866;
                        cosTheta = cosTheta / Math.Abs(cosTheta) * 0.5;
                        endP.Y = iniP.Y - d * sinTheta;
                        endP.X = iniP.X + d * cosTheta;
                    }
                    if (Math.Abs(cosTheta / sinTheta) < 1.0 / 12)
                    {
                        endP.X = iniP.X;
                        sinTheta = 1;
                        cosTheta = 0;
                    }
                    strokes.Add(GenerateLineStroke(new Point(iniP.X - 3 * x * sinTheta, iniP.Y - 3 * x * cosTheta), new Point(endP.X - 3 * x * sinTheta, endP.Y - 3 * x * cosTheta)));
                    strokes.Add(GenerateLineStroke(new Point(iniP.X - x * sinTheta, iniP.Y - x * cosTheta), new Point(endP.X - x * sinTheta, endP.Y - x * cosTheta)));
                    strokes.Add(GenerateLineStroke(new Point(iniP.X + x * sinTheta, iniP.Y + x * cosTheta), new Point(endP.X + x * sinTheta, endP.Y + x * cosTheta)));
                    strokes.Add(GenerateLineStroke(new Point(iniP.X + 3 * x * sinTheta, iniP.Y + 3 * x * cosTheta), new Point(endP.X + 3 * x * sinTheta, endP.Y + 3 * x * cosTheta)));
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    inkCanvas.Strokes.Add(strokes);

                    break;
                case 11:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    strokes.Add(GenerateArrowLineStroke(new Point(2 * iniP.X - (endP.X - 20), iniP.Y), new Point(endP.X, iniP.Y)));
                    strokes.Add(GenerateArrowLineStroke(new Point(iniP.X, 2 * iniP.Y - (endP.Y + 20)), new Point(iniP.X, endP.Y)));
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    inkCanvas.Strokes.Add(strokes);

                    break;
                case 12:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    if (Math.Abs(iniP.X - endP.X) < 0.01) return;
                    strokes.Add(GenerateArrowLineStroke(new Point(iniP.X + (iniP.X - endP.X) / Math.Abs(iniP.X - endP.X) * 25, iniP.Y), new Point(endP.X, iniP.Y)));
                    strokes.Add(GenerateArrowLineStroke(new Point(iniP.X, 2 * iniP.Y - (endP.Y + 20)), new Point(iniP.X, endP.Y)));
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    inkCanvas.Strokes.Add(strokes);

                    break;
                case 13:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    if (Math.Abs(iniP.Y - endP.Y) < 0.01) return;
                    strokes.Add(GenerateArrowLineStroke(new Point(2 * iniP.X - (endP.X - 20), iniP.Y), new Point(endP.X, iniP.Y)));
                    strokes.Add(GenerateArrowLineStroke(new Point(iniP.X, iniP.Y + (iniP.Y - endP.Y) / Math.Abs(iniP.Y - endP.Y) * 25), new Point(iniP.X, endP.Y)));
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    inkCanvas.Strokes.Add(strokes);

                    break;
                case 14:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    if (Math.Abs(iniP.X - endP.X) < 0.01 || Math.Abs(iniP.Y - endP.Y) < 0.01) return;
                    strokes.Add(GenerateArrowLineStroke(new Point(iniP.X + (iniP.X - endP.X) / Math.Abs(iniP.X - endP.X) * 25, iniP.Y), new Point(endP.X, iniP.Y)));
                    strokes.Add(GenerateArrowLineStroke(new Point(iniP.X, iniP.Y + (iniP.Y - endP.Y) / Math.Abs(iniP.Y - endP.Y) * 25), new Point(iniP.X, endP.Y)));
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    inkCanvas.Strokes.Add(strokes);

                    break;
                case 17:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    strokes.Add(GenerateArrowLineStroke(new Point(iniP.X, iniP.Y), new Point(iniP.X + Math.Abs(endP.X - iniP.X), iniP.Y)));
                    strokes.Add(GenerateArrowLineStroke(new Point(iniP.X, iniP.Y), new Point(iniP.X, iniP.Y - Math.Abs(endP.Y - iniP.Y))));
                    d = (Math.Abs(iniP.X - endP.X) + Math.Abs(iniP.Y - endP.Y)) / 2;
                    strokes.Add(GenerateArrowLineStroke(new Point(iniP.X, iniP.Y), new Point(iniP.X - d / 1.76, iniP.Y + d / 1.76)));
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    inkCanvas.Strokes.Add(strokes);

                    break;
                case 3:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point(iniP.X, iniP.Y),
                        new System.Windows.Point(iniP.X, endP.Y),
                        new System.Windows.Point(endP.X, endP.Y),
                        new System.Windows.Point(endP.X, iniP.Y),
                        new System.Windows.Point(iniP.X, iniP.Y)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStroke);
                    }
                    catch { }
                    lastTempStroke = stroke;
                    inkCanvas.Strokes.Add(stroke);

                    break;
                case 19:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    double a = iniP.X - endP.X;
                    double b = iniP.Y - endP.Y;
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point(iniP.X - a, iniP.Y - b),
                        new System.Windows.Point(iniP.X - a, iniP.Y + b),
                        new System.Windows.Point(iniP.X + a, iniP.Y + b),
                        new System.Windows.Point(iniP.X + a, iniP.Y - b),
                        new System.Windows.Point(iniP.X - a, iniP.Y - b)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStroke);
                    }
                    catch { }
                    lastTempStroke = stroke;
                    inkCanvas.Strokes.Add(stroke);

                    break;
                case 4:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    pointList = GenerateEllipseGeometry(iniP, endP);
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStroke);
                    }
                    catch { }
                    lastTempStroke = stroke;
                    inkCanvas.Strokes.Add(stroke);

                    break;
                case 5:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    double R = GetDistance(iniP, endP);
                    pointList = GenerateEllipseGeometry(new Point(iniP.X - R, iniP.Y - R), new Point(iniP.X + R, iniP.Y + R));
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStroke);
                    }
                    catch { }
                    lastTempStroke = stroke;
                    inkCanvas.Strokes.Add(stroke);

                    break;
                case 16:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    double halfA = endP.X - iniP.X;
                    double halfB = endP.Y - iniP.Y;
                    pointList = GenerateEllipseGeometry(new Point(iniP.X - halfA, iniP.Y - halfB), new Point(iniP.X + halfA, iniP.Y + halfB));
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStroke);
                    }
                    catch { }
                    lastTempStroke = stroke;
                    inkCanvas.Strokes.Add(stroke);

                    break;
                case 23:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    a = Math.Abs(endP.X - iniP.X);
                    b = Math.Abs(endP.Y - iniP.Y);
                    pointList = GenerateEllipseGeometry(new Point(iniP.X - a, iniP.Y - b), new Point(iniP.X + a, iniP.Y + b));
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    strokes.Add(stroke);
                    double c = Math.Sqrt(Math.Abs(a * a - b * b));
                    StylusPoint stylusPoint;
                    if (a > b)
                    {
                        stylusPoint = new StylusPoint(iniP.X + c, iniP.Y, (float)1.0);
                        point = new StylusPointCollection();
                        point.Add(stylusPoint);
                        stroke = new Stroke(point)
                        {
                            DrawingAttributes = ShapeStrokeAttributes
                        };
                        strokes.Add(stroke.Clone());
                        stylusPoint = new StylusPoint(iniP.X - c, iniP.Y, (float)1.0);
                        point = new StylusPointCollection();
                        point.Add(stylusPoint);
                        stroke = new Stroke(point)
                        {
                            DrawingAttributes = ShapeStrokeAttributes
                        };
                        strokes.Add(stroke.Clone());
                    }
                    else if (a < b)
                    {
                        stylusPoint = new StylusPoint(iniP.X, iniP.Y - c, (float)1.0);
                        point = new StylusPointCollection();
                        point.Add(stylusPoint);
                        stroke = new Stroke(point)
                        {
                            DrawingAttributes = ShapeStrokeAttributes
                        };
                        strokes.Add(stroke.Clone());
                        stylusPoint = new StylusPoint(iniP.X, iniP.Y + c, (float)1.0);
                        point = new StylusPointCollection();
                        point.Add(stylusPoint);
                        stroke = new Stroke(point)
                        {
                            DrawingAttributes = ShapeStrokeAttributes
                        };
                        strokes.Add(stroke.Clone());
                    }
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    inkCanvas.Strokes.Add(strokes);

                    break;
                case 10:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    R = GetDistance(iniP, endP);
                    strokes = GenerateDashedLineEllipseStrokeCollection(new Point(iniP.X - R, iniP.Y - R), new Point(iniP.X + R, iniP.Y + R));
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    inkCanvas.Strokes.Add(strokes);

                    break;
                case 24:
                case 25:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    //双曲线 x^2/a^2 - y^2/b^2 = 1
                    if (Math.Abs(iniP.X - endP.X) < 0.01 || Math.Abs(iniP.Y - endP.Y) < 0.01) return;
                    var pointList2 = new List<Point>();
                    var pointList3 = new List<Point>();
                    var pointList4 = new List<Point>();
                    if (drawMultiStepShapeCurrentStep == 0)
                    {
                        //第一笔：画渐近线
                        double k = Math.Abs((endP.Y - iniP.Y) / (endP.X - iniP.X));
                        strokes.Add(GenerateDashedLineStrokeCollection(new Point(2 * iniP.X - endP.X, 2 * iniP.Y - endP.Y), endP));
                        strokes.Add(GenerateDashedLineStrokeCollection(new Point(2 * iniP.X - endP.X, endP.Y), new Point(endP.X, 2 * iniP.Y - endP.Y)));
                        drawMultiStepShapeSpecialParameter3 = k;
                        drawMultiStepShapeSpecialStrokeCollection = strokes;
                    }
                    else
                    {
                        //第二笔：画双曲线
                        double k = drawMultiStepShapeSpecialParameter3;
                        bool isHyperbolaFocalPointOnXAxis = Math.Abs((endP.Y - iniP.Y) / (endP.X - iniP.X)) < k;
                        if (isHyperbolaFocalPointOnXAxis)
                        { // 焦点在 x 轴上
                            a = Math.Sqrt(Math.Abs((endP.X - iniP.X) * (endP.X - iniP.X) - (endP.Y - iniP.Y) * (endP.Y - iniP.Y) / (k * k)));
                            b = a * k;
                            pointList = new List<Point>();
                            for (double i = a; i <= Math.Abs(endP.X - iniP.X); i += 0.5)
                            {
                                double rY = Math.Sqrt(Math.Abs(k * k * i * i - b * b));
                                pointList.Add(new Point(iniP.X + i, iniP.Y - rY));
                                pointList2.Add(new Point(iniP.X + i, iniP.Y + rY));
                                pointList3.Add(new Point(iniP.X - i, iniP.Y - rY));
                                pointList4.Add(new Point(iniP.X - i, iniP.Y + rY));
                            }
                        }
                        else
                        { // 焦点在 y 轴上
                            a = Math.Sqrt(Math.Abs((endP.Y - iniP.Y) * (endP.Y - iniP.Y) - (endP.X - iniP.X) * (endP.X - iniP.X) * (k * k)));
                            b = a / k;
                            pointList = new List<Point>();
                            for (double i = a; i <= Math.Abs(endP.Y - iniP.Y); i += 0.5)
                            {
                                double rX = Math.Sqrt(Math.Abs(i * i / k / k - b * b));
                                pointList.Add(new Point(iniP.X - rX, iniP.Y + i));
                                pointList2.Add(new Point(iniP.X + rX, iniP.Y + i));
                                pointList3.Add(new Point(iniP.X - rX, iniP.Y - i));
                                pointList4.Add(new Point(iniP.X + rX, iniP.Y - i));
                            }
                        }
                        try
                        {
                            point = new StylusPointCollection(pointList);
                            stroke = new Stroke(point) { DrawingAttributes = ShapeStrokeAttributes };
                            strokes.Add(stroke.Clone());
                            point = new StylusPointCollection(pointList2);
                            stroke = new Stroke(point) { DrawingAttributes = ShapeStrokeAttributes };
                            strokes.Add(stroke.Clone());
                            point = new StylusPointCollection(pointList3);
                            stroke = new Stroke(point) { DrawingAttributes = ShapeStrokeAttributes };
                            strokes.Add(stroke.Clone());
                            point = new StylusPointCollection(pointList4);
                            stroke = new Stroke(point) { DrawingAttributes = ShapeStrokeAttributes };
                            strokes.Add(stroke.Clone());
                            if (drawingShapeMode == 25)
                            {
                                //画焦点
                                c = Math.Sqrt(a * a + b * b);
                                stylusPoint = isHyperbolaFocalPointOnXAxis ? new StylusPoint(iniP.X + c, iniP.Y, (float)1.0) : new StylusPoint(iniP.X, iniP.Y + c, (float)1.0);
                                point = new StylusPointCollection();
                                point.Add(stylusPoint);
                                stroke = new Stroke(point) { DrawingAttributes = ShapeStrokeAttributes };
                                strokes.Add(stroke.Clone());
                                stylusPoint = isHyperbolaFocalPointOnXAxis ? new StylusPoint(iniP.X - c, iniP.Y, (float)1.0) : new StylusPoint(iniP.X, iniP.Y - c, (float)1.0);
                                point = new StylusPointCollection();
                                point.Add(stylusPoint);
                                stroke = new Stroke(point) { DrawingAttributes = ShapeStrokeAttributes };
                                strokes.Add(stroke.Clone());
                            }
                        }
                        catch (Exception ex)
                        {
                            App.Log("Hyperbola.Curve", ex);   // 生成异常只进 error.log（log 只存错误）
                            return;
                        }
                    }
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    inkCanvas.Strokes.Add(strokes);

                    break;
                case 20:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    //抛物线 y=ax^2
                    if (Math.Abs(iniP.X - endP.X) < 0.01 || Math.Abs(iniP.Y - endP.Y) < 0.01) return;
                    a = (iniP.Y - endP.Y) / ((iniP.X - endP.X) * (iniP.X - endP.X));
                    pointList = new List<Point>();
                    pointList2 = new List<Point>();
                    for (double i = 0.0; i <= Math.Abs(endP.X - iniP.X); i += 0.5)
                    {
                        pointList.Add(new Point(iniP.X + i, iniP.Y - a * i * i));
                        pointList2.Add(new Point(iniP.X - i, iniP.Y - a * i * i));
                    }
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    strokes.Add(stroke.Clone());
                    point = new StylusPointCollection(pointList2);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    strokes.Add(stroke.Clone());
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    inkCanvas.Strokes.Add(strokes);

                    break;
                case 21:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    //抛物线 y^2=ax
                    if (Math.Abs(iniP.X - endP.X) < 0.01 || Math.Abs(iniP.Y - endP.Y) < 0.01) return;
                    a = (iniP.X - endP.X) / ((iniP.Y - endP.Y) * (iniP.Y - endP.Y));
                    pointList = new List<Point>();
                    pointList2 = new List<Point>();
                    for (double i = 0.0; i <= Math.Abs(endP.Y - iniP.Y); i += 0.5)
                    {
                        pointList.Add(new Point(iniP.X - a * i * i, iniP.Y + i));
                        pointList2.Add(new Point(iniP.X - a * i * i, iniP.Y - i));
                    }
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    strokes.Add(stroke.Clone());
                    point = new StylusPointCollection(pointList2);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    strokes.Add(stroke.Clone());
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    inkCanvas.Strokes.Add(strokes);

                    break;
                case 22:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    //抛物线 y^2=ax, 含焦点
                    if (Math.Abs(iniP.X - endP.X) < 0.01 || Math.Abs(iniP.Y - endP.Y) < 0.01) return;
                    double p = (iniP.Y - endP.Y) * (iniP.Y - endP.Y) / (2 * (iniP.X - endP.X));
                    a = 0.5 / p;
                    pointList = new List<Point>();
                    pointList2 = new List<Point>();
                    for (double i = 0.0; i <= Math.Abs(endP.Y - iniP.Y); i += 0.5)
                    {
                        pointList.Add(new Point(iniP.X - a * i * i, iniP.Y + i));
                        pointList2.Add(new Point(iniP.X - a * i * i, iniP.Y - i));
                    }
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    strokes.Add(stroke.Clone());
                    point = new StylusPointCollection(pointList2);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    strokes.Add(stroke.Clone());
                    stylusPoint = new StylusPoint(iniP.X - p / 2, iniP.Y, (float)1.0);
                    point = new StylusPointCollection();
                    point.Add(stylusPoint);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    strokes.Add(stroke.Clone());
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    inkCanvas.Strokes.Add(strokes);

                    break;
                case 6:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    newIniP = iniP;
                    if (iniP.Y > endP.Y)
                    {
                        newIniP = new Point(iniP.X, endP.Y);
                        endP = new Point(endP.X, iniP.Y);
                    }
                    double topA = Math.Abs(newIniP.X - endP.X);
                    double topB = topA / 2.646;
                    //顶部椭圆
                    pointList = GenerateEllipseGeometry(new Point(newIniP.X, newIniP.Y - topB / 2), new Point(endP.X, newIniP.Y + topB / 2));
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    strokes.Add(stroke.Clone());
                    //底部椭圆
                    pointList = GenerateEllipseGeometry(new Point(newIniP.X, endP.Y - topB / 2), new Point(endP.X, endP.Y + topB / 2), false, true);
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    strokes.Add(stroke.Clone());
                    strokes.Add(GenerateDashedLineEllipseStrokeCollection(new Point(newIniP.X, endP.Y - topB / 2), new Point(endP.X, endP.Y + topB / 2), true, false));
                    //左侧
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point(newIniP.X, newIniP.Y),
                        new System.Windows.Point(newIniP.X, endP.Y)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    strokes.Add(stroke.Clone());
                    //右侧
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point(endP.X, newIniP.Y),
                        new System.Windows.Point(endP.X, endP.Y)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    strokes.Add(stroke.Clone());
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    inkCanvas.Strokes.Add(strokes);

                    break;
                case 7:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    if (iniP.Y > endP.Y)
                    {
                        newIniP = new Point(iniP.X, endP.Y);
                        endP = new Point(endP.X, iniP.Y);
                    }
                    double bottomA = Math.Abs(newIniP.X - endP.X);
                    double bottomB = bottomA / 2.646;
                    //底部椭圆
                    pointList = GenerateEllipseGeometry(new Point(newIniP.X, endP.Y - bottomB / 2), new Point(endP.X, endP.Y + bottomB / 2), false, true);
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    strokes.Add(stroke.Clone());
                    strokes.Add(GenerateDashedLineEllipseStrokeCollection(new Point(newIniP.X, endP.Y - bottomB / 2), new Point(endP.X, endP.Y + bottomB / 2), true, false));
                    //左侧
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point((newIniP.X + endP.X) / 2, newIniP.Y),
                        new System.Windows.Point(newIniP.X, endP.Y)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    strokes.Add(stroke.Clone());
                    //右侧
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point((newIniP.X + endP.X) / 2, newIniP.Y),
                        new System.Windows.Point(endP.X, endP.Y)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    strokes.Add(stroke.Clone());
                    try
                    {
                        inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    inkCanvas.Strokes.Add(strokes);

                    break;
                case 9:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    if (isFirstTouchCuboid)
                    {
                        //分开画线条方便后期单独擦除某一条棱
                        strokes.Add(GenerateLineStroke(new Point(iniP.X, iniP.Y), new Point(iniP.X, endP.Y)));
                        strokes.Add(GenerateLineStroke(new Point(iniP.X, endP.Y), new Point(endP.X, endP.Y)));
                        strokes.Add(GenerateLineStroke(new Point(endP.X, endP.Y), new Point(endP.X, iniP.Y)));
                        strokes.Add(GenerateLineStroke(new Point(iniP.X, iniP.Y), new Point(endP.X, iniP.Y)));
                        try
                        {
                            inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                        }
                        catch { }
                        lastTempStrokeCollection = strokes;
                        inkCanvas.Strokes.Add(strokes);
                        CuboidFrontRectIniP = iniP;
                        CuboidFrontRectEndP = endP;
                    }
                    else
                    {
                        d = CuboidFrontRectIniP.Y - endP.Y;
                        if (d < 0) d = -d;
                        a = CuboidFrontRectEndP.X - CuboidFrontRectIniP.X; //正面矩形长
                        b = CuboidFrontRectEndP.Y - CuboidFrontRectIniP.Y; //正面矩形宽

                        //横上
                        Point newLineIniP = new Point(CuboidFrontRectIniP.X + d, CuboidFrontRectIniP.Y - d);
                        Point newLineEndP = new Point(CuboidFrontRectEndP.X + d, CuboidFrontRectIniP.Y - d);
                        pointList = new List<System.Windows.Point> { newLineIniP, newLineEndP };
                        point = new StylusPointCollection(pointList);
                        stroke = new Stroke(point) { DrawingAttributes = ShapeStrokeAttributes };
                        strokes.Add(stroke.Clone());
                        //横下 (虚线)
                        newLineIniP = new Point(CuboidFrontRectIniP.X + d, CuboidFrontRectEndP.Y - d);
                        newLineEndP = new Point(CuboidFrontRectEndP.X + d, CuboidFrontRectEndP.Y - d);
                        strokes.Add(GenerateDashedLineStrokeCollection(newLineIniP, newLineEndP));
                        //斜左上
                        newLineIniP = new Point(CuboidFrontRectIniP.X, CuboidFrontRectIniP.Y);
                        newLineEndP = new Point(CuboidFrontRectIniP.X + d, CuboidFrontRectIniP.Y - d);
                        pointList = new List<System.Windows.Point> { newLineIniP, newLineEndP };
                        point = new StylusPointCollection(pointList);
                        stroke = new Stroke(point) { DrawingAttributes = ShapeStrokeAttributes };
                        strokes.Add(stroke.Clone());
                        //斜右上
                        newLineIniP = new Point(CuboidFrontRectEndP.X, CuboidFrontRectIniP.Y);
                        newLineEndP = new Point(CuboidFrontRectEndP.X + d, CuboidFrontRectIniP.Y - d);
                        pointList = new List<System.Windows.Point> { newLineIniP, newLineEndP };
                        point = new StylusPointCollection(pointList);
                        stroke = new Stroke(point) { DrawingAttributes = ShapeStrokeAttributes };
                        strokes.Add(stroke.Clone());
                        //斜左下 (虚线)
                        newLineIniP = new Point(CuboidFrontRectIniP.X, CuboidFrontRectEndP.Y);
                        newLineEndP = new Point(CuboidFrontRectIniP.X + d, CuboidFrontRectEndP.Y - d);
                        strokes.Add(GenerateDashedLineStrokeCollection(newLineIniP, newLineEndP));
                        //斜右下
                        newLineIniP = new Point(CuboidFrontRectEndP.X, CuboidFrontRectEndP.Y);
                        newLineEndP = new Point(CuboidFrontRectEndP.X + d, CuboidFrontRectEndP.Y - d);
                        pointList = new List<System.Windows.Point> { newLineIniP, newLineEndP };
                        point = new StylusPointCollection(pointList);
                        stroke = new Stroke(point) { DrawingAttributes = ShapeStrokeAttributes };
                        strokes.Add(stroke.Clone());
                        //竖左 (虚线)
                        newLineIniP = new Point(CuboidFrontRectIniP.X + d, CuboidFrontRectIniP.Y - d);
                        newLineEndP = new Point(CuboidFrontRectIniP.X + d, CuboidFrontRectEndP.Y - d);
                        strokes.Add(GenerateDashedLineStrokeCollection(newLineIniP, newLineEndP));
                        //竖右
                        newLineIniP = new Point(CuboidFrontRectEndP.X + d, CuboidFrontRectIniP.Y - d);
                        newLineEndP = new Point(CuboidFrontRectEndP.X + d, CuboidFrontRectEndP.Y - d);
                        pointList = new List<System.Windows.Point> { newLineIniP, newLineEndP };
                        point = new StylusPointCollection(pointList);
                        stroke = new Stroke(point) { DrawingAttributes = ShapeStrokeAttributes };
                        strokes.Add(stroke.Clone());

                        try
                        {
                            inkCanvas.Strokes.Remove(lastTempStrokeCollection);
                        }
                        catch { }
                        lastTempStrokeCollection = strokes;
                        inkCanvas.Strokes.Add(strokes);
                    }

                    break;
            }
        }

        #endregion

        #region 笔画生成辅助（ICA MW_ShapeDrawing.cs:1297-1467 原样移植）

        private List<System.Windows.Point> GenerateEllipseGeometry(System.Windows.Point st, System.Windows.Point ed, bool isDrawTop = true, bool isDrawBottom = true)
        {
            double a = 0.5 * (ed.X - st.X);
            double b = 0.5 * (ed.Y - st.Y);
            List<System.Windows.Point> pointList = new List<System.Windows.Point>();
            if (isDrawTop && isDrawBottom)
            {
                for (double r = 0; r <= 2 * Math.PI; r = r + 0.01)
                {
                    pointList.Add(new System.Windows.Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                }
            }
            else
            {
                if (isDrawBottom)
                {
                    for (double r = 0; r <= Math.PI; r = r + 0.01)
                    {
                        pointList.Add(new System.Windows.Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                    }
                }
                if (isDrawTop)
                {
                    for (double r = Math.PI; r <= 2 * Math.PI; r = r + 0.01)
                    {
                        pointList.Add(new System.Windows.Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                    }
                }
            }
            return pointList;
        }

        private StrokeCollection GenerateDashedLineEllipseStrokeCollection(System.Windows.Point st, System.Windows.Point ed, bool isDrawTop = true, bool isDrawBottom = true)
        {
            double a = 0.5 * (ed.X - st.X);
            double b = 0.5 * (ed.Y - st.Y);
            double step = 0.05;
            List<System.Windows.Point> pointList = new List<System.Windows.Point>();
            StylusPointCollection point;
            Stroke stroke;
            StrokeCollection strokes = new StrokeCollection();
            if (isDrawBottom)
            {
                for (double i = 0.0; i < 1.0; i += step * 1.66)
                {
                    pointList = new List<Point>();
                    for (double r = Math.PI * i; r <= Math.PI * (i + step); r = r + 0.01)
                    {
                        pointList.Add(new System.Windows.Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                    }
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    strokes.Add(stroke.Clone());
                }
            }
            if (isDrawTop)
            {
                for (double i = 1.0; i < 2.0; i += step * 1.66)
                {
                    pointList = new List<Point>();
                    for (double r = Math.PI * i; r <= Math.PI * (i + step); r = r + 0.01)
                    {
                        pointList.Add(new System.Windows.Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                    }
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = ShapeStrokeAttributes
                    };
                    strokes.Add(stroke.Clone());
                }
            }
            return strokes;
        }

        private Stroke GenerateLineStroke(System.Windows.Point st, System.Windows.Point ed)
        {
            List<System.Windows.Point> pointList = new List<System.Windows.Point>();
            StylusPointCollection point;
            Stroke stroke;
            pointList = new List<System.Windows.Point>{
                new System.Windows.Point(st.X, st.Y),
                new System.Windows.Point(ed.X, ed.Y)
            };
            point = new StylusPointCollection(pointList);
            stroke = new Stroke(point)
            {
                DrawingAttributes = ShapeStrokeAttributes
            };
            return stroke;
        }

        private Stroke GenerateArrowLineStroke(System.Windows.Point st, System.Windows.Point ed)
        {
            List<System.Windows.Point> pointList = new List<System.Windows.Point>();
            StylusPointCollection point;
            Stroke stroke;

            double w = 20, h = 7;
            double theta = Math.Atan2(st.Y - ed.Y, st.X - ed.X);
            double sint = Math.Sin(theta);
            double cost = Math.Cos(theta);

            pointList = new List<Point>
            {
                new Point(st.X, st.Y),
                new Point(ed.X , ed.Y),
                new Point(ed.X + (w * cost - h * sint), ed.Y + (w * sint + h * cost)),
                new Point(ed.X,ed.Y),
                new Point(ed.X + (w * cost + h * sint), ed.Y - (h * cost - w * sint))
            };
            point = new StylusPointCollection(pointList);
            stroke = new Stroke(point)
            {
                DrawingAttributes = ShapeStrokeAttributes
            };
            return stroke;
        }

        private StrokeCollection GenerateDashedLineStrokeCollection(System.Windows.Point st, System.Windows.Point ed)
        {
            double step = 5;
            List<System.Windows.Point> pointList = new List<System.Windows.Point>();
            StylusPointCollection point;
            Stroke stroke;
            StrokeCollection strokes = new StrokeCollection();
            double d = GetDistance(st, ed);
            double sinTheta = (ed.Y - st.Y) / d;
            double cosTheta = (ed.X - st.X) / d;
            for (double i = 0.0; i < d; i += step * 2.76)
            {
                pointList = new List<System.Windows.Point>{
                    new System.Windows.Point(st.X + i * cosTheta, st.Y + i * sinTheta),
                    new System.Windows.Point(st.X + Math.Min(i + step, d) * cosTheta, st.Y + Math.Min(i + step, d) * sinTheta)
                };
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = ShapeStrokeAttributes
                };
                strokes.Add(stroke.Clone());
            }
            return strokes;
        }

        private StrokeCollection GenerateDotLineStrokeCollection(System.Windows.Point st, System.Windows.Point ed)
        {
            double step = 3;
            List<System.Windows.Point> pointList = new List<System.Windows.Point>();
            StylusPointCollection point;
            Stroke stroke;
            StrokeCollection strokes = new StrokeCollection();
            double d = GetDistance(st, ed);
            double sinTheta = (ed.Y - st.Y) / d;
            double cosTheta = (ed.X - st.X) / d;
            for (double i = 0.0; i < d; i += step * 2.76)
            {
                var stylusPoint = new StylusPoint(st.X + i * cosTheta, st.Y + i * sinTheta, (float)0.8);
                point = new StylusPointCollection();
                point.Add(stylusPoint);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = ShapeStrokeAttributes
                };
                strokes.Add(stroke.Clone());
            }
            return strokes;
        }

        #endregion
    }
}
