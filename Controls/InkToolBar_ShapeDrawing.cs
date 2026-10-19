#nullable disable warnings

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using InkCanvasForPDF.ViewModels;

namespace InkCanvasForPDF.Controls;

/// <summary>墨迹浮动栏·形状手绘部分（自 Ink-Canvas-Artistry 4.9.4 MW_ShapeDrawing.cs 浮动栏区段移植）。
/// 选中形状 = SetActiveTool(Pen) + ShapeDrawMode；页面画布的实际绘制事件由 MainWindow 处理。
/// Pin 语义（用户规格）：固定（E718）= 仅图形按钮或批注按钮可关面板；非固定（E77A）= 点形状或点外部即关。
/// 固定状态不持久化（默认不置顶，仅本次会话有效）。</summary>
public partial class InkToolBar
{
    // ═══ 面板开合 / 图钉（ICA MW_ShapeDrawing.cs:20-55）═══

    /// <summary>面板固定状态：true=固定（E718），false=自动隐藏（E77A）。默认非固定，不持久化。</summary>
    private bool _shapePanelPinned;

    /// <summary>面板外点击关闭已挂接到宿主窗口（打开时挂接、Closed 摘除，处理器自判开合与固定状态）。</summary>
    private bool _shapeOutsideHooked;

    /// <summary>图形面板提示文字颜色：浅色主题 #666666 / 深色主题 #AAAAAA（打开时与主题切换时刷新）。</summary>
    private void ApplyShapeHintColor()
        => ShapeHintTextBlock.Foreground = new SolidColorBrush(
            Services.ThemeService.IsDark
                ? Color.FromRgb(0xAA, 0xAA, 0xAA)
                : Color.FromRgb(0x66, 0x66, 0x66));

    private void ImageDrawShape_Click(object sender, RoutedEventArgs e)
    {
        if (ShapePalette.IsOpen && !PopupFlyAnimator.IsClosing(ShapePalette))
        {
            CollapseBorderDrawShape();
            return;
        }
        // 滑出动画进行中再点按钮：中止滑出立即关闭后重开（避免 150ms 内点击无反应的死点）
        if (PopupFlyAnimator.IsClosing(ShapePalette))
            PopupFlyAnimator.AbortFlyOut(ShapePalette);
        // 面板跟随浮动栏缩放档（Popup 不在 Viewbox 内，需自行应用），再乘 0.65 让菜单小一号（同墨迹菜单）
        double s = ViewboxFloatingBarScaleTransform.ScaleX * 0.65;
        BorderDrawShape.LayoutTransform = new ScaleTransform(s, s);
        ShapePalette.PlacementTarget = BtnDrawShape;
        // 打开前预设动画初态（纯滑入：整块位于终点下方，150ms EaseOut 由快到慢滑入）：
        // Popup 首帧呈现时即为动画起点，不闪终态；Opacity 恒 1 无淡入
        PopupFlyAnimator.EnsureMeasured(BorderDrawShape);   // 首开 ActualHeight=0：先同步布局取真实高度
        BorderDrawShape.RenderTransform = new TranslateTransform(0, BorderDrawShape.ActualHeight);
        BorderDrawShape.RenderTransformOrigin = new Point(0.5, 1);
        ShapePalette.IsOpen = true;   // FlyIn 由 Opened 事件经 Dispatcher 启动；点外关闭在打开时挂接
        HookShapePanelOutsideClose();
    }

    private void CollapseBorderDrawShape()
    {
        PopupFlyAnimator.FlyOut(ShapePalette, BorderDrawShape);   // 滑出动画播完才 IsOpen=false（点外关闭挂接由 Closed 摘除）
    }

    /// <summary>面板滑入动画（纯滑入：整块从下方滑入，150ms CircleEase EaseOut 由快到慢，无淡入）。
    /// 由 ShapePalette.Opened 经 Dispatcher（Loaded 优先级）调用：Popup 子树就绪后动画才启动。
    /// 动画统一实现在 PopupFlyAnimator。</summary>
    private void FlyInShapePalette()
        => PopupFlyAnimator.FlyIn(ShapePalette, BorderDrawShape);

    /// <summary>面板外点击关闭（非固定状态生效；宿主 Window PreviewMouseDown 隧道入口，打开时挂接/关闭时摘除）。</summary>
    private void ShapePanelOutsideDown(object sender, MouseButtonEventArgs e)
    {
        if (_shapePanelPinned || !ShapePalette.IsOpen)
            return;
        if (e.OriginalSource is not DependencyObject src)
            return;
        // 点在面板内或图形入口按钮上交给各自处理器（入口按钮 Click 负责 toggle）
        if (BorderDrawShape.IsAncestorOf(src) || BtnDrawShape.IsAncestorOf(src))
            return;
        CollapseBorderDrawShape();
    }

    /// <summary>挂接宿主窗口的点外关闭（面板打开时调用一次；Loaded 后窗口引用可用）。</summary>
    private void HookShapePanelOutsideClose()
    {
        if (_shapeOutsideHooked) return;
        if (Window.GetWindow(this) is not { } w) return;
        w.AddHandler(UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler(ShapePanelOutsideDown), true);
        _shapeOutsideHooked = true;
    }

