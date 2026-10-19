using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using InkCanvasForPDF.Services;
using InkCanvasForPDF.ViewModels;
using iNKORE.UI.WPF.Modern.Controls;
using Newtonsoft.Json.Linq;
using FIcon = FluentIcons.Common.Icon;

namespace InkCanvasForPDF.Controls;

/// <summary>设置覆盖层：NavigationView 侧栏（书写/外观）导航 + 右侧 SettingsCard 设置区。
/// 设置修改即时写回 InkToolViewModel 并保存到 Data\Settings.json。</summary>
public partial class SettingsView : System.Windows.Controls.UserControl
{
    /// <summary>浮动栏文字注释切换时触发（由 MainWindow 转发给 InkToolBar）。</summary>
    public event Action<bool>? FloatBarTextChanged;

    /// <summary>「显示画笔光标」切换时触发（由 MainWindow 同步黑板 PenCursorEnabled，PDF 侧走绑定即时生效）。</summary>
    public event Action<bool>? PenCursorChanged;

    /// <summary>「墨迹平滑」切换时触发（由 MainWindow 同步黑板 DefaultDrawingAttributes.FitToCurve，PDF 侧走 InkTool.FitToCurve）。</summary>
    public event Action<bool>? InkSmoothChanged;

    /// <summary>「时间笔锋墨迹点长满时间」修改时触发（由 MainWindow 同步所有页面时间笔锋控制器）。</summary>
    public event Action<int>? TimePenGrowMsChanged;

    /// <summary>「选择笔锋样式」变更时触发（由 MainWindow 同步画板侧 InkStyle 并落盘）。</summary>
    public event Action<int>? PenStyleChanged;

    /// <summary>"选择笔锋样式"下拉框索引 → InkStyle 内部值（时间笔锋显示在第一位，但内部值仍为 3，兼容已保存的设置）。</summary>
    private static readonly int[] PenStyleMap = { 3, 0, 1, 2 };

    /// <summary>PDF 墨迹栏 / 黑板墨迹栏布局设置（缩放/边距/背景色）变更时触发（由 MainWindow 应用并转发 BoardView）。</summary>
    public event Action? BarLayoutChanged;

    /// <summary>「墨迹识别灵敏度」滑块变更时触发（值 = 相似度门槛百分比，由 MainWindow 推送到 PDF/画板两侧识别引擎）。</summary>
    public event Action<int>? InkToShapeSensitivityChanged;

    /// <summary>停顿强制识别 4 项设置任一变更时触发（由 MainWindow 统一重推画板；PDF 侧经活值 lambda 自动生效）。</summary>
    public event Action? PauseInkToShapeChanged;

