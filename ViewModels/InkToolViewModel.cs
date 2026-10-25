using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using InkCanvasForPDF.Board.Helpers;
using InkCanvasForPDF.Models;

namespace InkCanvasForPDF.ViewModels;

/// <summary>墨迹工具模式：None=浏览/滚动，Pen=批注书写，Eraser=面积擦，Select=套索选择（ICA"选择"）。</summary>
public enum InkToolMode
{
    None,
    Pen,
    Eraser,
    Select
}

/// <summary>墨迹类型（墨迹菜单页签）：Hard=硬笔（关压感），Soft=软笔（开压感），Highlighter=荧光笔。</summary>
public enum InkPenType
{
    Hard = 0,
    Soft = 1,
    Highlighter = 2
}

/// <summary>墨迹批注工具状态 + 全局撤销时间线（ICA TimeMachine 语义：捕获笔画集合实例直改、按引用回放）。
/// 墨迹语义抄 Ink-Canvas：绘制提交 UserInput、面积擦提交"原笔+拆分"。</summary>
public sealed class InkToolViewModel : ViewModelBase
{
    private InkToolMode _activeTool = InkToolMode.None;

    /// <summary>当前工具（切换时通知 EditingMode/IsInkMode）。</summary>
    public InkToolMode ActiveTool
    {
        get => _activeTool;
        private set
        {
            if (SetField(ref _activeTool, value))
            {
                OnPropertyChanged(nameof(EditingMode));
                OnPropertyChanged(nameof(IsInkMode));
                OnPropertyChanged(nameof(ForceCursor));
            }
        }
    }

    /// <summary>是否处于墨迹模式（页面 InkCanvas 参与命中测试）。</summary>
    public bool IsInkMode => ActiveTool != InkToolMode.None;

    private int _shapeDrawMode;

    /// <summary>形状手绘模式（ICA drawingShapeMode 语义：0=关，1-25=对应形状）。
    /// 非 0 时页面画布 EditingMode 置 None，由 MainWindow 的鼠标/触摸事件自行生成形状笔画。</summary>
    public int ShapeDrawMode
    {
        get => _shapeDrawMode;
        set
        {
            if (SetField(ref _shapeDrawMode, value))
                OnPropertyChanged(nameof(EditingMode));
        }
    }

    /// <summary>形状长按锁定（ICA isLongPressSelected：长按按钮保持形状模式，画完不自动回笔）。</summary>
    public bool IsShapeLongPressSelected { get; set; }

    private bool _showPenCursor;

    /// <summary>是否显示画笔光标（设置项对齐 ICA isShowCursor）。开启后仅批注 Ink 模式强制钢笔光标。</summary>
    public bool ShowPenCursor
    {
        get => _showPenCursor;
        set
        {
            if (SetField(ref _showPenCursor, value))
                OnPropertyChanged(nameof(ForceCursor));
        }
    }

    private bool _fitToCurve = true;

    /// <summary>墨迹平滑（贝塞尔美化笔迹，作用于新笔画）。</summary>
    public bool FitToCurve
    {
        get => _fitToCurve;
        set
        {
            if (SetField(ref _fitToCurve, value))
                ApplyCurrentColor();
        }
    }

    private InkPenType _inkType = InkPenType.Hard;

    /// <summary>当前墨迹类型（墨迹菜单页签）。切换时通知 PressureEnabled（压感跟随软/硬笔）。</summary>
    public InkPenType InkType
    {
        get => _inkType;
        private set
        {
            if (SetField(ref _inkType, value))
                OnPropertyChanged(nameof(PressureEnabled));
        }
    }

    /// <summary>压感跟随墨迹类型：软笔开、硬笔关（荧光笔页签视为关，荧光笔本就豁免时间笔锋武装、等粗绘制）。</summary>
    public bool PressureEnabled => InkType == InkPenType.Soft;

    /// <summary>是否强制钢笔光标（仅批注 Ink 模式且开启显示时才 ForceCursor；其余模式保持 WPF 自动光标）。</summary>
    public bool ForceCursor => ShowPenCursor && ActiveTool == InkToolMode.Pen;