    /// <summary>摘除宿主窗口的点外关闭挂接（ShapePalette.Closed 时调用，对齐 PenPalette_Closed）。</summary>
    private void UnhookShapePanelOutsideClose()
    {
        if (!_shapeOutsideHooked) return;
        if (Window.GetWindow(this) is { } w)
            w.RemoveHandler(UIElement.PreviewMouseDownEvent,
                new MouseButtonEventHandler(ShapePanelOutsideDown));
        _shapeOutsideHooked = false;
    }

    private void Border_MouseDown(object sender, MouseButtonEventArgs e)
        => _lastBorderMouseDownObject = sender;

    private void SymbolIconPinBorderDrawShape_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_lastBorderMouseDownObject != sender) return;

        _shapePanelPinned = !_shapePanelPinned;
        BorderDrawShapePin.Glyph = _shapePanelPinned ? "\uE718" : "\uE77a";
        // 置顶状态不持久化：默认不置顶（仅本次会话有效）
    }

    private object _lastBorderMouseDownObject;

    // ═══ 按钮选中（ICA MW_ShapeDrawing.cs:57-467）═══

    object _lastMouseDownSender;

    /// <summary>ICA isLongPressSelected：长按 500ms 保持选中（全部图形项通用）。</summary>
    private bool _isLongPressSelected;

    /// <summary>长按保持选中（泛化）：按住 500ms 未松开即以正常选中样式选中该图形（无灰色动画）。
    /// mode 取自父 Border 的 Tag；短按（500ms 内松开）不在此处理，交给各 MouseUp handler。</summary>
    private async void Image_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Image img
            || LogicalTreeHelper.GetParent(img) is not Border { Tag: not null } b
            || !int.TryParse(b.Tag.ToString(), out var mode)) return;

        _lastMouseDownSender = sender;

        await Task.Delay(500);
        if (_lastMouseDownSender != sender) return;   // 已松开：短按路径

        _lastMouseDownSender = null;
        _isLongPressSelected = true;
        SelectShape(mode);
        // 长按保持选中提示：中下侧成功样式浮动通知（IUWM InfoBar，3 秒自动关闭）
        if (Window.GetWindow(this) is MainWindow mw)
            mw.ShowFloatingTip("已保持选中该图形");
    }

    /// <summary>选中形状：批注模式下页面画布 EditingMode 置 None（经 VM 绑定传播），实际绘制由 MainWindow 承担。
    /// 高亮「图形」按钮（仿画板 HideSubPanels("shape")）；非固定状态收起面板。</summary>
    private void SelectShape(int mode)
    {
        if (Vm is not { } vm) return;
        vm.InkTool.SetActiveTool(InkToolMode.Pen);   // 内部会先清 ShapeDrawMode，须再设回
        vm.InkTool.IsShapeLongPressSelected = _isLongPressSelected;
        vm.InkTool.ShapeDrawMode = mode;   // 高亮由 InkToolBar 的 ShapeDrawMode 订阅统一驱动（对齐画板）
        if (!_shapePanelPinned)
            CollapseBorderDrawShape();
    }

    // ICA Btn*_Click 语义已泛化为单个通用 handler：短按单击选中（复位锁定标志+清按下记录），
    // 长按路径（500ms 命中后 _lastMouseDownSender 已被清空）松开时不会重复触发。
    // mode 统一从父 Border 的 Tag 解析（XAML 中 24 个图形项的 MouseUp 均挂 Image_MouseUp）。
    private void Image_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_lastMouseDownSender != sender) return;
        _lastMouseDownSender = null;
        _isLongPressSelected = false;   // 短按 = 非锁定：画完自动回笔
        if (sender is Image img
            && LogicalTreeHelper.GetParent(img) is Border { Tag: not null } b
            && int.TryParse(b.Tag.ToString(), out var mode))
            SelectShape(mode);
    }

    // ═══ 图形项选中态（圆角方形强调色底 + 图标反色，仿墨迹栏 CheckBorder 手法）═══

    /// <summary>图形项 Border 查找表（Tag = ShapeDrawMode 编号），首次访问时构建。</summary>
    private Dictionary<int, Border>? _shapeItemBorders;

    private Dictionary<int, Border> GetShapeItemBorders()
    {
        if (_shapeItemBorders != null) return _shapeItemBorders;
        _shapeItemBorders = new Dictionary<int, Border>();
        CollectShapeItemBorders(BorderDrawShape);
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

    /// <summary>刷新图形面板选中态：选中项画纯强调色圆角方块（透明度跟随浮动栏设置），图标反色
    /// （浅色主题显白、深色主题显黑），其余图标为当前主题色。mode=0 全部复位为主题色。</summary>
    private void UpdateShapeItemHighlight(int mode)
    {
        var borders = GetShapeItemBorders();
        foreach (var b in borders.Values)
            b.Background = Brushes.Transparent;

        // 图标颜色直接控制：共享 DrawingImage 内部 DynamicResource 按"定义处上下文"解析，
        // Border.Resources 本地覆盖不会命中（主题级替换才生效）——故改为每图标克隆独占实例直接改画刷。
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
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(Vm?.Settings.AccentColor ?? "#0078D4"));
        brush.Opacity = Math.Clamp(Vm?.Settings.FloatingBarOpacity ?? 1, 0, 1);
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
                img.Source = clone;   // 仅本面板改用克隆；原字典实例不动，画板侧引用不受影响
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
}
