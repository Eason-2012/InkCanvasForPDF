using System.Diagnostics;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using WinRtAnalyzer = global::Windows.UI.Input.Inking.Analysis.InkAnalyzer;
using WinRtDrawingKind = global::Windows.UI.Input.Inking.Analysis.InkAnalysisDrawingKind;
using WinRtInkDrawingAttributes = global::Windows.UI.Input.Inking.InkDrawingAttributes;
using WinRtInkStroke = global::Windows.UI.Input.Inking.InkStroke;
using WinRtNode = global::Windows.UI.Input.Inking.Analysis.IInkAnalysisNode;

namespace InkCanvasForPDF.Services;

/// <summary>识别到的规范图形（墨迹纠正的目标）。</summary>
internal sealed class InkShapeMatch
{
    /// <summary>图形名（Circle/Ellipse/Triangle/Rectangle/Square/Diamond/Parallelogram/Quadrilateral/Trapezoid）。</summary>
    public required string ShapeName { get; init; }

    /// <summary>图形中心（画布坐标）。</summary>
    public Point Center { get; init; }

    /// <summary>图形包围盒宽（画布坐标）。</summary>
    public double Width { get; init; }

    /// <summary>图形包围盒高（画布坐标）。</summary>
    public double Height { get; init; }

    /// <summary>图形热点（角点/端点，WinRT 返回的点集）。</summary>
    public PointCollection HotPoints { get; init; } = new();

    /// <summary>构成本图形的画布笔画（仅含仍在画布上的）。</summary>
    public StrokeCollection Strokes { get; init; } = new();
}

/// <summary>墨迹形状识别后端：WinRT <c>Windows.UI.Input.Inking.Analysis.InkAnalyzer</c>。
/// 必须在 UI 线程上调用与等待（不 <c>Task.Run</c>、不 <c>GetResult()</c>，以免 WinRT 延续贴回 UI 线程时死锁）。</summary>
internal static class WinRtInkShapeRecognizer
{
    /// <summary>识别 <paramref name="strokes"/>，并只接受包含 <paramref name="mustInclude"/>（刚落笔的那一笔）的图形；
    /// 识别失败/不可用/无匹配一律返回 null。</summary>
    internal static async Task<InkShapeMatch?> RecognizeAsync(StrokeCollection strokes, Stroke mustInclude)
    {
        if (strokes.Count == 0)
        {
            ConsoleLog.Write("墨迹纠正", "WinRT识别未命中: 无输入笔画");
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var analyzer = new WinRtAnalyzer();
            var idToStroke = new Dictionary<uint, Stroke>();
            foreach (Stroke stroke in strokes)
            {
                var inkStroke = CreateInkStroke(stroke);
                if (inkStroke == null)
                    continue;
                analyzer.AddDataForStroke(inkStroke);
                analyzer.SetStrokeDataKind(
                    inkStroke.Id,
                    global::Windows.UI.Input.Inking.Analysis.InkAnalysisStrokeKind.Drawing);
                idToStroke[inkStroke.Id] = stroke;
            }

            if (idToStroke.Count == 0)
            {
                ConsoleLog.Write("墨迹纠正", "WinRT识别未命中: 无可映射笔画");
                return null;
            }

            var analysisResult = await analyzer.AnalyzeAsync().AsTask().ConfigureAwait(true);
            if (analysisResult?.Status != global::Windows.UI.Input.Inking.Analysis.InkAnalysisStatus.Updated)
            {
                ConsoleLog.Write("墨迹纠正", $"WinRT识别未命中: 分析状态非Updated ({analysisResult?.Status.ToString() ?? "null"})");
                return null;
            }

            var drawing = FindDrawing(analyzer.AnalysisRoot, idToStroke, mustInclude);
            if (drawing == null)
            {
                ConsoleLog.Write("墨迹纠正", "WinRT识别未命中: 未找到包含最新一笔的图形");
                return null;
            }

            var shapeName = MapShapeName(drawing.DrawingKind);
            if (shapeName == null)
            {
                ConsoleLog.Write("墨迹纠正", $"WinRT识别未命中: 形状名未映射 ({drawing.DrawingKind})");
                return null;
            }

            var points = CopyPoints(drawing);
            if (!HasValidGeometry(drawing.DrawingKind, points))
            {
                ConsoleLog.Write("墨迹纠正", $"WinRT识别未命中: 几何无效 ({shapeName}, 点数={points.Length})");
                return null;
            }

            // 只替换构成本图形的笔画；最新的那一笔必须在内（否则可能命中画布上更早的无关图形）
            var matched = new StrokeCollection();
            var ids = drawing.GetStrokeIds();
            if (ids == null)
            {
                ConsoleLog.Write("墨迹纠正", "WinRT识别未命中: 图形笔画ID列表为空");
                return null;
            }
            foreach (var id in ids)
            {
                if (idToStroke.TryGetValue(id, out var stroke))
                    matched.Add(stroke);
            }

            if (matched.Count == 0 || !matched.Contains(mustInclude))
            {
                ConsoleLog.Write("墨迹纠正", $"WinRT识别未命中: 不含最新一笔 (matched={matched.Count})");
                return null;
            }

            BoundsFromPoints(points, out double width, out double height);
            var center = drawing.Center;
            stopwatch.Stop();
            ConsoleLog.Write("墨迹纠正",
                $"WinRT命中: shape={shapeName}, 笔画={matched.Count}, 耗时={stopwatch.ElapsedMilliseconds}ms");
            return new InkShapeMatch
            {
                ShapeName = shapeName,
                Center = new Point(center.X, center.Y),
                Width = width,
                Height = height,
                HotPoints = ToPointCollection(points),
                Strokes = matched
            };
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("墨迹纠正", $"WinRT识别异常: {ex}");
            return null;   // 识别属书写增强：任何异常直接放弃本轮，保留手绘笔画
        }
    }