    public SettingsView()
    {
        InitializeComponent();
        // 更新状态机/进度变化 → 重渲染状态区（本控件与应用同生命周期，构造期订阅一次即可；
        // ApplyUpdateState 带可选参不能直接方法组转换，用 lambda）
        UpdateService.StateChanged += () => ApplyUpdateState();
        UpdateService.ProgressChanged += () => ApplyUpdateState();
        Loaded += (_, _) =>
        {
            // 默认选中"墨迹书写"导航项；若实际未触发选中，也强制落到墨迹书写页。
            SelectDefaultItem();
            if (DataContext is MainViewModel vm)
            {
                // 把已保存的画笔光标设置应用到墨迹工具。
                vm.InkTool.ShowPenCursor = vm.Settings.IsShowCursor;
                // 已保存的墨迹平滑开关应用到墨迹工具。
                vm.InkTool.FitToCurve = vm.Settings.IsFitToCurve;
                // 已保存的时间笔锋长满时间回填输入框（TextChanged 幂等回写同值）。
                TimePenGrowMsBox.Text = vm.Settings.TimePenGrowMs.ToString();
                // 已保存的笔锋样式回填下拉框，并按样式决定"时间笔锋长满时间"卡片可见性。
                ComboBoxPenStyle.SelectedIndex = Math.Max(0, Array.IndexOf(PenStyleMap, Math.Clamp(vm.Settings.InkStyle, 0, 3)));
                UpdateTimePenCardVisibility();
                // 已保存的橡皮粗细 → 实际直径（1~20 ×4），保证橡皮光标与设置一致。
                vm.InkTool.EraserSize = vm.Settings.EraserSizeSetting * 4.0;
                // 外观页初始即按保存值同步浮动栏文字。
                FloatBarTextChanged?.Invoke(vm.Settings.IsEnableDisPlayFloatBarText);
                // 启动恢复：主题选择与两条墨迹栏缩放/透明度滑块（滑块值 = 设置 ×100；边距滑块由绑定自动同步）。
                ComboBoxTheme.SelectedIndex = Math.Clamp(vm.Settings.Theme, 0, 2);
                FloatingBarScaleSlider.Value = vm.Settings.FloatingBarScale * 100;
                BlackboardScaleSlider.Value = vm.Settings.BlackboardBarScale * 100;
                FloatingBarOpacitySlider.Value = vm.Settings.FloatingBarOpacity * 100;
                BlackboardOpacitySlider.Value = vm.Settings.BlackboardBarOpacity * 100;
                // 边距数值提示（带 px；滑块 0~50，超界旧值按界显示）。
                FloatingBarBottomMarginValue.Text = $"{Math.Clamp((int)vm.Settings.FloatingBarBottomMargin, 0, 50)}px";
                FloatingBarRightMarginValue.Text = $"{Math.Clamp((int)vm.Settings.FloatingBarRightMargin, 0, 50)}px";
                BlackboardBottomMarginValue.Text = $"{Math.Clamp((int)vm.Settings.BlackboardBarBottomMargin, 0, 50)}px";
                // 主题色：应用保存的强调色。
                ThemeService.ApplyAccent(vm.Settings.AccentColor);
                // 墨迹识别：相似度门槛滑块回填（百分比即门槛；开关走绑定自动同步）。
                InkToShapeSensitivitySlider.Value = vm.Settings.InkToShapeSensitivity;
                InkToShapeSensitivityValue.Text = $"{vm.Settings.InkToShapeSensitivity}%";   // 显式同步提示文字，不依赖 ValueChanged 触发
                // 停顿强制识别：滑块与两个输入框回填（开关走绑定自动同步；TextChanged 幂等回写同值）。
                PauseInkToShapeSensitivitySlider.Value = vm.Settings.PauseInkToShapeSensitivity;
                PauseInkToShapeSensitivityValue.Text = $"{vm.Settings.PauseInkToShapeSensitivity}%";   // 显式同步提示文字，不依赖 ValueChanged 触发
                PauseInkToShapeDelayMsBox.Text = vm.Settings.PauseInkToShapeDelayMs.ToString();
                PauseInkToShapeToleranceBox.Text = vm.Settings.PauseInkToShapeTolerancePx.ToString();
                // 手掌擦除：阈值输入框回填 + 笔尖模式开关可用性同步（开关走绑定自动同步）。
                PalmModeThresholdBox.Text = vm.Settings.FingerModeBoundsWidth.ToString();
                NibModeThresholdBox.Text = vm.Settings.NibModeBoundsWidth.ToString();
                NibModeToggle.IsEnabled = vm.Settings.IsPalmEraseEnabled;
                // 数据存储：保留天数输入框回填（两个开关走绑定自动同步）。
                AutoCleanDaysBox.Text = vm.Settings.AutoCleanInkDays.ToString();
                // 应用更新：通道/代理回填（自动检查开关走绑定自动同步）+ 状态区首次渲染。
                UpdateChannelCombo.SelectedIndex = Math.Clamp(vm.Settings.UpdateChannel, 0, 1);
                UpdateProxyBox.Text = vm.Settings.UpdateProxy;
                ApplyUpdateState();
            }
        };
    }

