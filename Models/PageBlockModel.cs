using InkCanvasForPDF.ViewModels;

namespace InkCanvasForPDF.Models;

/// <summary>页面分块：外层列表只承载块（轻量，常驻），块进入视口附近时才实例化块内页槽。
/// 1 万页一次性实例化模板页槽会让 UI 线程卡死数十秒；分块后视觉树只保留视口附近约 3 块。</summary>
public sealed class PageBlockModel : ViewModelBase
{
    public int Index { get; init; }

    private double _heightBase;

    /// <summary>块内容高度（Base DIP，含页间距）：所有页固定高度之和，用于外层布局与滚动范围。
    /// 旋转某页改变页高时必须同步更新，否则块高与实际页槽不符、滚动范围错位。</summary>
    public double HeightBase
    {
        get => _heightBase;
        set => SetField(ref _heightBase, value);
    }

    private List<PageModel>? _pages;

    /// <summary>块内页模型；null = 未激活（内层列表不生成任何页槽）。</summary>
    public List<PageModel>? Pages
    {
        get => _pages;
        set => SetField(ref _pages, value);
    }
}
