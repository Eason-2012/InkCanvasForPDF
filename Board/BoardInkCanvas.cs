#nullable disable
using System.Windows.Controls;
using InkCanvasForPDF.Services;

namespace InkCanvasForPDF.Board;

/// <summary>画板画布：装配时间笔锋。
/// InkCanvas.DynamicRenderer 是 protected 属性，只能在子类构造函数内替换（替换会自动把自定义 DR 挂到内部 InkPresenter）。</summary>
public class BoardInkCanvas : InkCanvas
{
    private readonly TimePressureDynamicRenderer _renderer;

    public BoardInkCanvas()
    {
        Controller = new TimePressureController(this);
        _renderer = new TimePressureDynamicRenderer(Controller);
        DynamicRenderer = _renderer;   // InkCanvas 负责移除旧 DR、把新 DR 追加到 StylusPlugIns 末尾并挂接 RootVisual
        StylusPlugIns.Add(new TimePressurePlugIn(Controller));
    }

    /// <summary>画板的时间笔锋控制器（由 BoardView 在武装刷新/清理时驱动）。</summary>
    public TimePressureController Controller { get; }
}