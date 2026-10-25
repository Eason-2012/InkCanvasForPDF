#nullable disable warnings

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using InkCanvasForPDF.Services;
using InkCanvasForPDF.ViewModels;

namespace InkCanvasForPDF.Controls;

/// <summary>墨迹浮动栏（XAML 由用户拷贝的 ICA 屏幕批注浮动栏转换；事件后置整体在此）。
/// 激活态沿用 ICA HideSubPanels；主题切换移植 ICA CheckColorTheme；面板静态定位（右缘距窗口右缘 16px、底缘距浮动栏顶 16px）。</summary>
public partial class InkToolBar : System.Windows.Controls.UserControl
{
    /// <summary>"文字注释"开关当前状态（true=显示文字，小圆条不出现）。</summary>
    private bool _floatBarTextVisible = true;
    private bool _inkToolHooked;   //「图形」高亮订阅防重复挂接（Loaded 可能多次触发）

    /// <summary>最近一次选中态模式（mouse/pen/color/eraser），供文字注释切换后幂等重刷。</summary>
    private string? _lastMode;

    /// <summary>选中项的移入叠加色（随主题刷新：浅 8% 黑 / 深 13% 白）。</summary>
    private Brush _hoverOverlayBrush = Brushes.Transparent;

    public InkToolBar()
    {
        InitializeComponent();
        // 墨迹选项 Popup 定位（照 ICA PenPalette.CustomPopupPlacementCallback）：
        // 水平居中于 PlacementTarget（墨迹按钮），悬于其上方 8px；PopupPrimaryAxis.Vertical 让屏幕边缘自动翻转
        PenPalette.CustomPopupPlacementCallback =
            (popupSize, targetSize, offset) => new[]
            {
                new CustomPopupPlacement(
                    new Point(targetSize.Width / 2 - popupSize.Width / 2, -popupSize.Height - 8),
                    PopupPrimaryAxis.Vertical)
            };
        // 关闭即摘除"点外关闭"挂接（挂接在打开时进行）
        PenPalette.Closed += PenPalette_Closed;
        // 橡皮选项 Popup 定位（与 PenPalette 完全一致：水平居中于橡皮按钮、悬于其上方 8px，屏幕边缘自动翻转）
        EraserPalette.CustomPopupPlacementCallback =
            (popupSize, targetSize, offset) => new[]
            {
                new CustomPopupPlacement(
                    new Point(targetSize.Width / 2 - popupSize.Width / 2, -popupSize.Height - 8),
                    PopupPrimaryAxis.Vertical)
            };
        // FlyIn 启动时机同 PenPalette（Opened 后经 Dispatcher（Loaded 优先级），首开播放完整）
        EraserPalette.Opened += (_, _) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FlyInEraserPalette));
        // 关闭即摘除"点外关闭"挂接（挂接在打开时进行；两个菜单都关着才真摘）
        EraserPalette.Closed += PenPalette_Closed;
        // 滑动清空滑块回弹变换（拖动/回弹动画统一作用于该 Transform）
        ClearInkThumb.RenderTransform = _clearInkTransform;
        // 图形面板 Popup 定位（照抄 PenPalette.CustomPopupPlacementCallback）：
        // 水平居中于 PlacementTarget（图形按钮），悬于其上方 8px；PopupPrimaryAxis.Vertical 让屏幕边缘自动翻转
        ShapePalette.CustomPopupPlacementCallback =
            (popupSize, targetSize, offset) => new[]
            {
                new CustomPopupPlacement(
                    new Point(targetSize.Width / 2 - popupSize.Width / 2, -popupSize.Height - 8),
                    PopupPrimaryAxis.Vertical)
            };
        // FlyIn 启动时机同 PenPalette（Opened 后经 Dispatcher（Loaded 优先级），首开播放完整）
        ShapePalette.Opened += (_, _) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FlyInShapePalette));
        // 关闭即摘除图形面板"点外关闭"挂接（挂接在打开时进行）
        ShapePalette.Closed += (_, _) => UnhookShapePanelOutsideClose();
        // FlyIn 在 Popup.Opened 后经 Dispatcher（Loaded 优先级）再启动：Popup 首次打开时子树尚未完成
        // 加载/呈现，若在 IsOpen=true 同帧启动 Storyboard，150ms 时钟已走，等首帧呈现动画已过半
        // → "第一次播放不完全"。初态（暗+偏下）已在打开前预设，此处只需等就绪后启动动画。
        PenPalette.Opened += (_, _) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FlyInPalette));
        // 主题切换联动：非彩色模式下按新主题重刷工具栏底色，并按当前工具刷新选中高亮色（深浅色叠加结果不同）
        Services.ThemeService.ThemeChanged += () =>
        {
            ApplyBarBackground(Vm?.Settings.IsColorfulViewboxFloatingBar ?? false,
                               Vm?.Settings.FloatingBarOpacity ?? 0.75);
            UpdateBoardToolState(Vm?.InkTool.ActiveTool switch
            {
                InkToolMode.Pen => "pen",
                InkToolMode.Eraser => "eraser",
                _ => "mouse"
            });
            ApplyShapeHintColor();   // 图形面板提示文字：浅 #666666 / 深 #AAAAAA
        };
        // 定位重算：宿主/栏尺寸任何变化（窗口缩放、缩放滑块、布局完成）都重新摆放栏
        Loaded += (_, _) => ReapplyPlacement();
        SizeChanged += (_, _) => ReapplyPlacement();
        ViewboxFloatingBar.SizeChanged += (_, _) => ReapplyPlacement();
                Loaded += (_, _) =>
            {
                // 启动恢复：MainViewModel 构造已用 LoadFromSettings 装入记忆页签的活动值，此处只回填面板 UI
                if (Vm is { } vm)
                {
                    // Pivot 初始页签按记忆的墨迹类型落位（先摘掉处理器避免触发页签切换回写）
                    PivotInk.SelectionChanged -= PivotInk_SelectionChanged;
                    PivotInk.SelectedIndex = (int)vm.InkTool.InkType;
                    PivotInk.SelectionChanged += PivotInk_SelectionChanged;
                    SyncActiveTabControls();
                    // 三个页签的多指书写开关同步到 VM 实际值（全局共享）
                    ToggleSwitchEnableMultiFingerInkHard.IsOn = vm.InkTool.IsMultiFingerInk;
                    ToggleSwitchEnableMultiFingerInkSoft.IsOn = vm.InkTool.IsMultiFingerInk;
                    ToggleSwitchEnableMultiFingerInkHighlighter.IsOn = vm.InkTool.IsMultiFingerInk;
                    // 两个页签（硬笔/软笔）的墨迹纠正开关同步到 VM 实际值（全局共享；荧光笔无此项）
                    ToggleSwitchEnableInkToShapeHard.IsOn = vm.InkTool.IsInkToShapeEnabled;
                    ToggleSwitchEnableInkToShapeSoft.IsOn = vm.InkTool.IsInkToShapeEnabled;
                    // 形状面板固定状态不持久化：默认不置顶（Pin 切换仅本次会话有效）
                    ApplyShapeHintColor();   // 图形面板提示文字初始颜色按当前主题
                    // 「图形」高亮与 VM 同步（对齐画板）：仅选中形状时高亮图形按钮，退出形状模式按当前工具复位
                    if (!_inkToolHooked)
                    {
                        _inkToolHooked = true;
                        vm.InkTool.PropertyChanged += (_, e) =>
                        {
                            // 设置页/画板改墨迹纠正开关 → 两个页签的开关镜像 VM 实际值（程序化赋同值不回环）
                            if (e.PropertyName == nameof(InkToolViewModel.IsInkToShapeEnabled))
                            {
                                ToggleSwitchEnableInkToShapeHard.IsOn = Vm?.InkTool.IsInkToShapeEnabled ?? false;
                                ToggleSwitchEnableInkToShapeSoft.IsOn = Vm?.InkTool.IsInkToShapeEnabled ?? false;
                                return;
                            }
                            if (e.PropertyName != nameof(InkToolViewModel.ShapeDrawMode)) return;
                            // 图形面板选中态：选中项强调色圆角底+图标反色，归 0 全部复位
                            UpdateShapeItemHighlight(Vm?.InkTool.ShapeDrawMode ?? 0);
                            if ((Vm?.InkTool.ShapeDrawMode ?? 0) != 0)
                                UpdateBoardToolState("shape");
                            else
                                UpdateBoardToolState(Vm?.InkTool.ActiveTool switch
                                {
                                    InkToolMode.Pen => "pen",
                                    InkToolMode.Eraser => "eraser",
                                    _ => "mouse"
                                });
                        };
                    }
                }
                ApplyThemeToPalette();   // 色块 + 主题图标/文字 + 当前色勾标一并恢复
                UpdateBoardToolState("mouse");   // 首次加载高亮"鼠标"（默认即浏览模式，ICA 未做但需要）
            };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // ══════════ 定位（宿主铺满整窗，栏位置由代码算 Margin 驱动）══════════

    private double _placementRightMargin = 16;
    private double _placementBottomMargin = 16;

    /// <summary>记录目标边距并立即重算栏位置（右/下边距 = 栏距窗口右/底缘的距离）。</summary>
    public void ApplyPlacement(double rightMargin, double bottomMargin)
    {
        _placementRightMargin = rightMargin;
        _placementBottomMargin = bottomMargin;
        ReapplyPlacement();
    }

    /// <summary>把栏摆到宿主右下：实测主 Border 视觉右/下角锚定边距；右/下恒为大负值留呼吸空间，
    /// 使墨迹选项面板的负 Margin 伸出栏外的部分始终落在宿主范围内（可渲染、可命中）。</summary>
    private void ReapplyPlacement()
    {
        double w = ViewboxFloatingBar.ActualWidth;
        double h = ViewboxFloatingBar.ActualHeight;
        if (w < 1 || h < 1 || ActualWidth < 1 || ActualHeight < 1)
            return;   // 尚未布局：SizeChanged/Loaded 钩子会在布局完成后再次触发

        // 实测主 Border 视觉右/下角（含 Viewbox 内部缩放与缩放设置的 LayoutTransform），
        // 减去当前 Margin 平移得到与 Margin 无关的锚定常量 K：
        // 旧算法 w 不含 LayoutTransform 缩放且 Left 对齐以左上为锚放大，缩放 >100% 时栏飞出窗口；
        // 同时天然补偿内部 Margin 造成的右/底视觉偏差。
        double kx = w, ky = h;   // 兜底：TransformToVisual 不可用时退回旧算法
        if (BorderFloatingBarMainControls.Parent is not null)
        {
            var toSelf = BorderFloatingBarMainControls.TransformToVisual(this);
            if (toSelf.TryTransform(
                    new Point(BorderFloatingBarMainControls.ActualWidth,
                              BorderFloatingBarMainControls.ActualHeight), out var br))
            {
                kx = br.X - ViewboxFloatingBar.Margin.Left;
                ky = br.Y - ViewboxFloatingBar.Margin.Top;
            }
        }

        double left = Math.Max(0, ActualWidth  - _placementRightMargin  - kx);
        double top  = Math.Max(0, ActualHeight - _placementBottomMargin - ky);
        ViewboxFloatingBar.Margin = new Thickness(left, top, -2000, -200);
    }

    /// <summary>把当前墨迹选项写回 Data\Settings.json（墨迹类型 + 共用画笔颜色/粗细 + 荧光笔颜色/粗细 + 全局笔锋 + 不透明度 + 亮暗系）。</summary>
    private void SaveInkOptions()
    {
        if (Vm is not { } vm) return;
        var t = vm.InkTool;
        vm.Settings.InkType = (int)t.InkType;
        // 活动类型的值直接以活动值覆盖对应存储字段（硬/软笔共用，改完即存）
        switch (t.InkType)
        {
            case InkPenType.Hard:
            case InkPenType.Soft:
                vm.Settings.PenColor = t.InkColor;
                vm.Settings.PenWidth = t.InkWidth;
                break;
            case InkPenType.Highlighter:
                vm.Settings.HighlighterColor = t.InkColor;
                vm.Settings.HighlighterWidth = t.InkWidth;
                break;
        }
        vm.Settings.InkStyle = t.InkStyle;   // 笔锋全局共享
        vm.Settings.IsInkToShapeEnabled = t.IsInkToShapeEnabled;   // 墨迹纠正全局共享
        vm.Settings.IsMultiFingerInkEnabled = t.IsMultiFingerInk;   // 多指书写全局共享
        vm.Settings.InkAlpha = t.InkAlpha;
        vm.Settings.UseLightThemeColor = t.IsUselightThemeColor;
        SettingsService.Save(vm.Settings);
    }

    /// <summary>把 VM 当前活动值回填到当前页签的控件（笔锋下拉/粗细滑块；荧光笔页签为不透明度+粗细）。
    /// 程序化赋值触发的事件按同值幂等回写。页签切换与启动恢复共用。</summary>
    private void SyncActiveTabControls()
    {
        if (Vm is not { } vm)
            return;
        switch (PivotInk.SelectedIndex)
        {
            case 0:
                InkWidthSliderHard.Value = vm.InkTool.InkWidth;
                break;
            case 1:
                InkWidthSliderSoft.Value = vm.InkTool.InkWidth;
                break;
            case 2:
                InkAlphaSlider.Value = vm.InkTool.InkAlpha;
                InkHighlighterWidthSlider.Value = vm.InkTool.InkWidth;
                break;
        }
    }

    /// <summary>设置浮动工具栏是否显示文字注释（抄 ICA isEnableDisPlayFloatBarText + MW_SettingsToLoad）：