    private bool _isStrokeEraser;

    /// <summary>橡皮模式：false=面积擦（EraseByPoint），true=墨迹擦（EraseByStroke）。持久化到 Settings.EraserModeByStrokeSetting（画板/PDF 共享）。</summary>
    public bool IsStrokeEraser
    {
        get => _isStrokeEraser;
        set
        {
            if (SetField(ref _isStrokeEraser, value))
                OnPropertyChanged(nameof(EditingMode));
        }
    }

    /// <summary>页面 InkCanvas 编辑模式（形状模式/视图模式置 None；选择=内建套索，ICA BtnSelect_Click 语义）。</summary>
    public InkCanvasEditingMode EditingMode => ShapeDrawMode != 0
        ? InkCanvasEditingMode.None
        : ActiveTool switch
        {
            InkToolMode.Eraser => IsStrokeEraser ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.EraseByPoint,
            InkToolMode.Pen => InkCanvasEditingMode.Ink,
            InkToolMode.Select => InkCanvasEditingMode.Select,
            _ => InkCanvasEditingMode.None
        };

    private DrawingAttributes _drawingAttributes = new()
    {
        Color = Colors.Red,
        Width = 3,
        Height = 3,
        FitToCurve = true
    };

    /// <summary>当前画笔属性（绑定页面 InkCanvas.DefaultDrawingAttributes）。</summary>
    public DrawingAttributes DrawingAttributes
    {
        get => _drawingAttributes;
        private set => SetField(ref _drawingAttributes, value);
    }

    private double _displayScale = 1.0;

    /// <summary>页面显示缩放（整层 ScaleTransform 缩放）。墨迹画布随整层缩放，故"粗细"设 1 即为屏幕 1，
    /// 须把画笔宽度除以缩放抵消放大（含 <100% 缩放），保证屏幕恒定（设 1 → 恰好 1）。</summary>
    public double DisplayScale
    {
        get => _displayScale;
        set
        {
            if (SetField(ref _displayScale, value))
                ApplyCurrentColor();
        }
    }

    private double _eraserSize = 50;

    /// <summary>面积擦直径（页面坐标，屏幕恒定尺寸需除以 DisplayScale，由代码后置处理）。</summary>
    public double EraserSize
    {
        get => _eraserSize;
        set => SetField(ref _eraserSize, value);
    }

    /// <summary>橡皮粗细滑块值(1~30) → 实际直径（15 + 3.5x，替代旧 ×4；滑块 1~30 → 18.5~120）。</summary>
    public static double EraserSizeFromSlider(double sliderValue) => 15 + 3.5 * sliderValue;

    /// <summary>实际直径 → 橡皮粗细滑块值(1~30)：反向换算并夹取有效范围。</summary>
    public static double SliderFromEraserSize(double size) => Math.Clamp((size - 15) / 3.5, 1, 30);

    // ══════════ 墨迹选项（抄 Ink-Canvas MW_PenColors / MW_Settings，无 Settings 持久化）══════════

    // 画笔色-亮系（抄 ICA inkColorLightThemeMapping：0黑 1红 2绿 3蓝 4黄 5白 6粉 7青 8橙）
    public static readonly Dictionary<int, Color> PenLightColorMap = new()
    {
        [0] = Colors.Black,                       // 黑
        [1] = Color.FromRgb(239, 68, 68),         // 红
        [2] = Color.FromRgb(34, 197, 94),         // 绿
        [3] = Color.FromRgb(59, 130, 246),        // 蓝
        [4] = Color.FromRgb(250, 204, 21),        // 黄
        [5] = Colors.White,                       // 白
        [6] = Color.FromRgb(236, 72, 153),        // 粉
        [7] = Color.FromRgb(20, 184, 166),        // 青
        [8] = Color.FromRgb(249, 115, 22)         // 橙
    };

