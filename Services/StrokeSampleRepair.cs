using System;
using System.Windows.Ink;
using System.Windows.Input;

namespace InkCanvasForPDF.Services;

/// <summary>采样离群点修复：只处理"跳出去又回来"的物理不可能尖刺（相邻两段都远超正常采样间距，
/// 而跳变前的位置与跳变后的位置几乎重合）。正常书写、正常快速甩笔（不回头）不会被改动。
/// 干墨（原生笔画）与自绘笔画在收笔入口统一调用；湿墨采样流在 AppendSamples 用同一套判定。</summary>
internal static class StrokeSampleRepair
{
    /// <summary>相邻点距超过该值视为"跳变"（页面坐标 px）。快速甩笔相邻点约 20~50px，正常书写远低于此。</summary>
    public const double JumpPx = 150.0;

    /// <summary>跳变前位置与跳变后位置的距离小于该值，才认定是"跳出去又回来"的离群点。</summary>
    public const double ReturnPx = 20.0;

    /// <summary>原地修复墨迹点集合；返回是否实际改动。</summary>
    public static bool Apply(Stroke stroke)
    {
        var points = stroke.StylusPoints;
        int n = points.Count;
        if (n < 3)
            return false;

        var drop = new bool[n];
        int maxDrop = Math.Max(2, n / 20);   // 上限：防极端数据被大面积改写
        int dropped = 0;

        // 首点：与第二点远跳、而第二点与第三点间距正常 → 上一笔残留的首报告点
        if (IsFar(points[0], points[1]) && !IsFar(points[1], points[2]))
        {
            drop[0] = true;
            dropped++;
        }

        // 中间点：前后两段都远跳、但跳变前后位置几乎重合 → 外突尖刺
        for (int i = 1; i < n - 1 && dropped < maxDrop; i++)
        {
            if (drop[i - 1])
                continue;
            if (IsFar(points[i - 1], points[i])
                && IsFar(points[i], points[i + 1])
                && IsClose(points[i - 1], points[i + 1]))
            {
                drop[i] = true;
                dropped++;
            }
        }

        // 末点：与倒数第二点远跳、而倒数第二点与倒数第三点间距正常 → 残留的收笔报告点
        if (dropped < maxDrop && IsFar(points[n - 2], points[n - 1]) && !IsFar(points[n - 3], points[n - 2]))
        {
            drop[n - 1] = true;
            dropped++;
        }

        if (dropped == 0)
            return false;

        var rebuilt = new StylusPointCollection(n - dropped);
        for (int i = 0; i < n; i++)
        {
            if (drop[i])
                continue;
            var point = points[i];
            rebuilt.Add(new StylusPoint(point.X, point.Y, point.PressureFactor));
        }
        if (rebuilt.Count < 2)
            return false;

        try
        {
            stroke.StylusPoints = rebuilt;   // 触发重绘；压力按原点保留
        }
        catch
        {
            return false;
        }
        ConsoleLog.Write("时间笔锋", $"修复离群点: 点数={n}→{rebuilt.Count} 跳过={dropped}");
        return true;
    }

    private static bool IsFar(StylusPoint a, StylusPoint b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return dx * dx + dy * dy > JumpPx * JumpPx;
    }

    private static bool IsClose(StylusPoint a, StylusPoint b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return dx * dx + dy * dy < ReturnPx * ReturnPx;
    }
}