/// 切换母开关 FloatBarSelectIconTextBlock.Visibility（其余按钮文字已绑定其 Visibility 自动跟随）；
/// 图标高度按 ICA 切换——显示文字=22（给文字让位），隐藏=32（撑满按钮，蓝区=全按钮高）；
/// 撤销仅调 Margin（ICA 做法）。图标栈无固定高，由按钮内容自动垂直居中（与撤销一致）。</summary>
public void ApplyFloatBarText(bool show)
{
    _floatBarTextVisible = show;
    FloatBarSelectIconTextBlock.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    if (Icon_Mouse is { } m) m.Height = show ? 22 : 32;
    if (Icon_Pen is { } p) p.Height = show ? 22 : 32;
    if (Icon_Eraser1 is { } e) e.Height = show ? 22 : 32;
    if (Icon_Undo is { } u) u.Margin = show ? new Thickness(0, 1.5, 0, -1.5) : new Thickness(0);

    // 小圆条可见性依赖"文字注释"开关：重刷一次选中态（幂等）
    if (_lastMode is { } mode)
        UpdateBoardToolState(mode);
}

    /// <summary>应用墨迹栏背景（设置"显示背景色"+"透明度"+ 主题联动）：彩色=ICA 渐变画刷（固定 alpha）；
    /// 否则=当前主题 FloatBarBackground 画刷 Clone 后按 opacity 设背景半透明（图标不受影响）。
    /// （缩放/边距由 MainWindow 统一设置。）</summary>
    public void ApplyBarBackground(bool colorful, double opacity)
    {
        if (colorful)
        {
            var gradientBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            gradientBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x95, 0x80, 0xB0, 0xFF), 0));
            gradientBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x95, 0xC0, 0xFF, 0xC0), 1));
            BorderFloatingBarMainControls.Background = gradientBrush;
        }
        else
        {
            if ((Brush)FindResource("FloatBarBackground") is not { } baseBrush)
                return;
            var brush = baseBrush.Clone();
            brush.Opacity = Math.Clamp(opacity, 0, 1);
            BorderFloatingBarMainControls.Background = brush;
        }

        // 彩色渐变背景为浅色系（ICA 固定蓝→绿渐变）：即使深色模式，图标/文字也保持黑色保证可读；
        // 非彩色时回落到当前主题的 FloatBarForeground（主题切换会重新走到这里）。
        // 弹窗（PenPalette）内部仍用 FloatBarForeground，不受此覆盖影响。
        Resources["FloatBarIconForeground"] = colorful
            ? new SolidColorBrush(Colors.Black)
            : (SolidColorBrush)FindResource("FloatBarForeground");

        // 移入/按下色：彩色模式恢复 ICA 最早版本（深色青 #2200CDCD/#4400CDCD，浅色白透明）；
        // 非彩色时移除覆盖，回落 Theme/Light-Dark.xaml 的中性色（深色 #22FFFFFF/#44FFFFFF）。
        if (colorful)
        {
            Resources["FloatBarButtonBackgroundPointerOverKey"] = Services.ThemeService.IsDark
                ? (Color)ColorConverter.ConvertFromString("#2200CDCD")
                : (Color)ColorConverter.ConvertFromString("#66FFFFFF");
            Resources["FloatBarButtonButtonBackgroundPressedKey"] = Services.ThemeService.IsDark
                ? (Color)ColorConverter.ConvertFromString("#4400CDCD")
                : (Color)ColorConverter.ConvertFromString("#99FFFFFF");
        }
        else
        {
            Resources.Remove("FloatBarButtonBackgroundPointerOverKey");
            Resources.Remove("FloatBarButtonButtonBackgroundPressedKey");
        }
        UpdateBoardToolState(_lastMode);   // 彩色开关切换/主题变化后同步刷新选中色与圆条（_lastMode 为 null 时跳过）
        UpdateShapeItemHighlight(Vm?.InkTool.ShapeDrawMode ?? 0);   // 主题切换：未选中图标回主题色、选中图标保持反色
    }

    /// <summary>激活态：复位 3 个工具图标区，再给当前工具加勾选底纹（圆角 5 = 外浮动栏一致）+ Opacity 0.99。
    /// 灰区画在 CheckBorder 上（hover 不被按钮模板覆盖）；隐藏文字时图标撑满按钮，底部叠加强调色小圆条。</summary>
    private void UpdateBoardToolState(string? mode)
    {
        _lastMode = mode;
        MouseCheckBorder.Background = Brushes.Transparent;
        MouseCheckBorder.Opacity = 1;
        MouseCheckBorder.Resources.Remove("FloatBarIconForeground");
        MouseAccentPill.Visibility = Visibility.Collapsed;
        PenCheckBorder.Background = Brushes.Transparent;
        PenCheckBorder.Opacity = 1;
        PenCheckBorder.Resources.Remove("FloatBarIconForeground");
        PenAccentPill.Visibility = Visibility.Collapsed;
        EraserCheckBorder.Background = Brushes.Transparent;
        EraserCheckBorder.Opacity = 1;
        EraserCheckBorder.Resources.Remove("FloatBarIconForeground");
        EraserAccentPill.Visibility = Visibility.Collapsed;
        ShapeCheckBorder.Background = Brushes.Transparent;
        ShapeCheckBorder.Opacity = 1;
        ShapeCheckBorder.Resources.Remove("FloatBarIconForeground");
        ShapeAccentPill.Visibility = Visibility.Collapsed;
        SelectCheckBorder.Background = Brushes.Transparent;
        SelectCheckBorder.Opacity = 1;
        SelectCheckBorder.Resources.Remove("FloatBarIconForeground");
        SelectAccentPill.Visibility = Visibility.Collapsed;
        if (mode != "pen" && mode != "color")
            PopupFlyAnimator.FlyOut(PenPalette, PenPaletteRoot);

        // 选中色按状态分层：
        // ①彩色背景（ICA 最早版本；彩色栏深浅主题观感一致，恒用浅色值）：亮 #88CAED
        // ②开文字注释：强调色 50% 混白（浅）/混黑（深）
        // ③关文字注释（圆条模式）：浅 #E2E4E8 / 深 #525252
        bool colorful = Vm?.Settings.IsColorfulViewboxFloatingBar ?? false;
        Color check;
        if (colorful)
            check = Color.FromRgb(0x88, 0xCA, 0xED);
        else if (_floatBarTextVisible)
        {
            // 开文字注释：纯强调色实色（不再与白/黑混 50%）
            check = (Color)ColorConverter.ConvertFromString(Vm?.Settings.AccentColor ?? "#005FB0");
        }
        else
            check = Services.ThemeService.IsDark ? Color.FromRgb(0x52, 0x52, 0x52)
                                                 : Color.FromRgb(0xD6, 0xDA, 0xE0);
        var pillVisible = !_floatBarTextVisible && !colorful ? Visibility.Visible : Visibility.Collapsed;
        // 选中色画刷：恒为实心，不跟随栏不透明度（栏透明度只作用于背景画刷）；
        // CheckBorder.Opacity=0.99 仅激活态合成约定，视觉≈1。
        var checkBrush = new SolidColorBrush(check);
        // 纯强调色选中态（开文字注释、非彩色）：选中项图标/文字反色——浅色主题显白、深色主题显黑。
        // 经 CheckBorder.Resources 本地覆盖 FloatBarIconForeground，其子树 DynamicResource（图标+文字）优先命中；
        // 复位段 Resources.Remove 即恢复主题前景。
        var invertedIconBrush = !colorful && _floatBarTextVisible
            ? new SolidColorBrush(Services.ThemeService.IsDark ? Colors.Black : Colors.White)
            : null;
        // 选中项的移入反馈：选中底色不透明盖住按钮模板移入背景，故用叠加层（选中底色之上、图标之下）
        _hoverOverlayBrush = new SolidColorBrush(Services.ThemeService.IsDark
            ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        MouseHoverOverlay.Visibility = Visibility.Collapsed;
        PenHoverOverlay.Visibility = Visibility.Collapsed;
        EraserHoverOverlay.Visibility = Visibility.Collapsed;
        switch (mode)
        {
            case "mouse":
                MouseCheckBorder.Background = checkBrush;
                MouseCheckBorder.Opacity = 0.99;
                if (invertedIconBrush != null)
                    MouseCheckBorder.Resources["FloatBarIconForeground"] = invertedIconBrush;
                MouseAccentPill.Visibility = pillVisible;
                break;
            case "pen":
            case "color":
                PenCheckBorder.Background = checkBrush;
                PenCheckBorder.Opacity = 0.99;
                if (invertedIconBrush != null)
                    PenCheckBorder.Resources["FloatBarIconForeground"] = invertedIconBrush;
                PenAccentPill.Visibility = pillVisible;
                break;
            case "eraser":
                EraserCheckBorder.Background = checkBrush;
                EraserCheckBorder.Opacity = 0.99;
                if (invertedIconBrush != null)
                    EraserCheckBorder.Resources["FloatBarIconForeground"] = invertedIconBrush;
                EraserAccentPill.Visibility = pillVisible;
                break;
            case "shape":
                ShapeCheckBorder.Background = checkBrush;
                ShapeCheckBorder.Opacity = 0.99;
                if (invertedIconBrush != null)
                    ShapeCheckBorder.Resources["FloatBarIconForeground"] = invertedIconBrush;
                ShapeAccentPill.Visibility = pillVisible;
                break;
            case "select":
                SelectCheckBorder.Background = checkBrush;
                SelectCheckBorder.Opacity = 0.99;
                if (invertedIconBrush != null)
                    SelectCheckBorder.Resources["FloatBarIconForeground"] = invertedIconBrush;
                SelectAccentPill.Visibility = pillVisible;
                break;
        }
    }

    /// <summary>选中项悬停反馈：仅当该按钮是当前选中工具时显示叠加层（未选中项由按钮模板负责）。</summary>
    private void ToolButton_MouseEnter(object sender, MouseEventArgs e)
        => SetHoverOverlay((Border)((System.Windows.Controls.Button)sender).Content, true);

    private void ToolButton_MouseLeave(object sender, MouseEventArgs e)
        => SetHoverOverlay((Border)((System.Windows.Controls.Button)sender).Content, false);

    private void SetHoverOverlay(Border checkBorder, bool hovered)
    {
        var overlay = checkBorder == MouseCheckBorder ? MouseHoverOverlay
            : checkBorder == PenCheckBorder ? PenHoverOverlay
            : checkBorder == EraserCheckBorder ? EraserHoverOverlay
            : checkBorder == ShapeCheckBorder ? ShapeHoverOverlay
            : checkBorder == SelectCheckBorder ? SelectHoverOverlay
            : null;
        if (overlay == null) return;
        var m = _lastMode;
        bool selected = m != null && ((checkBorder == MouseCheckBorder && m == "mouse") ||
                                      (checkBorder == PenCheckBorder && (m == "pen" || m == "color")) ||
                                      (checkBorder == EraserCheckBorder && m == "eraser") ||
                                      (checkBorder == ShapeCheckBorder && m == "shape") ||
                                      (checkBorder == SelectCheckBorder && m == "select"));
        if (hovered && selected)
        {
            overlay.Background = _hoverOverlayBrush;
            overlay.Visibility = Visibility.Visible;
        }
        else
        {
            overlay.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>鼠标（浏览/滚动）模式。</summary>
    private void CursorIcon_Click(object sender, RoutedEventArgs e)
    {
        Vm?.InkTool.SetActiveTool(InkToolMode.None);
        UpdateBoardToolState("mouse");
    }

    /// <summary>批注按钮：未激活→切批注模式；已激活→切换墨迹选项 Popup（照 ICA：Popup 开关，位置由 Custom 回调一步算定）。
    /// 形状模式选中时：点批注=切换回普通批注模式（不弹墨迹菜单），形状面板一并关闭。</summary>
    private void PenIcon_Click(object sender, RoutedEventArgs e)
    {
        // 形状模式选中（面板可能已被自动隐藏，须按 ShapeDrawMode 判断）：点批注=退出形状回普通批注
        bool exitingShape = (Vm?.InkTool.ShapeDrawMode ?? 0) != 0 || ShapePalette.IsOpen;
        if (exitingShape)
        {
            CollapseBorderDrawShape();
            if (Vm is { } vm0)
            {
                vm0.InkTool.IsShapeLongPressSelected = false;
                vm0.InkTool.ShapeDrawMode = 0;   // 通知 EditingMode 绑定回到 Ink
            }
        }

        if (Vm?.InkTool.ActiveTool != InkToolMode.Pen)
        {
            Vm?.InkTool.SetActiveTool(InkToolMode.Pen);
            UpdateBoardToolState("pen");
        }
        else if (exitingShape)
        {
            UpdateBoardToolState("pen");   // 从形状切回普通批注：仅复位高亮（shape→pen），不弹墨迹菜单
        }
        else if (PenPalette.IsOpen && !PopupFlyAnimator.IsClosing(PenPalette))
        {
            PopupFlyAnimator.FlyOut(PenPalette, PenPaletteRoot);
        }
        else
        {
            // 滑出动画进行中再点按钮：中止滑出立即关闭后重开（避免 150ms 内点击无反应的死点）
            if (PopupFlyAnimator.IsClosing(PenPalette))
                PopupFlyAnimator.AbortFlyOut(PenPalette);
            // 面板跟随浮动栏缩放档（Popup 不在 Viewbox 内，需自行应用），再乘 0.6 让菜单小一号
            double s = ViewboxFloatingBarScaleTransform.ScaleX * 0.65;
            PenPaletteRoot.LayoutTransform = new ScaleTransform(s, s);
            PenPalette.PlacementTarget = BtnPen;
            // 互斥：打开墨迹菜单时关掉橡皮菜单（避免双菜单叠加）
            if (EraserPalette.IsOpen)
                PopupFlyAnimator.FlyOut(EraserPalette, EraserPaletteRoot);
            // 打开前预设动画初态（纯滑入：整块位于终点下方，150ms EaseOut 由快到慢滑入）：
            // Popup 首帧呈现（可能晚于打开）时即为动画起点，不闪终态；Opacity 恒 1 无淡入
            PopupFlyAnimator.EnsureMeasured(PenPaletteRoot);   // 首开 ActualHeight=0：先同步布局取真实高度
            PenPaletteRoot.RenderTransform = new TranslateTransform(0, PenPaletteRoot.ActualHeight);
            PenPaletteRoot.RenderTransformOrigin = new Point(0.5, 1);
            PenPalette.IsOpen = true;   // FlyIn 由 Opened 事件经 Dispatcher 启动（见构造器）
            HookOutsideClose();   // 点外关闭（墨迹按钮自身豁免，toggle 不受影响）
        }
    }

    // ────────── 墨迹选项菜单关闭（点外关闭；色块/多指书写/抬笔纠正在各自处理器里关）──────────

    /// <summary>窗口失去焦点时关闭墨迹菜单与图形菜单（MainWindow.Deactivated 调用；
    /// 置顶只影响"点外是否关"，失焦属系统级一律关；播 150ms 滑出动画后真正关闭，
    /// Closed 事件自行摘除点外挂接）。</summary>
    public void CloseMenusOnDeactivate()
    {
        PopupFlyAnimator.FlyOut(PenPalette, PenPaletteRoot);
        PopupFlyAnimator.FlyOut(EraserPalette, EraserPaletteRoot);
        PopupFlyAnimator.FlyOut(ShapePalette, BorderDrawShape);
    }

    /// <summary>宿主窗口外点关闭挂接：PreviewMouseDown + handledEventsToo 覆盖一切点击（含已被处理的）。
    /// 显式转 MouseButtonEventHandler：方法组会被推断为 Action&lt;,&gt;，AddHandler 运行时抛"处理程序类型不匹配"。
    /// 幂等挂接（点外关闭同时管墨迹/橡皮两个菜单，只挂一次）。</summary>
    private bool _outsideCloseHooked;

    private void HookOutsideClose()
    {
        if (_outsideCloseHooked) return;
        if (Window.GetWindow(BtnPen) is { } w)
        {
            w.AddHandler(UIElement.PreviewMouseDownEvent, (MouseButtonEventHandler)OutsideCloseHandler, true);
            _outsideCloseHooked = true;
        }
    }

    /// <summary>任一菜单 Closed 时调用：墨迹/橡皮菜单都已关着才摘除点外挂接。</summary>
    private void PenPalette_Closed(object? sender, EventArgs e)
    {
        if (!PenPalette.IsOpen && !EraserPalette.IsOpen)
            UnhookOutsideClose();
    }

    private void UnhookOutsideClose()
    {
        if (!_outsideCloseHooked) return;
        if (Window.GetWindow(BtnPen) is { } w)
            w.RemoveHandler(UIElement.PreviewMouseDownEvent, (MouseButtonEventHandler)OutsideCloseHandler);
        _outsideCloseHooked = false;
    }

    private void OutsideCloseHandler(object sender, MouseButtonEventArgs e)
    {
        bool penOpen = PenPalette.IsOpen;
        bool eraserOpen = EraserPalette.IsOpen;
        if (!penOpen && !eraserOpen)
        {
            UnhookOutsideClose();
            return;
        }
        if (e.OriginalSource is not DependencyObject d) return;
        // 点在墨迹面板内或墨迹按钮上不关（按钮留给 PenIcon_Click 自己 toggle）；橡皮同理
        if (penOpen && !IsDescendantOf(d, PenPaletteRoot) && !IsDescendantOf(d, BtnPen))
        {
            PopupFlyAnimator.FlyOut(PenPalette, PenPaletteRoot);   // 滑出动画幂等，重复点外不叠加
        }
        if (eraserOpen && !IsDescendantOf(d, EraserPaletteRoot) && !IsDescendantOf(d, BtnEraser))
        {
            PopupFlyAnimator.FlyOut(EraserPalette, EraserPaletteRoot);
        }
    }

    /// <summary>视觉/逻辑树向上查找祖先（Popup 子树用视觉树，与主树隔离）。</summary>
    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
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

    /// <summary>面板滑入动画（纯滑入：整块从下方滑入，150ms CircleEase EaseOut 由快到慢，无淡入）。
    /// 由 PenPalette.Opened 经 Dispatcher（Loaded 优先级）调用：Popup 子树就绪后动画才启动，首开播放完整。
    /// 动画统一实现在 PopupFlyAnimator（初态已在打开前预设，此处幂等重设）。</summary>
    private void FlyInPalette()
        => PopupFlyAnimator.FlyIn(PenPalette, PenPaletteRoot);

    /// <summary>面积擦（EraserShape 由 MainWindow 在切橡皮瞬间统一设置，保证黑板擦指针首显）。
    /// 交互对齐批注按钮：未激活→只切橡皮（不弹菜单）；已激活→切换橡皮选项 Popup。</summary>
    private void EraserIcon_Click(object sender, RoutedEventArgs e)
    {
        if (Vm?.InkTool.ActiveTool != InkToolMode.Eraser)
        {
            Vm?.InkTool.SetActiveTool(InkToolMode.Eraser);
            UpdateBoardToolState("eraser");
        }
        else if (EraserPalette.IsOpen && !PopupFlyAnimator.IsClosing(EraserPalette))
        {
            PopupFlyAnimator.FlyOut(EraserPalette, EraserPaletteRoot);
        }
        else
        {
            // 滑出动画进行中再点按钮：中止滑出立即关闭后重开（避免 150ms 内点击无反应的死点）
            if (PopupFlyAnimator.IsClosing(EraserPalette))
                PopupFlyAnimator.AbortFlyOut(EraserPalette);
            // 互斥：打开橡皮菜单时关掉墨迹菜单（避免双菜单叠加）
            if (PenPalette.IsOpen)
                PopupFlyAnimator.FlyOut(PenPalette, PenPaletteRoot);
            // 面板跟随浮动栏缩放档（Popup 不在 Viewbox 内，需自行应用），再乘 0.65 让菜单小一号
            double s = ViewboxFloatingBarScaleTransform.ScaleX * 0.65;
            EraserPaletteRoot.LayoutTransform = new ScaleTransform(s, s);
            EraserPalette.PlacementTarget = BtnEraser;
            // 打开前回填 UI：Pivot 页签、粗细滑块（值=SliderFromEraserSize(EraserSize)）、手掌擦除/笔尖模式开关、滑动清空滑块复位
            if (Vm is { } vm)
            {
                PivotEraser.SelectedIndex = vm.InkTool.IsStrokeEraser ? 1 : 0;
                EraserSizeSlider.Value = InkToolViewModel.SliderFromEraserSize(vm.InkTool.EraserSize);
                _syncingEraserToggles = true;
                ToggleSwitchPalmEraseArea.IsOn = ToggleSwitchPalmEraseStroke.IsOn = vm.Settings.IsPalmEraseEnabled;
                ToggleSwitchNibModeArea.IsOn = ToggleSwitchNibModeStroke.IsOn = vm.Settings.IsNibModeEnabled;
                ToggleSwitchNibModeArea.IsEnabled = ToggleSwitchNibModeStroke.IsEnabled = vm.Settings.IsPalmEraseEnabled;
                _syncingEraserToggles = false;
            }
            ResetClearInkSlider();
            // 打开前预设动画初态（纯滑入：整块位于终点下方，150ms EaseOut 由快到慢滑入）
            PopupFlyAnimator.EnsureMeasured(EraserPaletteRoot);   // 首开 ActualHeight=0：先同步布局取真实高度
            EraserPaletteRoot.RenderTransform = new TranslateTransform(0, EraserPaletteRoot.ActualHeight);
            EraserPaletteRoot.RenderTransformOrigin = new Point(0.5, 1);
            EraserPalette.IsOpen = true;   // FlyIn 由 Opened 事件经 Dispatcher 启动（见构造器）
            HookOutsideClose();            // 点外关闭（橡皮按钮自身豁免，toggle 不受影响）
        }
    }

    /// <summary>橡皮菜单滑入动画（同 FlyInPalette：由 Opened 经 Dispatcher（Loaded 优先级）调用，首开播放完整）。</summary>
    private void FlyInEraserPalette()
        => PopupFlyAnimator.FlyIn(EraserPalette, EraserPaletteRoot);

    /// <summary>Pivot 页签切换 → 面积擦/墨迹擦（IsStrokeEraser 驱动 EditingMode 通知页面画布），并持久化共享设置
    /// （画板橡皮菜单读写同一 EraserModeByStrokeSetting，两侧模式实时同步）。
    /// 顺序照画板 EraserIconByStrokes_Click：先设 EraserShape 再写模式——PDF 侧模式经绑定传播
    /// （订阅早于 MainWindow 刷新），必须在写 VM 之前把形状设好；同帧"形状→模式"触发 WPF 光标失效重建，
    /// 面积擦大矩形点擦光标不再在墨迹擦模式残留成大白方块。</summary>
    private void PivotEraser_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is { } vm)
        {
            bool byStroke = PivotEraser.SelectedIndex == 1;
            if (Window.GetWindow(this) is MainWindow mainWindow)
                mainWindow.ApplyEraserShapeForMode(byStroke);
            vm.InkTool.IsStrokeEraser = byStroke;
            if (vm.Settings.EraserModeByStrokeSetting != byStroke)
            {
                vm.Settings.EraserModeByStrokeSetting = byStroke;
                SettingsService.Save(vm.Settings);
            }
        }
    }

    /// <summary>橡皮粗细滑块（1-30）→ 实际 EraserSize=15+3.5x（MainWindow 监听变更统一刷 EraserShape），
    /// 并回写 EraserSizeSetting 持久化（同值守卫：菜单打开回填滑块不重复存盘）。</summary>
    private void EraserSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Vm is not { } vm) return;
        vm.InkTool.EraserSize = InkToolViewModel.EraserSizeFromSlider(e.NewValue);
        if (vm.Settings.EraserSizeSetting != (int)e.NewValue)
        {
            vm.Settings.EraserSizeSetting = (int)e.NewValue;
            SettingsService.Save(vm.Settings);
        }
    }

    // ────────── 手掌擦除 / 笔尖模式（橡皮菜单两页签各一份开关，靠同一处理器 + 重入守卫互同步）──────────

    /// <summary>回填/互同步重入守卫（菜单打开回填与页签互同步时不写回设置）。</summary>
    private bool _syncingEraserToggles;

    /// <summary>手掌擦除开关（面积擦/墨迹擦两页签共享）：写设置 → 持久化 → 互同步两开关 →
    /// 联动笔尖模式开关的可操作性（手掌擦除关则笔尖模式不可点；值不清零，重开后恢复原选择）。</summary>
    private void ToggleSwitchPalmErase_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingEraserToggles) return;
        if (sender is not iNKORE.UI.WPF.Modern.Controls.ToggleSwitch ts || Vm is not { } vm) return;

        if (vm.Settings.IsPalmEraseEnabled != ts.IsOn)
        {
            vm.Settings.IsPalmEraseEnabled = ts.IsOn;
            SettingsService.Save(vm.Settings);
        }

        _syncingEraserToggles = true;
        if (ToggleSwitchPalmEraseArea is { } pa) pa.IsOn = ts.IsOn;
        if (ToggleSwitchPalmEraseStroke is { } ps) ps.IsOn = ts.IsOn;
        if (ToggleSwitchNibModeArea is { } na) na.IsEnabled = ts.IsOn;
        if (ToggleSwitchNibModeStroke is { } ns) ns.IsEnabled = ts.IsOn;
        _syncingEraserToggles = false;
    }

    /// <summary>笔尖模式开关（面积擦/墨迹擦两页签共享）：写设置 → 持久化 → 互同步两开关。
    /// 只改手掌识别阈值（笔尖档/手指档），手掌擦除关闭时该开关被禁用、不会走到这里。</summary>
    private void ToggleSwitchNibMode_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingEraserToggles) return;
        if (sender is not iNKORE.UI.WPF.Modern.Controls.ToggleSwitch ts || Vm is not { } vm) return;

        if (vm.Settings.IsNibModeEnabled != ts.IsOn)
        {
            vm.Settings.IsNibModeEnabled = ts.IsOn;
            SettingsService.Save(vm.Settings);
        }

        _syncingEraserToggles = true;
        if (ToggleSwitchNibModeArea is { } na) na.IsOn = ts.IsOn;
        if (ToggleSwitchNibModeStroke is { } ns) ns.IsOn = ts.IsOn;
        _syncingEraserToggles = false;
    }

    // ────────── 滑动清空滑块（确认样式：拖到底松手清空当前页后直接回左，无已清空特效/红底）──────────

    /// <summary>滑块行程：(轨道160 - 边框2) 内部158 - 滑块40 - 左右内边距各4 = 110（抵到右端时右侧间隙与左侧 4px 相等）。</summary>
    private const double ClearInkMaxX = 110;

    private double _clearInkX;                                   // 当前位移
    private readonly TranslateTransform _clearInkTransform = new();   // 拖动/回弹统一作用的变换

    private void ClearInkThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        // 回弹动画中途抓取：DP 读回动画当前值 → 摘动画 → 基值对齐（避免跳变）
        _clearInkX = Math.Clamp(_clearInkTransform.X, 0, ClearInkMaxX);
        ClearInkThumb.BeginAnimation(TranslateTransform.XProperty, null);
        _clearInkTransform.X = _clearInkX;
    }

    private void ClearInkThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _clearInkX = Math.Clamp(_clearInkX + e.HorizontalChange, 0, ClearInkMaxX);
        _clearInkTransform.X = _clearInkX;
        // 文字：滑块一动即快速淡出（12px 内隐没）
        ClearInkLabel.Opacity = Math.Max(0, 1 - _clearInkX / 12);
    }

    private void ClearInkThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_clearInkX >= ClearInkMaxX * 0.95)
            CompleteClearInk();
        else
            AnimateClearInkBack();
    }

    /// <summary>键盘可达：方向键步进（行程/10）、Home 归零、End 直达即清空。</summary>
    private void ClearInkThumb_KeyDown(object sender, KeyEventArgs e)
    {
        double step = ClearInkMaxX / 10;
        switch (e.Key)
        {
            case Key.Right:
            case Key.Up:
                _clearInkX = Math.Min(ClearInkMaxX, _clearInkX + step);
                break;
            case Key.Left:
            case Key.Down:
                _clearInkX = Math.Max(0, _clearInkX - step);
                break;
            case Key.Home:
                _clearInkX = 0;
                break;
            case Key.End:
                _clearInkX = ClearInkMaxX;
                break;
            default:
                return;
        }
        e.Handled = true;
        ClearInkThumb.BeginAnimation(TranslateTransform.XProperty, null);
        _clearInkTransform.X = _clearInkX;
        ClearInkLabel.Opacity = Math.Max(0, 1 - _clearInkX / 12);
        if (_clearInkX >= ClearInkMaxX)
            CompleteClearInk();
    }

    /// <summary>滑到底：清空当前显示页墨迹（MainWindow 统一入口，绕面积擦累加器并登记可撤销历史），随即回左。
    /// 仅在成功清空时按设置自动切换编辑模式并关闭橡皮菜单（未成功：滑块回弹、菜单保持打开）。</summary>
    private void CompleteClearInk()
    {
        bool cleared = false;
        if (Window.GetWindow(this) is MainWindow mainWindow)
            cleared = mainWindow.ClearCurrentPageInk();
        AnimateClearInkBack();
        if (!cleared || Vm is not { } vm) return;

        switch (vm.Settings.ClearInkAutoSwitchMode)
        {
            case 1:   // 切换到鼠标
                CursorIcon_Click(null!, null!);
                break;
            case 2:   // 切换到墨迹（清空时 ActiveTool=Eraser，必走切模式分支，不会误开墨迹菜单）
                PenIcon_Click(null!, null!);
                break;
            case 3:   // 切换到选择
                SelectIcon_Click(null!, null!);
                break;
            // case 0 = 保持在橡皮：不切
        }
        PopupFlyAnimator.FlyOut(EraserPalette, EraserPaletteRoot);   // 成功清空即关橡皮菜单（滑出动画，幂等）
    }

    /// <summary>回弹到初始位：文字立即复原，滑块 160ms EaseOut 滑回（无淡入）。</summary>
    private void AnimateClearInkBack()
    {
        double from = _clearInkX;
        _clearInkX = 0;
        ClearInkLabel.Opacity = 1;
        if (from <= 0)
        {
            _clearInkTransform.X = 0;
            return;
        }
        ClearInkThumb.BeginAnimation(TranslateTransform.XProperty, null);
        _clearInkTransform.X = 0;   // 基值归零：动画结束后回落正确
        var anim = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) => ClearInkThumb.BeginAnimation(TranslateTransform.XProperty, null);
        ClearInkThumb.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    /// <summary>打开菜单前复位滑块（拖动中途关菜单的兜底）。</summary>
    private void ResetClearInkSlider()
    {
        _clearInkX = 0;
        ClearInkThumb.BeginAnimation(TranslateTransform.XProperty, null);
        _clearInkTransform.X = 0;
        ClearInkLabel.Opacity = 1;
    }

    /// <summary>选择（ICA 屏幕浮动栏 SymbolIconSelect_Click 语义：切入 InkCanvas 内建套索选择模式）。</summary>
    private void SelectIcon_Click(object sender, RoutedEventArgs e)
    {
        Vm?.InkTool.SetActiveTool(InkToolMode.Select);
        UpdateBoardToolState("select");
    }

    // ────────── 亮暗主题切换（移植 ICA ColorThemeSwitch_MouseUp + CheckColorTheme 色块/图标/文字部分）──────────

    private void ColorThemeSwitch_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        vm.InkTool.IsUselightThemeColor = !vm.InkTool.IsUselightThemeColor;
        ApplyThemeToPalette();
        SaveInkOptions();
    }

    /// <summary>按当前主题重绘硬笔/软笔两套 9 色块 + 主题图标 + "亮系/暗系"文字 + 选中勾标（亮暗系全局共享）。</summary>
    private void ApplyThemeToPalette()
    {
        if (Vm is not { } vm)
            return;

        var map = vm.InkTool.IsUselightThemeColor ? InkToolViewModel.PenLightColorMap : InkToolViewModel.PenDarkColorMap;
        var swatchSets = new (int Key, Border? Hard, Border? Soft)[]
        {
            (0, BorderPenColorBlackHard, BorderPenColorBlackSoft),
            (5, BorderPenColorWhiteHard, BorderPenColorWhiteSoft),
            (1, BorderPenColorRedHard, BorderPenColorRedSoft),
            (6, BorderPenColorPinkHard, BorderPenColorPinkSoft),
            (8, BorderPenColorOrangeHard, BorderPenColorOrangeSoft),
            (4, BorderPenColorYellowHard, BorderPenColorYellowSoft),
            (2, BorderPenColorGreenHard, BorderPenColorGreenSoft),
            (7, BorderPenColorTealHard, BorderPenColorTealSoft),
            (3, BorderPenColorBlueHard, BorderPenColorBlueSoft),
        };
        foreach (var (key, hard, soft) in swatchSets)
        {
            if (map.TryGetValue(key, out var color))
            {
                if (hard != null)
                    hard.Background = new SolidColorBrush(color);
                if (soft != null)
                    soft.Background = new SolidColorBrush(color);
            }
        }

        bool light = vm.InkTool.IsUselightThemeColor;
        ColorThemeSwitchIconHard.Glyph = light ? "\uE708" : "\uE706";
        ColorThemeSwitchTextBlockHard.Text = light ? "暗系" : "亮系";
        ColorThemeSwitchIconSoft.Glyph = light ? "\uE708" : "\uE706";
        ColorThemeSwitchTextBlockSoft.Text = light ? "暗系" : "亮系";

        UpdateInkColorCheckmarks(vm.InkTool.InkColor);
    }

    // ────────── 颜色选择（色块用 MouseUp，抄 ICA BtnColorXxx_MouseUp 语义）──────────

    private void BtnColorBlack_Click(object sender, MouseButtonEventArgs e) => SetInkColor(0);
    private void BtnColorWhite_Click(object sender, MouseButtonEventArgs e) => SetInkColor(5);
    private void BtnColorRed_Click(object sender, MouseButtonEventArgs e) => SetInkColor(1);
    private void BtnColorPink_Click(object sender, MouseButtonEventArgs e) => SetInkColor(6);
    private void BtnColorOrange_Click(object sender, MouseButtonEventArgs e) => SetInkColor(8);
    private void BtnColorYellow_Click(object sender, MouseButtonEventArgs e) => SetInkColor(4);
    private void BtnColorGreen_Click(object sender, MouseButtonEventArgs e) => SetInkColor(2);
    private void BtnColorTeal_Click(object sender, MouseButtonEventArgs e) => SetInkColor(7);
    private void BtnColorBlue_Click(object sender, MouseButtonEventArgs e) => SetInkColor(3);

    private void BtnHighlighterColorRed_Click(object sender, MouseButtonEventArgs e) => SetInkColor(101);
    private void BtnHighlighterColorOrange_Click(object sender, MouseButtonEventArgs e) => SetInkColor(102);
    private void BtnHighlighterColorYellow_Click(object sender, MouseButtonEventArgs e) => SetInkColor(103);
    private void BtnHighlighterColorTeal_Click(object sender, MouseButtonEventArgs e) => SetInkColor(104);
    private void BtnHighlighterColorBlue_Click(object sender, MouseButtonEventArgs e) => SetInkColor(105);
    private void BtnHighlighterColorPurple_Click(object sender, MouseButtonEventArgs e) => SetInkColor(106);

    /// <summary>设置墨迹颜色：切到批注模式 + 更新选中勾标 + 关闭墨迹菜单（点任意色块即关；亮暗切换不走此入口不关）。</summary>
    private void SetInkColor(int inkColor)
    {
        Vm?.InkTool.SetInkColor(inkColor);
        UpdateInkColorCheckmarks(inkColor);
        Vm?.InkTool.SetActiveTool(InkToolMode.Pen);
        UpdateBoardToolState("color");
        PopupFlyAnimator.FlyOut(PenPalette, PenPaletteRoot);
        SaveInkOptions();
    }

    /// <summary>选中色显示勾标（checked-*.png），其余隐藏（硬笔/软笔两套色板同刷，当前页签外不可见无副作用）。</summary>
    private void UpdateInkColorCheckmarks(int inkColor)
    {
        var map = new Dictionary<int, Viewbox?>
        {
            [0] = ViewboxBtnColorBlackHardContent,
            [5] = ViewboxBtnColorWhiteHardContent,
            [1] = ViewboxBtnColorRedHardContent,
            [6] = ViewboxBtnColorPinkHardContent,
            [8] = ViewboxBtnColorOrangeHardContent,
            [4] = ViewboxBtnColorYellowHardContent,
            [2] = ViewboxBtnColorGreenHardContent,
            [7] = ViewboxBtnColorTealHardContent,
            [3] = ViewboxBtnColorBlueHardContent,
            [101] = ViewboxBtnHighlighterColorRedContent,
            [102] = ViewboxBtnHighlighterColorOrangeContent,
            [103] = ViewboxBtnHighlighterColorYellowContent,
            [104] = ViewboxBtnHighlighterColorTealContent,
            [105] = ViewboxBtnHighlighterColorBlueContent,
            [106] = ViewboxBtnHighlighterColorPurpleContent,
        };
        foreach (var (key, box) in map)
        {
            if (box != null)
                box.Visibility = key == inkColor ? Visibility.Visible : Visibility.Collapsed;
        }
        if (inkColor <= 100)   // 画笔色：软笔色板同步勾标
        {
            var softMap = new Dictionary<int, Viewbox?>
            {
                [0] = ViewboxBtnColorBlackSoftContent,
                [5] = ViewboxBtnColorWhiteSoftContent,
                [1] = ViewboxBtnColorRedSoftContent,
                [6] = ViewboxBtnColorPinkSoftContent,
                [8] = ViewboxBtnColorOrangeSoftContent,
                [4] = ViewboxBtnColorYellowSoftContent,
                [2] = ViewboxBtnColorGreenSoftContent,
                [7] = ViewboxBtnColorTealSoftContent,
                [3] = ViewboxBtnColorBlueSoftContent,
            };
            foreach (var (key, box) in softMap)
            {
                if (box != null)
                    box.Visibility = key == inkColor ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    // ────────── 笔锋/多指书写/滑块 ──────────

    private void InkWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Vm != null)
            Vm.InkTool.InkWidth = e.NewValue;
        SaveInkOptions();
    }

    private void InkAlphaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Vm != null)
            Vm.InkTool.InkAlpha = (byte)e.NewValue;
        SaveInkOptions();
    }

    private void InkHighlighterWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Vm != null)
            Vm.InkTool.InkWidth = e.NewValue;   // 荧光笔页签激活时 InkWidth 即荧光笔粗细（按类型记忆由 VM 处理）
        SaveInkOptions();
    }

    /// <summary>多指书写开关（三页签共享）：写 VM → 三开关互同步 → 持久化 → 不关墨迹菜单（点击开关保持菜单打开）。</summary>
    private void ToggleSwitchEnableMultiFingerInk_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is iNKORE.UI.WPF.Modern.Controls.ToggleSwitch ts && Vm != null)
        {
            Vm.InkTool.IsMultiFingerInk = ts.IsOn;
            // 三个页签的多指书写开关状态互相同步（程序化赋同值不会再次触发 Toggled）
            if (ToggleSwitchEnableMultiFingerInkHard is { } a) a.IsOn = ts.IsOn;
            if (ToggleSwitchEnableMultiFingerInkSoft is { } b) b.IsOn = ts.IsOn;
            if (ToggleSwitchEnableMultiFingerInkHighlighter is { } c) c.IsOn = ts.IsOn;
            SaveInkOptions();
        }
    }

    /// <summary>墨迹纠正开关（硬笔/软笔两个页签，全局共享）：写 VM → 同步两个开关 → 持久化 → 不关墨迹菜单（与多指书写同款，保持菜单打开）。</summary>
    private void ToggleSwitchEnableInkToShape_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is iNKORE.UI.WPF.Modern.Controls.ToggleSwitch ts && Vm != null)
        {
            Vm.InkTool.IsInkToShapeEnabled = ts.IsOn;
            // 两个页签的墨迹纠正开关状态互相同步（程序化赋同值不会再次触发 Toggled）
            if (ToggleSwitchEnableInkToShapeHard is { } a) a.IsOn = ts.IsOn;
            if (ToggleSwitchEnableInkToShapeSoft is { } b) b.IsOn = ts.IsOn;
            SaveInkOptions();
        }
    }

    /// <summary>Pivot「硬笔/软笔/荧光笔」页签切换：VM 换入对应类型记忆的选项（压感随软/硬笔联动），
    /// 回填控件、刷新勾标并持久化。</summary>
    private void PivotInk_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { } vm || PivotInk.SelectedIndex < 0)
            return;
        vm.InkTool.SwitchInkType((InkPenType)PivotInk.SelectedIndex);
        SyncActiveTabControls();
        ApplyThemeToPalette();
        SaveInkOptions();
    }

    /// <summary>点击"画板"：请求主窗口进入画板模式（画板由 MainWindow 承载，浮动栏自身不管画板）。</summary>
    public event EventHandler? BoardRequested;

    private void BoardIcon_Click(object sender, RoutedEventArgs e)
        => BoardRequested?.Invoke(this, EventArgs.Empty);
}