    // 画笔色-暗系（抄 ICA inkColorDarkThemeMapping）
    public static readonly Dictionary<int, Color> PenDarkColorMap = new()
    {
        [0] = Colors.Black,                       // 黑
        [1] = Color.FromRgb(220, 38, 38),         // 红
        [2] = Color.FromRgb(22, 163, 74),         // 绿
        [3] = Color.FromRgb(37, 99, 235),         // 蓝
        [4] = Color.FromRgb(234, 179, 8),         // 黄
        [5] = Colors.White,                       // 白
        [6] = Color.FromRgb(147, 51, 234),        // 粉(紫)
        [7] = Color.FromRgb(13, 148, 136),        // 青
        [8] = Color.FromRgb(234, 88, 12)          // 橙
    };

    private static readonly Dictionary<int, Color> HighlighterColorMap = new()
    {
        [101] = Color.FromRgb(255, 94, 83),    // 红
        [102] = Color.FromRgb(255, 112, 13),   // 橙
        [103] = Color.FromRgb(255, 211, 8),    // 黄
        [104] = Color.FromRgb(16, 186, 171),   // 青
        [105] = Color.FromRgb(74, 118, 255),   // 蓝
        [106] = Color.FromRgb(147, 51, 234)    // 紫
    };

    private int _inkColor = 1;   // 默认红（对齐 ICA inkColor 默认）

    /// <summary>当前墨迹颜色索引：0~8 画笔色，101~106 荧光笔色。</summary>
    public int InkColor
    {
        get => _inkColor;
        private set => SetField(ref _inkColor, value);
    }

    // 页签记忆选项（按工具全记）：硬笔/软笔共用同一套 颜色+粗细（切换只改压感），荧光笔独立记 颜色+粗细；
    // 笔锋 InkStyle 为全局共享——硬笔/软笔/PDF/画板都记同一个
    private int _penColor = 1;              // 默认红
    private double _penWidth = 5;
    private int _highlighterColor = 103;    // 默认黄
    private double _highlighterWidth = 5;

    // 存储字段只读暴露（供 SaveInkOptions / LoadFromSettings 持久化往返）
    public int PenColor => _penColor;
    public double PenWidth => _penWidth;
    public int HighlighterColor => _highlighterColor;
    public double HighlighterWidth => _highlighterWidth;

    /// <summary>切换墨迹类型（墨迹菜单页签）：当前活动值回写旧类型存储 → 装入新类型存储 → 应用画笔属性。
    /// 笔锋为全局共享不随类型切换；压感随 InkType 通知自动联动（软笔开/硬笔关）。</summary>
    public void SwitchInkType(InkPenType type)
    {
        if (type == InkType)
            return;
        switch (InkType)   // 回写旧类型
        {
            case InkPenType.Hard:
            case InkPenType.Soft:
                _penColor = InkColor; _penWidth = InkWidth;
                break;
            case InkPenType.Highlighter:
                _highlighterColor = InkColor; _highlighterWidth = InkWidth;
                break;
        }
        InkType = type;
        switch (type)   // 装入新类型（SetInkColor 内部校验合法性 + ApplyCurrentColor）
        {
            case InkPenType.Hard:
            case InkPenType.Soft:
                InkWidth = Math.Clamp(_penWidth, 1, 30);
                SetInkColor(PenLightColorMap.ContainsKey(_penColor) ? _penColor : 1);
                break;
            case InkPenType.Highlighter:
                InkWidth = Math.Clamp(_highlighterWidth, 1, 30);
                SetInkColor(HighlighterColorMap.ContainsKey(_highlighterColor) ? _highlighterColor : 103);
                break;
        }
        ApplyCurrentColor();
    }

