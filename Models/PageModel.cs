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

    private int _contentWidth;

    /// <summary>未旋转内容宽（位图与墨迹坐标系尺寸；旋转只改显示槽位，不改内容坐标系）。</summary>
    public int ContentWidth
    {
        get => _contentWidth;
        set => SetField(ref _contentWidth, value);
    }

    private int _contentHeight;

    /// <summary>未旋转内容高（等比例随宽度变化）。</summary>
    public int ContentHeight
    {
        get => _contentHeight;
        set => SetField(ref _contentHeight, value);
    }

    private int _rotation;

    /// <summary>页面显示旋转角（0/90/180/270，顺时针）；仅影响显示，墨迹坐标不变。</summary>
    public int Rotation
    {
        get => _rotation;
        set => SetField(ref _rotation, value);
    }

    private int _width;

    /// <summary>显示槽位宽（旋转后包围盒：90/270 时为内容高，否则同内容宽，可写以复用容器避免重建白帧）。</summary>
    public int Width
    {
        get => _width;
        set => SetField(ref _width, value);
    }

    private int _height;

    /// <summary>显示槽位高（旋转后包围盒：90/270 时为内容宽，否则同内容高）。</summary>
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

    private bool _isMaterialized;

    /// <summary>墨迹画布是否已挂载：只有可见范围 ±缓冲 的页为 true，
    /// 其余页只保留轻量页槽与墨迹数据（Strokes 仍在模型上）。</summary>
    public bool IsMaterialized
    {
        get => _isMaterialized;
        set
        {
            if (_isMaterialized == value)
                return;
            _isMaterialized = value;
            // 先更新 Content 再更新模板开关：挂载时模板实例化的 DataContext 已就绪；
            // 卸载时先清 Content（销毁视觉、触发 Unloaded）再撤模板
            OnPropertyChanged(nameof(MaterializedContent));
            OnPropertyChanged(nameof(IsMaterialized));
        }
    }

    /// <summary>挂载时返回自身，未挂载返回 null：ContentControl 在 Content 与模板均为空时不生成视觉子树。</summary>
    public object? MaterializedContent => _isMaterialized ? this : null;
}
