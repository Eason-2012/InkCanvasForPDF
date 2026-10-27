using System;
using System.Windows.Ink;

namespace InkCanvasForPDF.Services;

/// <summary>稀疏笔画降级平滑：相邻采样间距过大时，WPF 的 FitToCurve 贝塞尔拟合会在点距悬殊/方向急变处
/// 越过点集包络（视觉上"墨迹飞出去又拉回来"）。此时只关闭本条笔画的 FitToCurve，
/// 不碰共享 DrawingAttributes、不改用户的「墨迹平滑」设置。</summary>
internal static class StrokeSmoothingGuard
{
    /// <summary>相邻点距超过该值视为稀疏（页面坐标 px）。快速甩笔约 20~50px，正常书写远低于此。</summary>
    public const double SparseGapPx = 120.0;

    /// <summary>命中稀疏条件则关闭本条笔画的平滑；返回是否降级。</summary>
    public static void Apply(Stroke stroke)
    {
        var points = stroke.StylusPoints;
        int count = points.Count;
        if (count < 2)
            return;

        double maxGap = 0;
        for (int i = 1; i < count; i++)
        {
            double dx = points[i].X - points[i - 1].X;
            double dy = points[i].Y - points[i - 1].Y;
            maxGap = Math.Max(maxGap, Math.Sqrt(dx * dx + dy * dy));
        }
        if (maxGap <= SparseGapPx)
            return;

        var attributes = stroke.DrawingAttributes;
        if (!attributes.FitToCurve)
            return;

        try
        {
            var clone = attributes.Clone();
            clone.FitToCurve = false;
            stroke.DrawingAttributes = clone;   // 替换 DA 触发重绘
        }
        catch
        {
            return;   // DA 冻结等异常：跳过，不影响收笔流程
        }
        ConsoleLog.Write("墨迹平滑", $"稀疏笔迹: 点数={count} 最大间距={maxGap:F0}px → 本笔关闭平滑");
    }
}