    /// <summary>启动恢复：把持久化设置装入各类型存储，并按记忆的页签类型装入活动值（替代逐项恢复）。</summary>
    public void LoadFromSettings(AppSettings s)
    {
        _penColor = PenLightColorMap.ContainsKey(s.PenColor) ? s.PenColor : 1;
        _penWidth = Math.Clamp(s.PenWidth, 1, 30);
        _highlighterColor = HighlighterColorMap.ContainsKey(s.HighlighterColor) ? s.HighlighterColor : 103;
        _highlighterWidth = Math.Clamp(s.HighlighterWidth, 1, 30);
        InkAlpha = (byte)Math.Clamp(s.InkAlpha, 20, 240);
        InkStyle = Math.Clamp(s.InkStyle, 0, 3);   // 笔锋全局共享
        IsInkToShapeEnabled = s.IsInkToShapeEnabled;   // 墨迹纠正全局共享
        IsMultiFingerInk = s.IsMultiFingerInkEnabled;   // 多指书写全局共享
        InkType = (InkPenType)Math.Clamp(s.InkType, 0, 2);
        switch (InkType)
        {
            case InkPenType.Hard:
            case InkPenType.Soft:
                InkWidth = _penWidth;
                SetInkColor(_penColor);
                break;
            case InkPenType.Highlighter:
                InkWidth = _highlighterWidth;
                SetInkColor(_highlighterColor);
                break;
        }
    }

    private double _inkWidth = 3;

    /// <summary>画笔粗细（1~30）。</summary>
    public double InkWidth
    {
        get => _inkWidth;
        set
        {
            if (!SetField(ref _inkWidth, Math.Clamp(value, 1, 30)))
                return;
            ApplyCurrentColor();   // 荧光笔宽度 = 30 + InkWidth/2
        }
    }

    private byte _inkAlpha = 120;

    /// <summary>荧光笔不透明度（20~240，对应 alpha）。</summary>
    public byte InkAlpha
    {
        get => _inkAlpha;
        set
        {
            if (!SetField(ref _inkAlpha, value))
                return;
            if (InkColor > 100)
                ApplyCurrentColor();
        }
    }

    private int _inkStyle;

    /// <summary>笔锋：0=点集笔锋，1=速率笔锋，2=关闭笔锋，3=时间笔锋（落笔 250ms 内实时变粗）。</summary>
    public int InkStyle
    {
        get => _inkStyle;
        set => SetField(ref _inkStyle, value);
    }

    private bool _isMultiFingerInk;

    /// <summary>多指书写：true=允许多指同时在 PDF 上书写（停用第 2 指停墨抑制）。</summary>
    public bool IsMultiFingerInk
    {
        get => _isMultiFingerInk;
        set => SetField(ref _isMultiFingerInk, value);
    }

    private bool _isInkToShapeEnabled = false;

    /// <summary>墨迹纠正（抄 ICA Settings.InkToShape.IsInkToShapeEnabled）：全局开关，PDF 墨迹栏与画板墨迹面板共享同一个值。</summary>
    public bool IsInkToShapeEnabled
    {
        get => _isInkToShapeEnabled;
        set => SetField(ref _isInkToShapeEnabled, value);
    }

    private bool _isUselightThemeColor = false;

    /// <summary>调色盘主题：true=亮系，false=暗系（切换时重算当前墨迹色；色块背景由 InkToolBar 重绘）。</summary>
    public bool IsUselightThemeColor
    {
        get => _isUselightThemeColor;
        set
        {
            if (SetField(ref _isUselightThemeColor, value))
                ApplyCurrentColor();
        }
    }

    /// <summary>按当前颜色索引重建 DrawingAttributes（画笔=实色+粗细；荧光笔=半透明+加粗+IsHighlighter）。
    /// 宽度除以页面缩放 DisplayScale 抵消整层 ScaleTransform 的放大——任何缩放（含 <100%）都屏幕恒定
    /// （缩小时 DA 页面值放大、整层缩放同步缩小，屏幕值不变，故不加 1.0 地板）；
    /// 粗细应用时 ÷2 对齐画板 ICA 约定（画板存储值 = 滑块显示值的一半），保证两侧同滑块值粗细一致。</summary>
    private void ApplyCurrentColor()
    {
        double scale = DisplayScale;
        var da = DrawingAttributes.Clone();
        da.FitToCurve = FitToCurve;
        if (InkColor > 100)
        {
            var c = HighlighterColorMap[InkColor];
            da.Color = Color.FromArgb(InkAlpha, c.R, c.G, c.B);
            da.IsHighlighter = true;
            da.IgnorePressure = true;   // 荧光笔屏蔽压感（等粗绘制）
            da.Width = (30 + InkWidth * 2) / scale;   // 对齐 ICA：实际宽 = 30 + 滑块值（滑块值 = InkWidth）
            da.Height = (30 + InkWidth * 2) / scale;
        }
        else
        {
            da.Color = (IsUselightThemeColor ? PenLightColorMap : PenDarkColorMap)[InkColor];
            da.IsHighlighter = false;
            da.IgnorePressure = false;  // Clone 会带走上一次荧光笔的值，复位为 WPF 默认（跟随压感）
            da.Width = InkWidth / 2 / scale;
            da.Height = InkWidth / 2 / scale;
        }
        DrawingAttributes = da;
    }

