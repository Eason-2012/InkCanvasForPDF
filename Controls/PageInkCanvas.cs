using System.Windows.Controls;
using InkCanvasForPDF.Services;

namespace InkCanvasForPDF.Controls;

/// <summary>PDF 页面画布：装配时间笔锋。
/// InkCanvas.DynamicRenderer 是 protected 属性，只能在子类构造函数内替换（替换会自动把自定义 DR 挂到内部 InkPresenter）。</summary>
public class PageInkCanvas : InkCanvas
{
    private readonly TimePressureDynamicRenderer _renderer;

    public PageInkCanvas()
    {
        Controller = new TimePressureController(this);
        _renderer = new TimePressureDynamicRenderer(Controller);
        DynamicRenderer = _renderer;   // InkCanvas 负责移除旧 DR、把新 DR 追加到 StylusPlugIns 末尾并挂接 RootVisual
        StylusPlugIns.Add(new TimePressurePlugIn(Controller));
    }

    /// <summary>本页的时间笔锋控制器（由 MainWindow 在加载/卸载与选项变化时驱动）。</summary>
    public TimePressureController Controller { get; }
}