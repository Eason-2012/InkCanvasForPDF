using System.Windows.Ink;
using System.Windows.Media;
using InkCanvasForPDF.ViewModels;

namespace InkCanvasForPDF.Models;

/// <summary>单页 PDF：矢量页内容 + 尺寸。宽高独立保存，便于后续叠加批注画布。</summary>
public sealed class PageModel : ViewModelBase
{
    public int PageIndex { get; init; }

    /// <summary>该页墨迹批注（叠加层 StrokeCollection，直接绑定页面 InkCanvas）。</summary>
    public StrokeCollection Strokes { get; } = new();

    private int _width;

    /// <summary>页面 DIP 宽（视口宽度变化时更新，可写以复用容器避免重建白帧）。</summary>
    public int Width
    {
        get => _width;
        set => SetField(ref _width, value);
    }

    private int _height;

    /// <summary>页面 DIP 高（等比例随宽度变化）。</summary>
    public int Height
    {
        get => _height;
        set => SetField(ref _height, value);
    }

    private ImageSource? _image;

    public ImageSource? Image
    {
        get => _image;
        set => SetField(ref _image, value);
    }
}