    /// <summary>WPF 笔画 → WinRT 笔画：只取坐标（丢弃压力），画笔属性按官方 InkAnalysis 推荐（圆形笔尖、FitToCurve）。</summary>
    private static WinRtInkStroke? CreateInkStroke(Stroke stroke)
    {
        var stylusPoints = stroke.StylusPoints;
        var attributes = stroke.DrawingAttributes;
        if (stylusPoints == null || stylusPoints.Count == 0 || attributes == null)
            return null;

        var winAttributes = new WinRtInkDrawingAttributes
        {
            PenTip = global::Windows.UI.Input.Inking.PenTipShape.Circle,
            Color = global::Windows.UI.Color.FromArgb(
                attributes.Color.A, attributes.Color.R, attributes.Color.G, attributes.Color.B),
            Size = new global::Windows.Foundation.Size(
                Math.Max(1f, (float)attributes.Width), Math.Max(1f, (float)attributes.Height)),
            FitToCurve = true,
            IgnorePressure = false
        };

        var builder = new global::Windows.UI.Input.Inking.InkStrokeBuilder();
        builder.SetDefaultDrawingAttributes(winAttributes);

        var points = new List<global::Windows.Foundation.Point>(stylusPoints.Count);
        foreach (StylusPoint stylusPoint in stylusPoints)
        {
            var point = stylusPoint.ToPoint();
            points.Add(new global::Windows.Foundation.Point((float)point.X, (float)point.Y));
        }

        return points.Count == 0 ? null : builder.CreateStroke(points);
    }

