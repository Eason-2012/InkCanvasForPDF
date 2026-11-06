using System.Windows.Media;
using InkCanvasForPDF.ViewModels;

namespace InkCanvasForPDF.Models;

/// <summary>缩略图：小尺寸矢量图 + 尺寸 + 当前页高亮。位图由渲染层按页旋转角原生旋转，尺寸即显示槽位尺寸。</summary>
public sealed class ThumbnailModel : ViewModelBase
{
    public int PageIndex { get; init; }

    /// <summary>打开文档时未旋转的缩略图内容尺寸（用于按宽高比推算旋转后的显示尺寸）。</summary>
    public int NaturalWidth { get; init; }

    /// <summary>未旋转的缩略图内容高。</summary>
    public int NaturalHeight { get; init; }

    private int _rotation;

    /// <summary>缩略图旋转角（0/90/180/270，顺时针）；渲染请求与显示尺寸都由它决定。</summary>
    public int Rotation
    {
        get => _rotation;
        set => SetField(ref _rotation, value);
    }

    private int _width;

    /// <summary>显示槽位宽（旋转后的位图显示宽）。</summary>
    public int Width
    {
        get => _width;
        set => SetField(ref _width, value);
    }

    private int _height;

    /// <summary>显示槽位高（旋转后的位图显示高）。</summary>
    public int Height
    {
        get => _height;
        set => SetField(ref _height, value);
    }

    public string PageNumber => (PageIndex + 1).ToString();

    private ImageSource? _image;

    public ImageSource? Image
    {
        get => _image;
        set => SetField(ref _image, value);
    }

    private bool _isCurrent;

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetField(ref _isCurrent, value);
    }

    private bool _isLoading;

    /// <summary>后台渲染进行中（防止滚动/重复触发时同一缩略图重复加载）。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => SetField(ref _isLoading, value);
    }
}