    /// <summary>点击强调按钮 → Carrot 取色窗口，确定后应用强调色 + 存盘。</summary>
    private void AccentColorChevron_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        var dialog = new Carrot.UI.ColorPicker.ColorPickerDialog(
            (Color)ColorConverter.ConvertFromString(vm.Settings.AccentColor))
        {
            Title = "主题色",
            ButtonOKText = "确定",
            ButtonCancelText = "取消",
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true)
            return;
        var c = dialog.PickedColor;
        vm.Settings.AccentColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        ThemeService.ApplyAccent(vm.Settings.AccentColor);
        SettingsService.Save(vm.Settings);
    }

    /// <summary>"设置为默认PDF应用"：写注册表设 .pdf 默认 + 刷新 Shell，
    /// 成功走成功样式浮动通知、失败走错误样式（均 3 秒自动消失，不打断操作）。</summary>
    private void SetDefaultPdfApp_Click(object sender, RoutedEventArgs e)
    {
        if (FileAssociationService.TrySetAsDefaultPdf(out var error))
            ShowFloatingTipFromOwner("已成功设置为默认PDF打开应用",
                iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity.Success);
        else
            ShowFloatingTipFromOwner(error ?? "设置默认程序失败，请手动在系统设置中设置",
                iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity.Error);
    }

    /// <summary>取宿主 MainWindow 发浮动通知（同 InkToolBar 范式；拿不到宿主时不弹，静默记日志）。</summary>
    private void ShowFloatingTipFromOwner(string message, iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity severity)
    {
        if (Window.GetWindow(this) is MainWindow mw)
            mw.ShowFloatingTip(message, severity);
        else
            App.LogText("FloatingTip 不可用（无 MainWindow 宿主）：" + message);
    }

    /// <summary>默认选中"墨迹书写"导航项（首个 MenuItem），并把右侧内容切到墨迹书写页。</summary>
    private void SelectDefaultItem()
    {
        if (SettingsNav.SelectedItem == null && SettingsNav.MenuItems.Count > 0)
            SettingsNav.SelectedItem = SettingsNav.MenuItems[0];
        ShowSection("writing");
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>点 NavigationView 内置返回箭头：退出设置回到上一页（PDF/主页）。</summary>
    private void SettingsNav_BackRequested(NavigationView sender,
        iNKORE.UI.WPF.Modern.Controls.NavigationViewBackRequestedEventArgs args)
    {
        if (Vm is { } vm)
            vm.IsSettingsVisible = false;
    }

    private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not NavigationViewItem item)
            return;
        ShowSection(item.Tag as string ?? "writing");
    }

    /// <summary>依导航选择切换右侧设置区（writing=墨迹书写，appearance=应用主题，pdfbar=PDF阅读，boardbar=画板书写，inkrecog=墨迹识别，about=关于应用）。</summary>
    private void ShowSection(string tag)
    {
        WritingSection.Visibility = tag == "writing" ? Visibility.Visible : Visibility.Collapsed;
        AppearanceSection.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        PdfBarSection.Visibility = tag == "pdfbar" ? Visibility.Visible : Visibility.Collapsed;
        BoardBarSection.Visibility = tag == "boardbar" ? Visibility.Visible : Visibility.Collapsed;
        InkRecognitionSection.Visibility = tag == "inkrecog" ? Visibility.Visible : Visibility.Collapsed;
        PalmEraseSection.Visibility = tag == "palmerase" ? Visibility.Visible : Visibility.Collapsed;
        DataStorageSection.Visibility = tag == "datastorage" ? Visibility.Visible : Visibility.Collapsed;
        UpdateSection.Visibility = tag == "update" ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "update")
            ApplyUpdateState();   // 进入更新页时按最新状态渲染（启动初始化可能晚于本控件 Loaded）
    }

    /// <summary>关于页超链接：用系统默认浏览器打开 Tag 中的 URL。</summary>
    private void AboutHyperlink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink { Tag: string url })
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>第三方代码参考：读取编译进程序集的 Resources\dependencies.g.json，
    /// 以「库名 + 链接」列表在 iuwm ContentDialog 中展示（格式对齐旧项目 AboutView）。</summary>
    private async void ThirdPartyDeps_Click(object sender, RoutedEventArgs e)
    {
        var root = new iNKORE.UI.WPF.Controls.SimpleStackPanel { Spacing = 8, Margin = new Thickness(8) };
        var items = LoadDependencyInfos();
        if (items.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "未找到第三方依赖清单。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "ToolbarForegroundBrush");
            root.Children.Add(empty);
        }
        else
        {
            foreach (var (name, url) in items)
            {
                var item = new StackPanel { Orientation = Orientation.Vertical };
                var nameBlock = new TextBlock
                {
                    Text = name,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 2),
                };
                nameBlock.SetResourceReference(TextBlock.ForegroundProperty, "ToolbarForegroundBrush");
                item.Children.Add(nameBlock);

                if (!string.IsNullOrWhiteSpace(url))
                    item.Children.Add(BuildDependencyLink(url));
                root.Children.Add(item);
            }
        }

        var dialog = new ContentDialog
        {
            Title = "第三方代码参考",
            Content = new ScrollViewer
            {
                Content = root,
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            CloseButtonText = "关闭",
            Owner = Window.GetWindow(this) ?? Application.Current.MainWindow,
        };
        dialog.SetResourceReference(ContentDialog.CloseButtonStyleProperty,
            iNKORE.UI.WPF.Modern.ThemeKeys.AccentButtonStyleKey);
        await dialog.ShowAsync();
    }

    /// <summary>依赖条目链接行：12px 强调色链接文本，点击复用 AboutHyperlink_Click 打开 Tag 中的 URL。</summary>
    private TextBlock BuildDependencyLink(string url)
    {
        var linkBlock = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        linkBlock.SetResourceReference(TextBlock.ForegroundProperty,
            iNKORE.UI.WPF.Modern.ThemeKeys.AccentFillColorDefaultBrushKey);
        var link = new Hyperlink(new Run(url)) { Tag = url };
        link.Click += AboutHyperlink_Click;
        linkBlock.Inlines.Add(link);
        return linkBlock;
    }

    /// <summary>读取编译进程序集的第三方依赖清单，按显示名排序；缺资源/解析失败记日志并返回空表。</summary>
    private static List<(string Name, string Url)> LoadDependencyInfos()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/dependencies.g.json");
            using var stream = Application.GetResourceStream(uri)!.Stream;
            using var reader = new StreamReader(stream);
            var arr = JArray.Parse(reader.ReadToEnd());

            var list = new List<(string Name, string Url)>();
            foreach (var token in arr)
            {
                var name = (string?)token["RepositoryId"] ?? (string?)token["PackageId"];
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var url = (string?)token["RepositoryProjectUrl"]
                          ?? (string?)token["PackageProjectUrl"]
                          ?? (string?)token["LicenseUrl"] ?? "";
                list.Add((name!, url));
            }
            return list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            App.Log("About.LoadDependencies", ex);
            return new List<(string Name, string Url)>();
        }
    }

    private void ShowCursorToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        vm.InkTool.ShowPenCursor = vm.Settings.IsShowCursor;
        PenCursorChanged?.Invoke(vm.Settings.IsShowCursor);   // 同步黑板即时生效
        SettingsService.Save(vm.Settings);
    }

    private void InkSmoothToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        vm.InkTool.FitToCurve = vm.Settings.IsFitToCurve;
        InkSmoothChanged?.Invoke(vm.Settings.IsFitToCurve);   // 同步黑板即时生效
        SettingsService.Save(vm.Settings);
    }

    /// <summary>笔锋样式选择：写回设置与 InkTool（PDF 侧即时生效）、更新时间笔锋卡片可见性、存盘并通知 MainWindow 同步画板。</summary>
    private void ComboBoxPenStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { } vm || ComboBoxPenStyle.SelectedIndex < 0)
            return;
        int style = PenStyleMap[ComboBoxPenStyle.SelectedIndex];
        vm.Settings.InkStyle = style;
        vm.InkTool.InkStyle = style;   // PDF 侧即时生效（PropertyChanged → MainWindow.RefreshTimePressureArmed）
        UpdateTimePenCardVisibility();
        SettingsService.Save(vm.Settings);
        PenStyleChanged?.Invoke(style);
    }

    /// <summary>时间笔锋长满时间卡片仅当笔锋样式 = 时间笔锋（3）时显示。</summary>
    private void UpdateTimePenCardVisibility()
        => TimePenGrowMsCard.Visibility = (Vm?.Settings.InkStyle == 3) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>时间笔锋长满时间输入：解析并钳制 10~1000ms 后写回设置、存盘并通知 MainWindow 同步控制器。</summary>
    private void TimePenGrowMsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        if (!int.TryParse(TimePenGrowMsBox.Text.Trim(), out int ms))
            return;   // 中途输入（空串/负号等）不处理，保留上次有效值
        ms = Math.Clamp(ms, 10, 1000);
        if (vm.Settings.TimePenGrowMs == ms)
            return;   // 回填或同值：不重复存盘
        vm.Settings.TimePenGrowMs = ms;
        SettingsService.Save(vm.Settings);
        TimePenGrowMsChanged?.Invoke(ms);
    }

    private void FloatBarTextToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        FloatBarTextChanged?.Invoke(vm.Settings.IsEnableDisPlayFloatBarText);
        SettingsService.Save(vm.Settings);
    }

    // ────────── 颜色主题（抄 ICA ComboBoxTheme_SelectionChanged：记偏好 → 应用 → 存盘）──────────

    private void ComboBoxTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        vm.Settings.Theme = ComboBoxTheme.SelectedIndex;
        ThemeService.Apply(vm.Settings.Theme);
        SettingsService.Save(vm.Settings);
    }

    // ────────── PDF 墨迹栏 / 黑板墨迹栏布局（缩放/边距/背景色；任何变更 → 存盘 + 通知 MainWindow 应用）──────────

    private void FloatingBarScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Vm is not { } vm)
            return;
        vm.Settings.FloatingBarScale = e.NewValue / 100.0;
        FloatingBarScaleValue.Text = $"{(int)e.NewValue}%";
        BarLayoutChanged?.Invoke();
        SettingsService.Save(vm.Settings);
    }

    private void FloatingBarBottomMarginSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Vm is not { } vm)
            return;
        FloatingBarBottomMarginValue.Text = $"{(int)e.NewValue}px";   // 滑块 TwoWay 绑定已写回 Settings
        BarLayoutChanged?.Invoke();
        SettingsService.Save(vm.Settings);
    }

    private void FloatingBarRightMarginSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Vm is not { } vm)
            return;
        FloatingBarRightMarginValue.Text = $"{(int)e.NewValue}px";
        BarLayoutChanged?.Invoke();
        SettingsService.Save(vm.Settings);
    }

    private void FloatingBarOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Vm is not { } vm)
            return;
        vm.Settings.FloatingBarOpacity = e.NewValue / 100.0;
        FloatingBarOpacityValue.Text = $"{(int)e.NewValue}%";
        BarLayoutChanged?.Invoke();
        SettingsService.Save(vm.Settings);
    }

    private void ColorfulFloatingBarToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        BarLayoutChanged?.Invoke();
        SettingsService.Save(vm.Settings);
    }

    private void BlackboardScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Vm is not { } vm)
            return;
        vm.Settings.BlackboardBarScale = e.NewValue / 100.0;
        BlackboardScaleValue.Text = $"{(int)e.NewValue}%";
        BarLayoutChanged?.Invoke();
        SettingsService.Save(vm.Settings);
    }

    private void BlackboardBottomMarginSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Vm is not { } vm)
            return;
        BlackboardBottomMarginValue.Text = $"{(int)e.NewValue}px";   // 滑块 TwoWay 绑定已写回 Settings
        BarLayoutChanged?.Invoke();
        SettingsService.Save(vm.Settings);
    }

    private void BlackboardOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Vm is not { } vm)
            return;
        vm.Settings.BlackboardBarOpacity = e.NewValue / 100.0;
        BlackboardOpacityValue.Text = $"{(int)e.NewValue}%";
        BarLayoutChanged?.Invoke();
        SettingsService.Save(vm.Settings);
    }

    // ────────── 墨迹识别（开关走全局真源；灵敏度滑块百分比 = 相似度门槛）──────────

    private void InkToShapeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        // 写 VM 即经既有 PropertyChanged 链传播：PDF 引擎 + 画板墨迹面板镜像（MainWindow.ApplyInkToShapeEnabled）。
        vm.InkTool.IsInkToShapeEnabled = vm.Settings.IsInkToShapeEnabled;
        SettingsService.Save(vm.Settings);
    }

    private void InkToShapeSensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Vm is not { } vm)
            return;
        int value = (int)e.NewValue;
        vm.Settings.InkToShapeSensitivity = value;
        InkToShapeSensitivityValue.Text = $"{value}%";
        InkToShapeSensitivityChanged?.Invoke(value);
        SettingsService.Save(vm.Settings);
    }

    // ────────── 停顿强制识别（独立开关；灵敏度 = 相似度门槛；时间/容错 = 输入框）──────────

    private void PauseInkToShapeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        SettingsService.Save(vm.Settings);
        PauseInkToShapeChanged?.Invoke();
    }

    private void PauseInkToShapeSensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Vm is not { } vm)
            return;
        int value = (int)e.NewValue;
        vm.Settings.PauseInkToShapeSensitivity = value;
        PauseInkToShapeSensitivityValue.Text = $"{value}%";
        SettingsService.Save(vm.Settings);
        PauseInkToShapeChanged?.Invoke();
    }

    /// <summary>停顿触发时间输入：解析并钳制 50~5000ms 后写回设置、存盘并通知重推画板。</summary>
    private void PauseInkToShapeDelayMsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        if (!int.TryParse(PauseInkToShapeDelayMsBox.Text.Trim(), out int ms))
            return;   // 中途输入（空串/负号等）不处理，保留上次有效值
        ms = Math.Clamp(ms, 50, 5000);
        if (vm.Settings.PauseInkToShapeDelayMs == ms)
            return;   // 回填或同值：不重复存盘
        vm.Settings.PauseInkToShapeDelayMs = ms;
        SettingsService.Save(vm.Settings);
        PauseInkToShapeChanged?.Invoke();
    }

    /// <summary>停顿容错值输入：解析并钳制 0~100px 后写回设置、存盘并通知重推画板。</summary>
    private void PauseInkToShapeToleranceBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        if (!int.TryParse(PauseInkToShapeToleranceBox.Text.Trim(), out int px))
            return;
        px = Math.Clamp(px, 0, 100);
        if (vm.Settings.PauseInkToShapeTolerancePx == px)
            return;
        vm.Settings.PauseInkToShapeTolerancePx = px;
        SettingsService.Save(vm.Settings);
        PauseInkToShapeChanged?.Invoke();
    }

    // ────────── 手掌擦除（开关即时生效：消费方实时读 Settings；橡皮菜单打开时回读同步）──────────

    /// <summary>手掌擦除开关：只联动笔尖模式卡片可用性（不强制关笔尖模式本身）并存盘。</summary>
    private void PalmEraseToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        NibModeToggle.IsEnabled = vm.Settings.IsPalmEraseEnabled;
        SettingsService.Save(vm.Settings);
    }

    /// <summary>笔尖模式开关：存盘即可（PDF/画板触摸判定实时读取）。</summary>
    private void NibModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        SettingsService.Save(vm.Settings);
    }

    /// <summary>手掌模式触摸阈值输入：解析并钳制 1~200px 后写回 FingerModeBoundsWidth 并存盘。</summary>
    private void PalmModeThresholdBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        if (!int.TryParse(PalmModeThresholdBox.Text.Trim(), out int px))
            return;   // 中途输入（空串/负号等）不处理，保留上次有效值
        px = Math.Clamp(px, 1, 200);
        if (vm.Settings.FingerModeBoundsWidth == px)
            return;   // 回填或同值：不重复存盘
        vm.Settings.FingerModeBoundsWidth = px;
        SettingsService.Save(vm.Settings);
    }

    /// <summary>笔尖模式触摸阈值输入：解析并钳制 1~200px 后写回 NibModeBoundsWidth 并存盘。</summary>
    private void NibModeThresholdBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        if (!int.TryParse(NibModeThresholdBox.Text.Trim(), out int px))
            return;
        px = Math.Clamp(px, 1, 200);
        if (vm.Settings.NibModeBoundsWidth == px)
            return;
        vm.Settings.NibModeBoundsWidth = px;
        SettingsService.Save(vm.Settings);
    }

    // ══ 数据存储 ══

    /// <summary>自动保存墨迹书写数据：关闭=立即 flush 当前库并停用（完全停用：不读不写，E1）；
    /// 开启=存盘即可，下次打开 PDF 生效（当前已打开文档不现场建库）。</summary>
    private void AutoSaveInkToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        SettingsService.Save(vm.Settings);
        if (!vm.Settings.IsAutoSaveInk)
            vm.StopInkPersistence();   // DocumentClosing → MainWindow.FlushInkStore → InkStore=null
    }

    /// <summary>自动删除超期墨迹数据开关：仅落盘（删除仅发生在下次启动，与"启动时删除"语义一致）。</summary>
    private void AutoCleanInkToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        SettingsService.Save(vm.Settings);
    }

    /// <summary>保留天数输入：解析并钳制 1~3650 天后写回并存盘（照 TimePenGrowMsBox 模板）。</summary>
    private void AutoCleanDaysBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        if (!int.TryParse(AutoCleanDaysBox.Text.Trim(), out int days))
            return;   // 中途输入（空串/负号等）不处理，保留上次有效值
        days = Math.Clamp(days, 1, 3650);
        if (vm.Settings.AutoCleanInkDays == days)
            return;   // 回填或同值：不重复存盘
        vm.Settings.AutoCleanInkDays = days;
        SettingsService.Save(vm.Settings);
    }

    // ═══════════ 应用更新（状态区 + 检查/下载/安装流程 + 更新日志）═══════════

    /// <summary>按 UpdateService 状态机 + 设置渲染状态区（图标/标题/副标题/按钮/进度条；状态表见计划）。
    /// forceAutoUpdateOff：关闭自动更新开关时强制渲染「已关闭自动更新」，立即反馈、不依赖服务态。</summary>
    private void ApplyUpdateState(bool forceAutoUpdateOff = false)
    {
        if (Vm is not { } vm)
            return;
        var settings = vm.Settings;
        var state = forceAutoUpdateOff ? UpdateState.AutoUpdateOff : UpdateService.State;
        // 服务只产出 UpToDate；自动检查关闭且无其它进展 → 已关闭自动更新态
        if (state == UpdateState.UpToDate && !settings.IsAutoCheckUpdate)
            state = UpdateState.AutoUpdateOff;

        var checkTime = string.IsNullOrEmpty(settings.LastUpdateCheckTime)
            ? "上次检查时间： 尚未检查"
            : $"上次检查时间： {settings.LastUpdateCheckTime}";

        FIcon icon;
        string title, subtitle, button;
        bool buttonEnabled = true;
        bool barVisible = false, barIndeterminate = false, barError = false;

        switch (state)
        {
            case UpdateState.Checking:
                icon = FIcon.ApprovalsApp;
                title = "正在检查最新版本";
                subtitle = checkTime;
                button = "检查更新";
                buttonEnabled = false;
                barVisible = true;
                barIndeterminate = true;
                break;
            case UpdateState.Downloading:
                icon = FIcon.ArrowDownload;
                title = "正在下载最新版本";
                subtitle = $"最新版本： v{UpdateService.PendingVersion}";
                button = "检查更新";
                buttonEnabled = false;
                barVisible = true;
                break;
            case UpdateState.Error:
                icon = FIcon.ErrorCircle;
                title = "更新发生错误";
                subtitle = string.IsNullOrEmpty(UpdateService.ErrorMessage) ? "未知错误" : UpdateService.ErrorMessage;
                button = "检查更新";
                barVisible = true;
                barIndeterminate = true;   // 画廊写法：IsIndeterminate + ShowError
                barError = true;
                break;
            case UpdateState.AutoUpdateOff:
                icon = FIcon.Pause;
                title = "已关闭自动更新";
                subtitle = "您可以点击右侧按钮手动更新";
                button = "检查更新";
                break;   // 该态不显示进度条
            case UpdateState.ReadyToInstall:
                icon = FIcon.ArrowClockwise;
                title = "已下载最新版本";
                subtitle = "点击右侧按钮立即更新";
                button = "立即更新";
                break;
            default:   // UpToDate
                icon = FIcon.ArrowSync;
                title = "您已更新到最新版本";
                subtitle = checkTime;
                button = "检查更新";
                break;
        }

        UpdateStateIcon.Icon = icon;
        UpdateStateTitle.Text = title;
        UpdateStateSubtitle.Text = subtitle;
        UpdateStateButton.Content = button;
        UpdateStateButton.IsEnabled = buttonEnabled;   // 强调样式固定在 XAML，各态仅变文字/灰置

        UpdateProgressBar.Visibility = barVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressBar.IsIndeterminate = barIndeterminate;
        UpdateProgressBar.ShowError = barError;
        if (barVisible && !barIndeterminate)
            UpdateProgressBar.Value = Math.Clamp(UpdateService.DownloadProgress, 0, 100);
    }

    /// <summary>状态区按钮：ReadyToInstall 态 = 立即更新（/restart 可见安装 + 退出应用）；其余 = 手动检查并下载。</summary>
    private async void UpdateCheck_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;

        if (UpdateService.State == UpdateState.ReadyToInstall)
        {
            try
            {
                UpdateService.StartInstall(UpdateService.GetSetupPath(), restart: true);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                App.Log("Update.StartInstall", ex);
                ShowFloatingTipFromOwner("更新失败：" + ex.Message, InfoBarSeverity.Error);
            }
            return;
        }

        await UpdateService.RunCheckAndDownloadAsync(vm.Settings, manual: true, ShowFloatingTipFromOwner);
    }

    /// <summary>启用自动检查更新：显式回写开关值（不依赖绑定先于事件的时序）+ 存盘；
    /// 关闭时强制立即渲染「已关闭自动更新」，开启时按服务态渲染。</summary>
    private void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        vm.Settings.IsAutoCheckUpdate = AutoUpdateToggle.IsOn;
        SettingsService.Save(vm.Settings);
        ApplyUpdateState(forceAutoUpdateOff: !AutoUpdateToggle.IsOn);
    }

    /// <summary>更新通道切换：写回设置、使已下载待装包失效、存盘并重渲染（同值回填不处理）。</summary>
    private void UpdateChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { } vm || UpdateChannelCombo.SelectedIndex < 0)
            return;
        if (vm.Settings.UpdateChannel == UpdateChannelCombo.SelectedIndex)
            return;   // 回填或同值：不重复存盘
        vm.Settings.UpdateChannel = UpdateChannelCombo.SelectedIndex;
        UpdateService.InvalidatePendingInstall(vm.Settings);
        SettingsService.Save(vm.Settings);
        ApplyUpdateState();
    }

    /// <summary>更新代理修改：写回设置（前缀拼接在服务侧）、使待装包失效、存盘（同值回填不处理）。</summary>
    private void UpdateProxyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        var proxy = UpdateProxyBox.Text.Trim();
        if (vm.Settings.UpdateProxy == proxy)
            return;
        vm.Settings.UpdateProxy = proxy;
        UpdateService.InvalidatePendingInstall(vm.Settings);
        SettingsService.Save(vm.Settings);
        ApplyUpdateState();
    }

    /// <summary>查看更新日志：优先取已下载新版本的 Release 正文，否则取当前通道最新 Release；iuwm ContentDialog 展示。</summary>
    private async void ViewChangelog_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;

        var body = new TextBlock
        {
            Text = "正在获取更新日志…",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15,
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "ToolbarForegroundBrush");
        var dialog = new ContentDialog
        {
            Title = "更新日志",
            Content = new ScrollViewer
            {
                Content = body,
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            CloseButtonText = "关闭",
            Owner = Window.GetWindow(this) ?? Application.Current.MainWindow,
        };
        // 关闭按钮用强调样式（与「查看更新日志」卡片按钮一致）
        dialog.SetResourceReference(ContentDialog.CloseButtonStyleProperty,
            iNKORE.UI.WPF.Modern.ThemeKeys.AccentButtonStyleKey);
        _ = dialog.ShowAsync();   // 先展示加载态，再异步回填正文

        try
        {
            var notes = UpdateService.HasPendingInstall(vm.Settings)
                ? await UpdateService.GetReleaseNotesAsync(vm.Settings, vm.Settings.DownloadedUpdateVersion)
                : await UpdateService.GetLatestReleaseNotesAsync(vm.Settings);
            body.Text = notes == null || string.IsNullOrWhiteSpace(notes)
                ? "当前版本只有滚木日志😁"
                : notes.Trim();
        }
        catch (Exception ex)
        {
            App.Log("Update.GetReleaseNotes", ex);
            body.Text = "获取更新日志失败：可能无法连接到 GitHub。";
        }
    }
}