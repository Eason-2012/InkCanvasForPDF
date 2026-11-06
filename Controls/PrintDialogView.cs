using System;
using System.Collections.Generic;
using System.Linq;
using System.Printing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using iNKORE.UI.WPF.Modern.Controls;
using InkCanvasForPDF.Services;
using FIcon = FluentIcons.Common.Icon;

namespace InkCanvasForPDF.Controls;

/// <summary>打印对话框内容视图（宿主为主窗口内 iUWM ContentDialog）：
/// 左侧设置 + 右侧预览，后台流式渲染每页（PDF + 墨迹，含页面旋转）。</summary>
public sealed class PrintDialogView : UserControl
{
    public event Action<bool>? PrintEnabledChanged;

    /// <summary>成功发送到打印机的页数（0 = 未打印或失败）。</summary>
    public int PrintedPageCount { get; private set; }

    public bool IsPrinting => _printing;

    private readonly PrintDocumentSource _source;
    private readonly BitmapSource?[] _pages;
    private readonly int[] _rasterW;
    private readonly int[] _rasterH;
    private readonly double[] _pageDipW;
    private readonly double[] _pageDipH;
    private readonly int _previewBox;

    private int _loadedCount;
    private bool _isLoading = true;
    private bool _previewFailed;
    private bool _previewStarted;
    private volatile bool _canceled;
    private bool _printing;

    private LocalPrintServer? _server;
    private readonly List<PrintQueue> _queues = new();
    private PrintQueue? _queue;
    private bool _landscape;
    private int _previewIndex;
    private int _alignH = 1;
    private int _alignV = 1;
    private int _scaleMode;
    private double _customPct = 100;
    private double _marginPx;
    private int _nUp = 1;
    private bool _duplex;
    private bool _grayscale;
    private double _areaW = 816;
    private double _areaH = 1056;

    private ComboBox _printerCombo = null!;
    private ComboBox _paperCombo = null!;
    private ComboBox _sourceCombo = null!;
    private NumberBox _copiesBox = null!;
    private NumberBox _scaleBox = null!;
    private TextBox _pagesBox = null!;
    private ComboBox _subsetCombo = null!;
    private ToggleSwitch _duplexCheck = null!;
    private Border _scaleRow = null!;
    private TranslateTransform _scaleSlide = null!;
    private readonly List<PageMediaSize> _paperSizes = new();
    private PageMediaSize? _paperOverride;
    private readonly List<InputBin> _sourceBins = new();
    private InputBin? _sourceOverride;

    private readonly Grid _previewHost = new();
    private readonly TextBlock _pageLabel = new();
    private readonly TextBlock _renderLabel = new();
    private Button _previousPage = null!;
    private Button _nextPage = null!;
    private Grid _previewOuter = null!;       // 预览列外层（占满 body 第二列）
    private DockPanel _previewInner = null!;  // 导航 + 预览框，宽度被约束并居中
    private Border _previewFrame = null!;
    private Grid _rootGrid = null!;
    private InfoBar _errorBar = null!;
    private Border? _printOverlay;
    private TextBlock? _printStatus;

    private const double PreviewColumnMaxWidth = 1000;  // 预览列硬上限（DIP）
    private const double PreviewViewboxMargin = 20;    // 与 Viewbox 的 Margin 保持一致
    private const double PreviewNavHeight = 42;        // 与 navigation.Height 保持一致

    // 进程内打印偏好（不写 Settings.json，符合“禁止碰设置文件”约束）。
    private static string? _lastPrinter;
    private static bool _lastLandscape;
    private static bool _lastGrayscale;
    private static bool _lastDuplex;

    public PrintDialogView(PrintDocumentSource source)
    {
        _source = source;
        int count = source.Pages.Count;
        _pages = new BitmapSource?[count];
        _rasterW = new int[count];
        _rasterH = new int[count];
        _pageDipW = new double[count];
        _pageDipH = new double[count];
        for (int i = 0; i < count; i++)
        {
            _pageDipW[i] = source.Pages[i].DisplayDipW;
            _pageDipH[i] = source.Pages[i].DisplayDipH;
        }
        _previewBox = count <= 80 ? 1536 : count <= 250 ? 1100 : 800;

        _landscape = _lastLandscape;
        _grayscale = _lastGrayscale;
        _duplex = _lastDuplex;

        BuildUi();
        LoadPrinters();
        UpdateDuplexAvailability();
        RefreshArea();
        UpdatePreview();

        Loaded += (_, _) =>
        {
            StartPreviewRendering();
            UpdatePreviewColumnWidth();
        };
    }