    /// <summary>切换墨迹颜色（0~8 画笔色 / 101~106 荧光笔色），并按当前墨迹类型写入对应记忆存储。</summary>
    public void SetInkColor(int inkColor)
    {
        if (!PenLightColorMap.ContainsKey(inkColor) && !HighlighterColorMap.ContainsKey(inkColor))
            return;
        switch (InkType)
        {
            case InkPenType.Hard:
            case InkPenType.Soft: _penColor = inkColor; break;
            case InkPenType.Highlighter: _highlighterColor = inkColor; break;
        }
        InkColor = inkColor;
        ApplyCurrentColor();
    }

    // ══════════ 工具切换命令 ══════════

    public RelayCommand SetNoneCommand { get; }
    public RelayCommand SetPenCommand { get; }
    public RelayCommand SetEraserCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }

    public InkToolViewModel()
    {
        SetNoneCommand = new RelayCommand(() => SetActiveTool(InkToolMode.None));
        SetPenCommand = new RelayCommand(() => SetActiveTool(InkToolMode.Pen));
        SetEraserCommand = new RelayCommand(() => SetActiveTool(InkToolMode.Eraser));
        UndoCommand = new RelayCommand(Undo);   // 可执行态由 TimeMachine 状态事件驱动（对齐画板 BoardUndo.IsEnabled）
        RedoCommand = new RelayCommand(Redo, () => _canRedo);
        TimeMachine.OnUndoStateChanged += b =>
        {
            _canUndo = b;
            RaiseHistoryChanged();
        };
        TimeMachine.OnRedoStateChanged += b =>
        {
            _canRedo = b;
            RaiseHistoryChanged();
        };
    }

    public void SetActiveTool(InkToolMode mode)
    {
        ShapeDrawMode = 0;   // ICA 语义：切工具即退出形状模式（几何按钮处理器随后再设回）
        ActiveTool = mode;
    }

    // ══════════ 全局撤销（直接复用画板 TimeMachine 引擎，ICA 原版）══════════

    /// <summary>画板 TimeMachine 引擎（ICA 原版类直接复用；条目 TargetStrokes 指向发生页的笔画集合）。</summary>
    public TimeMachine TimeMachine { get; } = new();

    /// <summary>回放到画布的回调（MainWindow 注入：等价画板 ApplyHistoryToCanvas）。</summary>
    public Action<TimeMachineHistory>? ApplyHistoryToCanvas { get; set; }

    private bool _canUndo, _canRedo;

    /// <summary>撤销/重做（等价画板 BtnUndo_Click：Undo 翻转标志 → 回放到目标集合）。</summary>
    public void Undo()
    {
        if (!_canUndo) return;   // 历史为空：no-op（按钮/Ctrl+Z 始终可点，栈空不动作）
        var item = TimeMachine.Undo();
        if (item != null)
            ApplyHistoryToCanvas?.Invoke(item);
        RaiseHistoryChanged();
    }

    public void Redo()
    {
        var item = TimeMachine.Redo();
        if (item != null)
            ApplyHistoryToCanvas?.Invoke(item);
        RaiseHistoryChanged();
    }

    /// <summary>打开新文档时清空历史（旧条目引用的 PageModel 已销毁）。</summary>
    public void ClearHistory()
    {
        TimeMachine.ClearStrokeHistory();
        RaiseHistoryChanged();
    }

    private void RaiseHistoryChanged()
    {
        CommandManager.InvalidateRequerySuggested();
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
    }
}