    /// <summary>在分析树中找「包含最新一笔」的图形节点；多个候选时取包围盒面积最大者。</summary>
    private static global::Windows.UI.Input.Inking.Analysis.InkAnalysisInkDrawing? FindDrawing(
        WinRtNode? root,
        IReadOnlyDictionary<uint, Stroke> idToStroke,
        Stroke mustInclude)
    {
        if (root == null)
            return null;

        global::Windows.UI.Input.Inking.Analysis.InkAnalysisInkDrawing? best = null;
        double bestArea = -1;
        var stack = new Stack<WinRtNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is global::Windows.UI.Input.Inking.Analysis.InkAnalysisInkDrawing drawing &&
                drawing.DrawingKind != WinRtDrawingKind.Drawing &&
                ContainsStroke(drawing, idToStroke, mustInclude))
            {
                BoundsFromPoints(CopyPoints(drawing), out double width, out double height);
                double area = width * height;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = drawing;
                }
            }

            // WinRT IInkAnalysisNode.Children 可能为 null，不可直接 foreach
            var children = node.Children;
            if (children == null)
                continue;
            foreach (var child in children)
            {
                if (child != null)
                    stack.Push(child);
            }
        }

        return best;
    }

    private static bool ContainsStroke(
        global::Windows.UI.Input.Inking.Analysis.InkAnalysisInkDrawing drawing,
        IReadOnlyDictionary<uint, Stroke> idToStroke,
        Stroke stroke)
    {
        var ids = drawing.GetStrokeIds();
        if (ids == null)
            return false;
        foreach (var id in ids)
        {
            if (idToStroke.TryGetValue(id, out var mapped) && ReferenceEquals(mapped, stroke))
                return true;
        }
        return false;
    }

    private static bool HasValidGeometry(
        WinRtDrawingKind kind,
        IReadOnlyList<global::Windows.Foundation.Point> points)
    {
        if (points == null || points.Count == 0)
            return false;

        int required = kind == WinRtDrawingKind.Circle || kind == WinRtDrawingKind.Ellipse ? 4 : 3;
        BoundsFromPoints(points, out double width, out double height);
        return points.Count >= required && width > 0 && height > 0;
    }

    private static string? MapShapeName(WinRtDrawingKind kind)
    {
        switch (kind)
        {
            case WinRtDrawingKind.Circle: return "Circle";
            case WinRtDrawingKind.Ellipse: return "Ellipse";
            case WinRtDrawingKind.Triangle:
            case WinRtDrawingKind.IsoscelesTriangle:
            case WinRtDrawingKind.EquilateralTriangle:
            case WinRtDrawingKind.RightTriangle: return "Triangle";
            case WinRtDrawingKind.Rectangle: return "Rectangle";
            case WinRtDrawingKind.Square: return "Square";
            case WinRtDrawingKind.Diamond: return "Diamond";
            case WinRtDrawingKind.Parallelogram: return "Parallelogram";
            case WinRtDrawingKind.Quadrilateral: return "Quadrilateral";
            case WinRtDrawingKind.Trapezoid: return "Trapezoid";
            default: return null;
        }
    }

    private static global::Windows.Foundation.Point[] CopyPoints(
        global::Windows.UI.Input.Inking.Analysis.InkAnalysisInkDrawing drawing)
    {
        var src = drawing.Points;
        if (src == null || src.Count == 0)
            return Array.Empty<global::Windows.Foundation.Point>();
        var arr = new global::Windows.Foundation.Point[src.Count];
        for (int i = 0; i < src.Count; i++)
            arr[i] = src[i];
        return arr;
    }

    private static void BoundsFromPoints(
        IReadOnlyList<global::Windows.Foundation.Point> points, out double width, out double height)
    {
        if (points == null || points.Count == 0)
        {
            width = height = 0;
            return;
        }

        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < points.Count; i++)
        {
            var point = points[i];
            minX = Math.Min(minX, point.X);
            maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y);
            maxY = Math.Max(maxY, point.Y);
        }

        width = Math.Max(0, maxX - minX);
        height = Math.Max(0, maxY - minY);
    }

    private static PointCollection ToPointCollection(IReadOnlyList<global::Windows.Foundation.Point> points)
    {
        var collection = new PointCollection();
        if (points == null)
            return collection;
        for (int i = 0; i < points.Count; i++)
            collection.Add(new Point(points[i].X, points[i].Y));
        return collection;
    }
}