    /// <summary>关闭对话框时调用：停止后台预览渲染并释放打印服务器。</summary>
    public void Cancel()
    {
        _canceled = true;
        try { _server?.Dispose(); } catch { }
        _server = null;
    }

    // ────────────────────────── UI 构建 ──────────────────────────

    private void BuildUi()
    {
        _rootGrid = new Grid { MinWidth = 800, MinHeight = 380 };
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(BuildSettingsColumn());
        body.Children.Add(BuildPreviewColumn());
        _rootGrid.Children.Add(body);
        Content = _rootGrid;
    }

    private FrameworkElement BuildSettingsColumn()
    {
        var panel = new StackPanel { Margin = new Thickness(16, 8, 12, 12) };

        _errorBar = new InfoBar
        {
            IsOpen = false,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Message = "",
            Margin = new Thickness(0, 0, 0, 8),
        };
        panel.Children.Add(_errorBar);

        panel.Children.Add(Label("打印机"));
        _printerCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 12) };
        _printerCombo.SelectionChanged += PrinterCombo_SelectionChanged;
        panel.Children.Add(_printerCombo);

        panel.Children.Add(Label("纸张大小"));
        _paperCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 12) };
        _paperCombo.SelectionChanged += PaperCombo_SelectionChanged;
        panel.Children.Add(_paperCombo);

        panel.Children.Add(Label("纸张来源"));
        _sourceCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 12) };
        _sourceCombo.SelectionChanged += SourceCombo_SelectionChanged;
        panel.Children.Add(_sourceCombo);

        panel.Children.Add(Label("方向"));
        var orientationCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 12) };
        orientationCombo.Items.Add("纵向");
        orientationCombo.Items.Add("横向");
        orientationCombo.SelectedIndex = _landscape ? 1 : 0;
        orientationCombo.SelectionChanged += (_, _) =>
        {
            _landscape = orientationCombo.SelectedIndex == 1;
            RefreshArea();
            UpdatePreview();
        };
        panel.Children.Add(orientationCombo);

        panel.Children.Add(Label("位置"));
        var positionCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 12) };
        var positions = new (string Key, int H, int V)[]
        {
            ("居中", 1, 1), ("顶部", 1, 0), ("底部", 1, 2),
            ("居左", 0, 1), ("居右", 2, 1),
            ("左上", 0, 0), ("右上", 2, 0),
            ("左下", 0, 2), ("右下", 2, 2)
        };
        foreach (var item in positions) positionCombo.Items.Add(item.Key);
        positionCombo.SelectedIndex = 0;
        positionCombo.SelectionChanged += (_, _) =>
        {
            int i = positionCombo.SelectedIndex;
            if (i < 0 || i >= positions.Length) return;
            _alignH = positions[i].H;
            _alignV = positions[i].V;
            UpdatePreview();
        };
        panel.Children.Add(positionCombo);

        panel.Children.Add(Label("边距"));
        var marginCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 12) };
        var margins = new (string Name, double Inches)[]
        {
            ("无", 0), ("窄 (0.25\")", 0.25), ("普通 (0.5\")", 0.5), ("宽 (1\")", 1.0)
        };
        foreach (var item in margins) marginCombo.Items.Add(item.Name);
        marginCombo.SelectedIndex = 0;
        marginCombo.SelectionChanged += (_, _) =>
        {
            int i = marginCombo.SelectedIndex;
            if (i < 0 || i >= margins.Length) return;
            _marginPx = margins[i].Inches * 96.0;
            UpdatePreview();
        };
        panel.Children.Add(marginCombo);

        panel.Children.Add(Label("每张页数"));
        var nupCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 12) };
        foreach (var n in new[] { "1", "2", "4", "6", "9" }) nupCombo.Items.Add(n);
        nupCombo.SelectedIndex = 0;
        nupCombo.SelectionChanged += (_, _) =>
        {
            _nUp = int.TryParse(nupCombo.SelectedItem as string, out int n) && n > 0 ? n : 1;
            _previewIndex = 0;
            UpdatePreview();
        };
        panel.Children.Add(nupCombo);

        panel.Children.Add(Label("缩放"));
        var scaleCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 6) };
        scaleCombo.Items.Add("适应页面");
        scaleCombo.Items.Add("实际大小 (100%)");
        scaleCombo.Items.Add("自定义 %");
        scaleCombo.SelectedIndex = 0;

        _scaleBox = new NumberBox
        {
            Header = "自定义缩放 (%)",
            Minimum = 1,
            Maximum = 1000,
            Value = 100,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Margin = new Thickness(0, 0, 0, 4),
        };
        _scaleBox.ValueChanged += (_, e) =>
        {
            _customPct = e.NewValue;
            if (_scaleMode == 2) UpdatePreview();
        };

        _scaleSlide = new TranslateTransform();
        _scaleRow = new Border
        {
            Visibility = Visibility.Collapsed,
            RenderTransform = _scaleSlide,
            Margin = new Thickness(0, 0, 0, 12),
            Child = _scaleBox,
        };
        panel.Children.Add(_scaleRow);

        scaleCombo.SelectionChanged += (_, _) =>
        {
            _scaleMode = scaleCombo.SelectedIndex;
            if (_scaleMode == 1)
            {
                _customPct = 100;
                _scaleBox.Value = 100;
            }
            if (_scaleMode == 2)
            {
                _scaleRow.Visibility = Visibility.Visible;
                _scaleRow.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(140))));
                _scaleSlide.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(-8, 0, new Duration(TimeSpan.FromMilliseconds(140)))
                    { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
                _scaleBox.Focus();
            }
            else
            {
                _scaleRow.Visibility = Visibility.Collapsed;
            }
            UpdatePreview();
        };
        panel.Children.Add(scaleCombo);

        panel.Children.Add(Label("颜色"));
        var colorCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 12) };
        colorCombo.Items.Add("彩色");
        colorCombo.Items.Add("黑白");
        colorCombo.SelectedIndex = _grayscale ? 1 : 0;
        colorCombo.SelectionChanged += (_, _) =>
        {
            _grayscale = colorCombo.SelectedIndex == 1;
            UpdatePreview();
        };
        panel.Children.Add(colorCombo);

        _copiesBox = new NumberBox
        {
            Header = "份数",
            Minimum = 1,
            Maximum = 9999,
            Value = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Margin = new Thickness(0, 0, 0, 12),
        };
        panel.Children.Add(_copiesBox);

        panel.Children.Add(Label("页面"));
        _pagesBox = new TextBox { Margin = new Thickness(0, 4, 0, 2) };
        _pagesBox.TextChanged += (_, _) =>
        {
            _previewIndex = 0;
            UpdatePreview();
        };
        panel.Children.Add(_pagesBox);
        panel.Children.Add(new TextBlock
        {
            Text = "例如 1-3,5（空白 = 全部）",
            Foreground = R("SecondaryForegroundBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        _subsetCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 12) };
        _subsetCombo.Items.Add("所有页面");
        _subsetCombo.Items.Add("仅奇数页");
        _subsetCombo.Items.Add("仅偶数页");
        _subsetCombo.SelectedIndex = 0;
        _subsetCombo.SelectionChanged += (_, _) =>
        {
            _previewIndex = 0;
            UpdatePreview();
        };
        panel.Children.Add(_subsetCombo);

        _duplexCheck = new ToggleSwitch
        {
            Header = "双面打印",
            IsOn = _duplex,
            Margin = new Thickness(0, 0, 0, 12),
        };
        _duplexCheck.Toggled += (_, _) => _duplex = _duplexCheck.IsOn;
        panel.Children.Add(_duplexCheck);

        var scroller = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetColumn(scroller, 0);
        return scroller;
    }

    private FrameworkElement BuildPreviewColumn()
    {
        _previewFrame = new Border
        {
            Background = R("AppBackgroundBrush"),
            BorderBrush = R("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(8, 4, 8, 12),
            CornerRadius = new CornerRadius(8),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 12, ShadowDepth = 2,
                Direction = 270, Opacity = 0.18
            },
        };
        var frame = _previewFrame;

        var grid = new Grid();
        grid.Children.Add(_previewHost);
        frame.Child = grid;

        var navigation = new Grid { Height = PreviewNavHeight, Margin = new Thickness(8, 0, 8, 8) };
        navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        navigation.ColumnDefinitions.Add(new ColumnDefinition());
        navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 与主窗口 PDF 页面工具栏同款（IconButton 定义在 MainWindow.Window.Resources）
        var iconButtonStyle = Application.Current.MainWindow?.TryFindResource("IconButton") as Style;

        _previousPage = new Button
        {
            Style = iconButtonStyle,
            ToolTip = "上一页",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0),   // 工具栏第一个按钮同款：覆盖样式的左 8px 边距
            Content = new Grid
            {
                Width = 32,
                Height = 32,
                Children =
                {
                    new FluentIcons.Wpf.FluentIcon
                    {
                        Icon = FIcon.ChevronLeft,
                        IconVariant = FluentIcons.Common.IconVariant.Regular,
                        FontSize = 24,
                        Foreground = R("ToolbarForegroundBrush"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };
        _previousPage.Click += (_, _) =>
        {
            if (_previewIndex > 0) { _previewIndex--; UpdatePreview(); }
        };
        Grid.SetColumn(_previousPage, 0);
        navigation.Children.Add(_previousPage);

        _nextPage = new Button
        {
            Style = iconButtonStyle,
            ToolTip = "下一页",
            HorizontalAlignment = HorizontalAlignment.Right,
            Content = new Grid
            {
                Width = 32,
                Height = 32,
                Children =
                {
                    new FluentIcons.Wpf.FluentIcon
                    {
                        Icon = FIcon.ChevronRight,
                        IconVariant = FluentIcons.Common.IconVariant.Regular,
                        FontSize = 24,
                        Foreground = R("ToolbarForegroundBrush"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };
        _nextPage.Click += (_, _) =>
        {
            if (_previewIndex < SheetCount() - 1) { _previewIndex++; UpdatePreview(); }
        };
        Grid.SetColumn(_nextPage, 2);
        navigation.Children.Add(_nextPage);

        _renderLabel.Foreground = R("SecondaryForegroundBrush");
        _renderLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _renderLabel.FontSize = 11;
        _renderLabel.Visibility = Visibility.Collapsed;

        _pageLabel.Foreground = R("ToolbarForegroundBrush");
        _pageLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _pageLabel.FontSize = 12;

        var navCenter = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        navCenter.Children.Add(_renderLabel);
        navCenter.Children.Add(_pageLabel);
        Grid.SetColumn(navCenter, 1);
        navigation.Children.Add(navCenter);

        _previewOuter = new Grid();
        _previewInner = new DockPanel { HorizontalAlignment = HorizontalAlignment.Center };
        DockPanel.SetDock(navigation, Dock.Bottom);
        _previewInner.Children.Add(navigation);
        _previewInner.Children.Add(frame);
        _previewOuter.Children.Add(_previewInner);
        _previewOuter.SizeChanged += (_, _) => UpdatePreviewColumnWidth();
        Grid.SetColumn(_previewOuter, 1);
        return _previewOuter;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = R("ToolbarForegroundBrush"),
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
    };

    // ────────────────────────── 打印机 / 纸张 ──────────────────────────

    private void LoadPrinters()
    {
        var set = PrintService.LoadPrintQueues();
        _server = set.Server;
        _queues.Clear();
        _queues.AddRange(set.Queues);

        foreach (var queue in _queues)
            _printerCombo.Items.Add(queue.FullName);

        int selected = !string.IsNullOrEmpty(_lastPrinter)
            ? _queues.FindIndex(q => q.FullName == _lastPrinter)
            : -1;
        if (selected < 0)
            selected = set.DefaultQueue != null ? _queues.FindIndex(q => q.FullName == set.DefaultQueue.FullName) : -1;
        if (_queues.Count > 0)
        {
            _printerCombo.SelectedIndex = selected >= 0 ? selected : 0;
            _queue = _queues[_printerCombo.SelectedIndex];
        }
        else
        {
            ShowError("未检测到可用的打印机。", InfoBarSeverity.Warning);
        }
    }

    private void PrinterCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        int index = _printerCombo.SelectedIndex;
        if (index < 0 || index >= _queues.Count) return;
        _queue = _queues[index];
        PopulatePaperSizes();
        PopulatePaperSources();
        RefreshArea();
        UpdateDuplexAvailability();
        UpdatePreview();
    }

    private void PopulatePaperSizes()
    {
        _paperSizes.Clear();
        _paperCombo.Items.Clear();
        _paperCombo.Items.Add("与文件一致");
        foreach (var media in PrintService.GetPaperSizes(_queue))
        {
            _paperSizes.Add(media);
            _paperCombo.Items.Add(PrintService.PaperDisplayName(media));
        }
        _paperCombo.SelectedIndex = 0;
        _paperOverride = null;
    }

    private void PopulatePaperSources()
    {
        _sourceBins.Clear();
        _sourceCombo.Items.Clear();
        _sourceCombo.Items.Add("打印机默认");
        foreach (var bin in PrintService.GetInputBins(_queue))
        {
            _sourceBins.Add(bin);
            _sourceCombo.Items.Add(PrintService.InputBinDisplayName(bin));
        }
        _sourceCombo.SelectedIndex = 0;
        _sourceOverride = null;
    }

    private void PaperCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        int index = _paperCombo.SelectedIndex;
        _paperOverride = index > 0 && index - 1 < _paperSizes.Count ? _paperSizes[index - 1] : null;
        RefreshArea();
        UpdatePreview();
    }

    private void SourceCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        int index = _sourceCombo.SelectedIndex;
        _sourceOverride = index > 0 && index - 1 < _sourceBins.Count ? _sourceBins[index - 1] : null;
    }

    private void UpdateDuplexAvailability()
    {
        bool supported = false;
        try
        {
            var caps = _queue?.GetPrintCapabilities();
            supported = caps?.DuplexingCapability?.Contains(Duplexing.TwoSidedLongEdge) == true;
        }
        catch { /* 驱动不支持能力查询 */ }

        _duplexCheck.IsEnabled = supported;
        if (!supported)
        {
            _duplexCheck.IsOn = false;
            _duplex = false;
        }
        _duplexCheck.Opacity = supported ? 1.0 : 0.4;
        _duplexCheck.ToolTip = supported ? null : "此打印机不支持双面打印。";
    }

    private void RefreshArea()
    {
        double firstW = _source.Pages.Count > 0 ? _source.Pages[0].DisplayDipW : 816;
        double firstH = _source.Pages.Count > 0 ? _source.Pages[0].DisplayDipH : 1056;
        (_areaW, _areaH) = PrintService.PrintableArea(
            _queue, _landscape, _paperOverride, firstW, firstH);
        UpdatePreviewColumnWidth();
    }

    /// <summary>预览列宽度 = 当前纸张按可用高度等比缩放后的显示宽度，并夹在 [保底宽度, 1000 DIP] 内。
    /// 只跟随纸张版面比例（_areaW/_areaH），不读取页面位图宽度。</summary>
    private void UpdatePreviewColumnWidth()
    {
        if (_areaW <= 0 || _areaH <= 0) return;
        if (_previewInner is null || _previewFrame is null) return;
        if (_previewInner.ActualHeight <= 0 || _previewOuter.ActualWidth <= 0) return;

        // 纸张在预览框内的可用高度：去掉导航、预览框外边距/边框、Viewbox 上下 margin
        double paperH = _previewInner.ActualHeight
                        - PreviewNavHeight
                        - _previewFrame.Margin.Top - _previewFrame.Margin.Bottom
                        - _previewFrame.BorderThickness.Top - _previewFrame.BorderThickness.Bottom
                        - 2 * PreviewViewboxMargin;
        if (paperH <= 0) return;

        double desiredW = paperH * (_areaW / _areaH)
                          + _previewFrame.Margin.Left + _previewFrame.Margin.Right
                          + _previewFrame.BorderThickness.Left + _previewFrame.BorderThickness.Right
                          + 2 * PreviewViewboxMargin;

        double availableW = _previewOuter.ActualWidth;
        double target = Math.Min(PreviewColumnMaxWidth, Math.Min(availableW, desiredW));
        target = Math.Max(target, Math.Min(240, availableW));   // 小窗口下保底可读

        if (Math.Abs(_previewInner.Width - target) > 0.5)
            _previewInner.Width = target;
    }

    // ────────────────────────── 预览 ──────────────────────────

    private void StartPreviewRendering()
    {
        if (_previewStarted) return;
        _previewStarted = true;
        _ = Task.Run(() =>
        {
            try
            {
                int total = _source.Pages.Count;
                for (int i = 0; i < total; i++)
                {
                    if (_canceled) return;
                    var info = _source.Pages[i];
                    double scale = _previewBox / Math.Max(1.0,
                        Math.Max(info.ContentDipW, info.ContentDipH));
                    int contentW = Math.Max(1, (int)Math.Round(info.ContentDipW * scale));
                    int contentH = Math.Max(1, (int)Math.Round(info.ContentDipH * scale));

                    var pageBitmap = (BitmapSource)_source.Pdf.RenderPage(
                        info.Index, contentW, contentW, contentH, 0);
                    var composite = PrintService.CompositePageWithInk(
                        pageBitmap, info.Model.Strokes,
                        info.ContentDipW, info.ContentDipH,
                        info.Model.ContentWidth, info.Model.ContentHeight, info.Rotation);

                    int index = i;
                    try
                    {
                        Dispatcher.Invoke(() => SetRenderedPage(
                            index, composite, composite.PixelWidth, composite.PixelHeight));
                    }
                    catch { return; }   // 窗口已关闭
                }

                if (!_canceled)
                {
                    try { Dispatcher.Invoke(FinishLoading); } catch { }
                }
            }
            catch (Exception ex)
            {
                try { Dispatcher.Invoke(() => LoadFailed(ex.Message)); } catch { }
            }
        });
    }

    private void SetRenderedPage(int index, BitmapSource source, int width, int height)
    {
        if (index < 0 || index >= _pages.Length) return;
        _pages[index] = source;
        _rasterW[index] = width;
        _rasterH[index] = height;
        _loadedCount++;

        int first = _previewIndex * _nUp;
        bool onCurrentSheet = index >= first && index < first + _nUp;
        if (onCurrentSheet)
            UpdatePreview();
        else if (_isLoading)
            UpdateRenderLabel();
    }

    private void FinishLoading()
    {
        _isLoading = false;
        RaisePrintEnabled();
        UpdatePreview();
    }

    private void LoadFailed(string message)
    {
        _isLoading = false;
        _previewFailed = true;
        _previewHost.Children.Clear();
        _previewHost.Children.Add(new TextBlock
        {
            Text = $"预览渲染失败：{message}",
            Foreground = R("SecondaryForegroundBrush"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
        });
        ShowError($"预览渲染失败：{message}", InfoBarSeverity.Error);
        RaisePrintEnabled();
    }

    private void UpdatePreview()
    {
        _previewHost.Children.Clear();
        if (_pages.Length == 0)
        {
            _pageLabel.Text = "无页面";
            _renderLabel.Visibility = Visibility.Collapsed;
            _previousPage.IsEnabled = false;
            _nextPage.IsEnabled = false;
            RaisePrintEnabled();
            return;
        }

        var selected = SelectedIndices();
        if (selected.Count == 0)
        {
            _pageLabel.Text = "";
            UpdateRenderLabel();
            _previewHost.Children.Add(new TextBlock
            {
                Text = "没有可打印的页面",
                Foreground = R("SecondaryForegroundBrush"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24),
            });
            _previousPage.IsEnabled = false;
            _nextPage.IsEnabled = false;
            RaisePrintEnabled();
            return;
        }

        int sheets = Math.Max(1, (selected.Count + _nUp - 1) / _nUp);
        int sheet = Math.Max(0, Math.Min(_previewIndex, sheets - 1));
        _previewIndex = sheet;
        _previousPage.IsEnabled = sheet > 0;
        _nextPage.IsEnabled = sheet + 1 < sheets;

        var indices = new List<int>();
        for (int i = sheet * _nUp; i < Math.Min(selected.Count, sheet * _nUp + _nUp); i++)
            indices.Add(selected[i]);

        _pageLabel.Text = _nUp > 1
            ? $"第 {sheet + 1} 张 / 共 {sheets} 张"
            : $"第 {(indices.Count > 0 ? indices[0] + 1 : 1)} / {_pages.Length} 页";
        UpdateRenderLabel();

        if (indices.Any(i => _pages[i] is null))
        {
            _previewHost.Children.Add(BuildLoadingIndicator());
            RaisePrintEnabled();
            return;
        }

        var paper = PrintService.ComposeSheet(indices, _areaW, _areaH,
            _pages, _rasterW, _rasterH, _pageDipW, _pageDipH, CurrentLayout());
        _previewHost.Children.Add(new Viewbox
        {
            Child = paper,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(PreviewViewboxMargin),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3,
                Direction = 270, Opacity = 0.5
            },
        });
        RaisePrintEnabled();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePreviewColumnWidth));
    }

    private void UpdateRenderLabel()
    {
        if (_isLoading)
        {
            _renderLabel.Text = $"正在渲染 {_loadedCount} / {_pages.Length}";
            _renderLabel.Visibility = Visibility.Visible;
        }
        else
        {
            _renderLabel.Visibility = Visibility.Collapsed;
        }
    }

    private StackPanel BuildLoadingIndicator()
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 40,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"正在渲染 {_loadedCount} / {_pages.Length}",
            Foreground = R("SecondaryForegroundBrush"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        });
        return panel;
    }

    private List<int> SelectedIndices()
    {
        var list = PrintService.ParseRange(_pagesBox.Text, _pages.Length);
        int subset = _subsetCombo?.SelectedIndex ?? 0;
        if (subset != 0)
        {
            var filtered = new List<int>(list.Count);
            foreach (var i in list)
                if ((i % 2 == 0) == (subset == 1)) filtered.Add(i);
            list = filtered;
        }
        return list;
    }

    private int SheetCount()
        => _pages.Length == 0 ? 0 : (SelectedIndices().Count + _nUp - 1) / _nUp;

    private PrintLayout CurrentLayout() => new(
        _landscape, _alignH, _alignV, _scaleMode, _customPct,
        _marginPx, _nUp, _duplex, _grayscale);

    private bool ComputeCanPrint()
        => !_isLoading && !_printing && !_previewFailed
           && _queue != null && SelectedIndices().Count > 0;

    private void RaisePrintEnabled() => PrintEnabledChanged?.Invoke(ComputeCanPrint());

    private void ShowError(string message, InfoBarSeverity severity)
    {
        _errorBar.Message = message;
        _errorBar.Severity = severity;
        _errorBar.IsOpen = true;
    }

    // ────────────────────────── 打印 ──────────────────────────

    /// <summary>执行打印（由 ContentDialog 的「打印」按钮调用）。成功返回 true，失败留在对话框内提示。</summary>
    public async Task<bool> RunPrintAsync()
    {
        if (_printing) return false;
        if (_queue is null)
        {
            ShowError("未检测到可用的打印机，无法打印。", InfoBarSeverity.Warning);
            return false;
        }

        var indices = SelectedIndices();
        if (indices.Count == 0)
        {
            ShowError("页面范围无效，没有可打印的页面。", InfoBarSeverity.Warning);
            return false;
        }

        int copies = (int)Math.Round(_copiesBox.Value);
        if (copies < 1) copies = 1;

        ShowPrintOverlay(out TextBlock status);
        _printStatus = status;
        _printing = true;
        bool succeeded = false;
        RaisePrintEnabled();

        try
        {
            // 先让遮罩完成一帧绘制，再查询驱动/渲染，避免点击后的卡顿观感。
            await Dispatcher.Yield(DispatcherPriority.Background);
            SaveSessionPrefs();

            var pd = new System.Windows.Controls.PrintDialog { PrintQueue = _queue };
            var ticket = pd.PrintTicket;
            // 份数由页序列复制实现，票据自身只请求一份（部分驱动会把 CopyCount 多打一份）。
            ticket.CopyCount = 1;
            ticket.PageOrientation = _landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
            ticket.Duplexing = _duplex ? Duplexing.TwoSidedLongEdge : Duplexing.OneSided;
            ticket.OutputColor = _grayscale ? OutputColor.Grayscale : OutputColor.Color;
            var docMedia = _paperOverride ?? PrintService.MediaSizeForDocument(
                _queue,
                _source.Pages.Count > 0 ? _source.Pages[0].DisplayDipW : 816,
                _source.Pages.Count > 0 ? _source.Pages[0].DisplayDipH : 1056);
            if (docMedia != null) ticket.PageMediaSize = docMedia;
            if (_sourceOverride is { } bin) ticket.InputBin = bin;
            pd.PrintTicket = ticket;

            double areaW = pd.PrintableAreaWidth, areaH = pd.PrintableAreaHeight;
            if (_landscape) { if (areaW < areaH) (areaW, areaH) = (areaH, areaW); }
            else { if (areaW > areaH) (areaW, areaH) = (areaH, areaW); }
            if (areaW <= 0 || areaH <= 0) { areaW = _areaW; areaH = _areaH; }

            var layout = CurrentLayout();
            var hiPages = new BitmapSource?[_pages.Length];
            var hiW = new int[_pages.Length];
            var hiH = new int[_pages.Length];
            int total = indices.Count;

            await Task.Run(() =>
            {
                int done = 0;
                foreach (int index in indices)
                {
                    done++;
                    if (index < 0 || index >= _source.Pages.Count) continue;
                    var info = _source.Pages[index];
                    // 打印按真实 300 DPI 重栅格化，与预览的低预算位图无关。
                    int contentW = Math.Max(1, (int)Math.Round(info.ContentDipW * 300.0 / 96.0));
                    int contentH = Math.Max(1, (int)Math.Round(info.ContentDipH * 300.0 / 96.0));
                    var pageBitmap = (BitmapSource)_source.Pdf.RenderPage(
                        info.Index, contentW, contentW, contentH, 0);
                    var composite = PrintService.CompositePageWithInk(
                        pageBitmap, info.Model.Strokes,
                        info.ContentDipW, info.ContentDipH,
                        info.Model.ContentWidth, info.Model.ContentHeight, info.Rotation);
                    hiPages[index] = composite;
                    hiW[index] = composite.PixelWidth;
                    hiH[index] = composite.PixelHeight;

                    int shown = done;
                    try
                    {
                        _printStatus!.Dispatcher.Invoke(() =>
                            _printStatus.Text = $"正在准备第 {shown} / {total} 页");
                    }
                    catch { /* 对话框已关闭 */ }
                }
            });

            _printStatus!.Text = "正在发送到打印机…";
            bool ok = await PrintService.SpoolAsync(
                indices, copies, areaW, areaH, hiPages, hiW, hiH,
                _pageDipW, _pageDipH, ticket, _queue.FullName, layout);

            if (ok)
            {
                PrintedPageCount = indices.Count;
                succeeded = true;
                return true;
            }

            ShowError("打印任务已被取消。", InfoBarSeverity.Warning);
            return false;
        }
        catch (Exception ex)
        {
            App.Log("PrintPdf", ex);
            ShowError($"打印失败：{ex.GetType().Name}: {ex.Message}", InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            _printing = false;
            if (!succeeded) RemovePrintOverlay();
            RaisePrintEnabled();
        }
    }

    private void SaveSessionPrefs()
    {
        try
        {
            if (_queue != null) _lastPrinter = _queue.FullName;
            _lastLandscape = _landscape;
            _lastGrayscale = _grayscale;
            _lastDuplex = _duplex;
        }
        catch { /* 偏好记忆失败可忽略 */ }
    }

    private Border ShowPrintOverlay(out TextBlock status)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(new ProgressRing
        {
            IsActive = true,
            IsIndeterminate = true,
            Width = 64,
            Height = 64,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        status = new TextBlock
        {
            Text = "正在打印",
            Foreground = R("SecondaryForegroundBrush"),
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
        };
        panel.Children.Add(status);

        // 完全不透明：打印期间不透出底层设置/预览（与“正在打开”加载页同尺寸）。
        var veil = RColor("CardBackgroundBrush");
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, veil.R, veil.G, veil.B)),
            Child = panel,
        };
        Panel.SetZIndex(overlay, 99);
        _rootGrid.Children.Add(overlay);
        _printOverlay = overlay;
        return overlay;
    }

    private void RemovePrintOverlay()
    {
        if (_printOverlay is null) return;
        _rootGrid.Children.Remove(_printOverlay);
        _printOverlay = null;
        _printStatus = null;
    }

    // ────────────────────────── 主题资源 ──────────────────────────

    private static Brush R(string key)
        => Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;

    private static Color RColor(string key) => Application.Current.TryFindResource(key) switch
    {
        SolidColorBrush s => s.Color,
        LinearGradientBrush { GradientStops.Count: > 0 } g => g.GradientStops[0].Color,
        _ => Colors.White,
    };
}
