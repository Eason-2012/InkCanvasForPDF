using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;

namespace InkCanvasForPDF.Services;

/// <summary>直线拉直检测（隶属于墨迹纠正）：WinRT 形状识别未命中后，对单笔墨迹做
/// PCA/总最小二乘拟合判定是否为近似直线。仅参考算法概念（拟合优度判定、端点取投影极值），代码全新实现。</summary>
internal static class StraightLineDetector
{
    /// <summary>最少采样点数：少于此数不判定（防点击/短撇误判）。</summary>
    private const int MinPointCount = 5;

    /// <summary>PCA 解释方差比门槛：0.995 ≈ 次轴标准差为主轴 7% 的容差（越大越严格，误拉直少但灵敏度低）。</summary>
    private const double ExplainedVarianceThreshold = 0.995;

    /// <summary>协方差计算前均匀降采样上限（长笔画等间隔取样，控制计算量）。</summary>
    private const int MaxSamplePoints = 64;

    /// <summary>判定单笔墨迹是否为近似直线并产出规范直线匹配。
    /// <paramref name="minLength"/> 为最短首尾弦长（宿主传入 = 100 × DisplayScale，对齐三角/四边形跨度惯例）。
    /// 命中返回 ShapeName="Line"、HotPoints=[起点,终点] 的 InkShapeMatch；未命中返回 null。</summary>
    internal static InkShapeMatch? TryDetectLine(Stroke stroke, double minLength)
    {
        var stylusPoints = stroke.StylusPoints;
        if (stylusPoints == null || stylusPoints.Count < MinPointCount)
            return null;

        // 首尾弦长门槛
        Point first = stylusPoints[0].ToPoint();
        Point last = stylusPoints[stylusPoints.Count - 1].ToPoint();
        double chordX = last.X - first.X;
        double chordY = last.Y - first.Y;
        double chord = Math.Sqrt(chordX * chordX + chordY * chordY);
        if (chord < minLength)
            return null;

        // 均匀降采样
        int count = stylusPoints.Count;
        int step = Math.Max(1, count / MaxSamplePoints);
        var points = new List<Point>();
        for (int i = 0; i < count; i += step)
            points.Add(stylusPoints[i].ToPoint());
        if (points.Count < MinPointCount)
            return null;

        // 中心化 + 协方差（等比缩放不影响解释方差比，不做归一化）
        double meanX = 0, meanY = 0;
        foreach (Point p in points)
        {
            meanX += p.X;
            meanY += p.Y;
        }
        meanX /= points.Count;
        meanY /= points.Count;

        double covXX = 0, covYY = 0, covXY = 0;
        foreach (Point p in points)
        {
            double dx = p.X - meanX;
            double dy = p.Y - meanY;
            covXX += dx * dx;
            covYY += dy * dy;
            covXY += dx * dy;
        }

        // 2×2 对称矩阵闭式特征分解：λ = (trace ± sqrt(trace² − 4·det)) / 2
        double trace = covXX + covYY;
        double det = covXX * covYY - covXY * covXY;
        double disc = Math.Sqrt(Math.Max(0, trace * trace - 4 * det));
        double lambdaMax = (trace + disc) / 2;
        double lambdaMin = (trace - disc) / 2;
        double total = lambdaMax + lambdaMin;
        if (total <= 0)
            return null;   // 所有点重合（退化）

        // 判定：主方向解释的方差占比达标才视为直线
        if (lambdaMax / total < ExplainedVarianceThreshold)
            return null;

        // 主方向单位向量：(A − λI)v = 0 → v = (covXY, λmax − covXX)；协方差无交叉项时按主轴对齐
        double dirX, dirY;
        if (Math.Abs(covXY) > 1e-12)
        {
            dirX = covXY;
            dirY = lambdaMax - covXX;
        }
        else
        {
            dirX = covXX >= covYY ? 1 : 0;
            dirY = covXX >= covYY ? 0 : 1;
        }
        double dirLen = Math.Sqrt(dirX * dirX + dirY * dirY);
        if (dirLen <= 0)
            return null;
        dirX /= dirLen;
        dirY /= dirLen;

        // 端点 = 全部原始点在主方向投影的极值（几何最优端点，非起收笔抖动点）
        double minT = double.MaxValue, maxT = double.MinValue;
        for (int i = 0; i < count; i += step)
        {
            Point p = stylusPoints[i].ToPoint();
            double t = (p.X - meanX) * dirX + (p.Y - meanY) * dirY;
            if (t < minT) minT = t;
            if (t > maxT) maxT = t;
        }
        if (maxT - minT < minLength)
            return null;   // 投影长度复检（首尾弦可能因端点抖动虚高）

        var start = new Point(meanX + dirX * minT, meanY + dirY * minT);
        var end = new Point(meanX + dirX * maxT, meanY + dirY * maxT);

        double width = Math.Abs(end.X - start.X);
        double height = Math.Abs(end.Y - start.Y);
        var hotPoints = new PointCollection { start, end };
        return new InkShapeMatch
        {
            ShapeName = "Line",
            Center = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2),
            Width = width,
            Height = height,
            HotPoints = hotPoints,
            Strokes = new StrokeCollection { stroke }
        };
    }
}
