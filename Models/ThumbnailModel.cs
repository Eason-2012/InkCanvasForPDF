using System.Windows.Media;
using InkCanvasForPDF.ViewModels;

namespace InkCanvasForPDF.Models;

/// <summary>缩略图：小尺寸矢量图 + 尺寸 + 当前页高亮。</summary>
public sealed class ThumbnailModel : ViewModelBase
{
    public int PageIndex { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

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
