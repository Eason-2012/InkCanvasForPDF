using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace InkCanvasForPDF.Services;

/// <summary>墨迹纠正引擎（对齐 ICA MW_SimulatePressure&amp;InkToShape.cs 的 InkToShapeProcess 语义，
/// 识别后端换成 WinRT）：收笔后把最近几笔交给识别器，若判定为规范图形则用品名几何替换手绘笔画。
/// 每个画布一个实例；识别异步串行、延后到 Background 优先级执行，替换前二次校验笔画仍在画布。</summary>
public sealed class InkToShapeEngine
{
    /// <summary>参与识别的最近笔画数（ICA newStrokes.Count &gt; 4 时丢最早一笔）。</summary>
    private const int RecentStrokeLimit = 4;

    private readonly Func<DrawingAttributes> _getDrawingAttributes;
    private readonly Action<StrokeCollection, StrokeCollection, StrokeCollection> _apply;
    private readonly StrokeCollection _recent = new();
    private readonly List<CircleInfo> _circles = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private StrokeCollection? _batchTarget;

    /// <param name="getDrawingAttributes">取当前默认画笔属性（用于新生成笔画的颜色/粗细/平滑/缩放补偿）。</param>
    /// <param name="apply">命中后由宿主执行替换（含撤销提交门），签名：(目标集合, 待移除笔画, 新笔画)。</param>
    public InkToShapeEngine(
        Func<DrawingAttributes> getDrawingAttributes,
        Action<StrokeCollection, StrokeCollection, StrokeCollection> apply)
    {
        _getDrawingAttributes = getDrawingAttributes;
        _apply = apply;
    }

    /// <summary>全局开关（宿主注入；关闭时完全不识别）。</summary>
    public bool Enabled { get; set; }

    /// <summary>相似度门槛（宿主注入 = 设置滑块百分比 / 100）：规范图形与原手绘墨迹的相似度达到该值才执行替换；值越大越严格。</summary>
    public double SimilarityThreshold { get; set; } = 0.75;

    /// <summary>停顿强制识别相似度门槛（与普通识别 <see cref="SimilarityThreshold"/> 相互独立；宿主注入 = 停顿灵敏度滑块 / 100）。</summary>
    public double PauseSimilarityThreshold { get; set; } = 0.75;

    /// <summary>显示缩放：尺寸阈值（75/100）保持屏幕像素口径恒定——门槛 = 常量 ÷ DisplayScale
    /// （放大时页面门槛变小，屏幕严格度不变）；画板无整层缩放，恒 1。</summary>
    public double DisplayScale { get; set; } = 1.0;

    /// <summary>收笔入口（宿主在笔画收集事件里调用）。</summary>
    public void OnStrokeCollected(Stroke stroke, StrokeCollection target)
    {
        if (!Enabled)
            return;
        if (stroke.DrawingAttributes.IsHighlighter)
            return;   // 荧光笔无墨迹纠正（对齐 ICA 页签划分）
        if (!target.Contains(stroke))
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        // 延后一拍：识别/替换不与抬笔瞬间的输入与布局抢 UI 线程
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = ProcessAsync(stroke, target)));
    }

    /// <summary>停顿强制识别：对书写中停顿而提前提交的笔画按 <see cref="PauseSimilarityThreshold"/> 判定并替换。
    /// 与正常收笔识别共用同一批窗口/串行门/替换回调，但<b>不检查 <see cref="Enabled"/></b>——
    /// 停顿识别是独立开关，不受墨迹纠正总开关影响。正常识别排在其后（Background 优先级）：
    /// 本方法命中则正常识别因笔画已不在画布自然放弃；未命中则正常识别兜底，两条路径互不冲突。</summary>
    public async Task<bool> TryPauseCorrectAsync(Stroke stroke, StrokeCollection target)
    {
        if (stroke.DrawingAttributes.IsHighlighter)
            return false;   // 荧光笔无墨迹纠正（对齐正常路径）
        if (!target.Contains(stroke))
            return false;

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!ReferenceEquals(target, _batchTarget))
            {
                _batchTarget = target;
                _recent.Clear();
                _circles.Clear();
            }

            // 正常收笔路径可能已把本笔加入窗口（Contains 防重复）；剔除已不在画布上的旧笔画
            if (!_recent.Contains(stroke))
                _recent.Add(stroke);
            Prune(target);
            if (!_recent.Contains(stroke))
                return false;

            var match = await WinRtInkShapeRecognizer.RecognizeAsync(_recent, stroke).ConfigureAwait(true);
            if (match == null || !ReferenceEquals(target, _batchTarget))
            {
                ConsoleLog.Write("停顿墨迹纠正", match == null ? "未识别" : "目标画布已切换，放弃本轮");
                return false;
            }

            var replacement = BuildReplacement(match);
            if (replacement == null || replacement.Count == 0)
            {
                ConsoleLog.Write("停顿墨迹纠正", $"替换为空 ({match.ShapeName})");
                return false;
            }

            double similarity = ComputeSimilarity(match, replacement);
            if (similarity < PauseSimilarityThreshold)
            {
                ConsoleLog.Write("停顿墨迹纠正", $"相似度不足: {similarity:F2} < {PauseSimilarityThreshold:F2} ({match.ShapeName})");
                return false;
            }

            // 识别期间用户可能已擦除/撤销：任一相关笔画已不在画布则放弃本轮
            if (!target.Contains(stroke))
            {
                ConsoleLog.Write("停顿墨迹纠正", "笔画已不在画布，放弃本轮");
                return false;
            }
            foreach (Stroke matched in match.Strokes)
            {
                if (!target.Contains(matched))
                {
                    ConsoleLog.Write("停顿墨迹纠正", "关联笔画已不在画布，放弃本轮");
                    return false;
                }
            }

            _apply(target, match.Strokes, replacement);
            ConsoleLog.Write("停顿墨迹纠正",
                $"替换: {match.ShapeName}, 相似度={similarity:F2}, 移除={match.Strokes.Count}, 新增={replacement.Count}");
            _recent.Clear();
            Prune(target);
            return true;
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("停顿墨迹纠正", $"停顿识别异常: {ex}");
            return false;   // 停顿强制识别属书写增强：任何异常放弃本轮，保留手绘笔画
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>停顿强制识别·试识别版：用<b>不入画布</b>的临时笔画先判定，只有命中才回调 <paramref name="commitReal"/>
    /// 真正断笔提交在绘笔画并替换为规范图形；未命中（未识别/尺寸不达标/相似度不足/异常）返回 false 且不发生任何
    /// 提交——宿主笔迹保持连续不断。与 <see cref="TryPauseCorrectAsync"/> 共用同一批窗口/串行门/替换回调，
    /// 不检查 <see cref="Enabled"/>（停顿识别是独立开关）。</summary>
    /// <param name="provisional">由宿主在绘点集构建的临时笔画（不入画布）。</param>
    /// <param name="commitReal">命中后提交真实在绘笔画，返回已入 <paramref name="target"/> 的笔画；null = 放弃本轮
    /// （如停顿后已抬笔/模式已切，抬笔走正常收笔识别兜底）。</param>
    public async Task<bool> TryPauseCorrectProvisionalAsync(Stroke provisional, StrokeCollection target, Func<Stroke?> commitReal)
    {
        if (provisional.DrawingAttributes.IsHighlighter)
            return false;   // 荧光笔无墨迹纠正（对齐正常路径）

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!ReferenceEquals(target, _batchTarget))
            {
                _batchTarget = target;
                _recent.Clear();
                _circles.Clear();
            }
            Prune(target);

            // 本地窗口：最近笔画（仍在校准目标上的）+ 临时笔画；不改共享 _recent（临时笔画不在画布上，会被 Prune 剔除）
            var window = new StrokeCollection();
            foreach (Stroke s in _recent)
            {
                if (target.Contains(s))
                    window.Add(s);
            }
            window.Add(provisional);

            var match = await WinRtInkShapeRecognizer.RecognizeAsync(window, provisional).ConfigureAwait(true);
            if (match == null)
            {
                // 先 WinRT 后直线：未命中封闭图形时对临时笔画做直线拉直检测（隶属墨迹纠正，跟随停顿识别链路）
                double lineScale = DisplayScale > 0 ? DisplayScale : 1.0;
                match = StraightLineDetector.TryDetectLine(provisional, 100 / lineScale);
            }
            if (match == null || !ReferenceEquals(target, _batchTarget))
            {
                ConsoleLog.Write("停顿墨迹纠正", match == null ? "未识别" : "目标画布已切换，放弃本轮");
                return false;
            }

            var replacement = BuildReplacement(match);
            if (replacement == null || replacement.Count == 0)
            {
                ConsoleLog.Write("停顿墨迹纠正", $"替换为空 ({match.ShapeName})");
                return false;
            }

            double similarity = ComputeSimilarity(match, replacement);
            if (similarity < PauseSimilarityThreshold)
            {
                ConsoleLog.Write("停顿墨迹纠正", $"相似度不足: {similarity:F2} < {PauseSimilarityThreshold:F2} ({match.ShapeName})");
                return false;
            }

            // 命中：此刻才断笔提交真实笔画
            Stroke? real;
            try
            {
                real = commitReal();
            }
            catch (Exception ex)
            {
                ConsoleLog.Write("停顿墨迹纠正", $"断笔提交异常: {ex}");
                return false;   // 提交失败保留在绘笔画，交回宿主管线
            }
            if (real == null || !target.Contains(real))
            {
                ConsoleLog.Write("停顿墨迹纠正", "断笔提交失败: 无有效笔画或已不在画布");
                return false;
            }

            // 待移除集合：match.Strokes 里的临时笔画换成真实笔画；真实笔画缺失时补入（防墨迹与图形重叠残留）
            var toRemove = new StrokeCollection();
            foreach (Stroke matched in match.Strokes)
            {
                if (!ReferenceEquals(matched, provisional) && target.Contains(matched))
                    toRemove.Add(matched);
            }
            if (!toRemove.Contains(real))
                toRemove.Add(real);

            _apply(target, toRemove, replacement);
            ConsoleLog.Write("停顿墨迹纠正",
                $"试识别替换: {match.ShapeName}, 相似度={similarity:F2}, 移除={toRemove.Count}, 新增={replacement.Count}");
            _recent.Clear();
            Prune(target);
            return true;
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("停顿墨迹纠正", $"停顿试识别异常: {ex}");
            return false;   // 停顿强制识别属书写增强：任何异常放弃本轮，保留手绘笔画
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ProcessAsync(Stroke stroke, StrokeCollection target)
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!Enabled)
                return;

            if (!ReferenceEquals(target, _batchTarget))
            {
                // 换页/换画布：旧的最近笔画与圆形记忆都失效
                _batchTarget = target;
                _recent.Clear();
                _circles.Clear();
            }

            _recent.Add(stroke);
            if (_recent.Count > RecentStrokeLimit)
                _recent.RemoveAt(0);
            Prune(target);
            if (!_recent.Contains(stroke))
                return;

            var match = await WinRtInkShapeRecognizer.RecognizeAsync(_recent, stroke).ConfigureAwait(true);
            if (match == null)
            {
                // 先 WinRT 后直线：未命中封闭图形时对刚收笔这一笔做直线拉直检测（隶属墨迹纠正，跟随 Enabled 开关）
                ConsoleLog.Write("抬笔墨迹纠正", "WinRT未命中 → 直线检测");
                double lineScale = DisplayScale > 0 ? DisplayScale : 1.0;
                match = StraightLineDetector.TryDetectLine(stroke, 100 / lineScale);
            }
            if (match == null || !Enabled || !ReferenceEquals(target, _batchTarget))
                return;

            var replacement = BuildReplacement(match);
            if (replacement == null || replacement.Count == 0)
            {
                ConsoleLog.Write("抬笔墨迹纠正", $"替换为空 ({match.ShapeName})");
                return;
            }

            // 灵敏度门槛：规范图形与原手绘墨迹相似度达标才替换（滑块百分比即门槛，越大越严格）
            double similarity = ComputeSimilarity(match, replacement);
            if (similarity < SimilarityThreshold)
            {
                ConsoleLog.Write("抬笔墨迹纠正", $"相似度不足: {similarity:F2} < {SimilarityThreshold:F2} ({match.ShapeName})");
                return;
            }

            // 识别期间用户可能已擦除/撤销：任一相关笔画已不在画布则放弃本轮
            if (!target.Contains(stroke))
                return;
            foreach (Stroke matched in match.Strokes)
            {
                if (!target.Contains(matched))
                    return;
            }

            ConsoleLog.Write("抬笔墨迹纠正",
                $"替换: {match.ShapeName}, 相似度={similarity:F2}, 移除={match.Strokes.Count}, 新增={replacement.Count}");
            _apply(target, match.Strokes, replacement);

            _recent.Clear();
            Prune(target);
        }
        catch (Exception ex)
        {
            // 墨迹纠正属书写增强：任何异常一律放弃本轮，保留手绘笔画
            ConsoleLog.Write("抬笔墨迹纠正", $"收笔识别异常: {ex}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>剔除已不在画布上的最近笔画与圆形记忆（ICA 进批前清理等价）。</summary>
    private void Prune(StrokeCollection target)
    {
        for (int i = _recent.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(_recent[i]))
                _recent.RemoveAt(i);
        }

        for (int i = _circles.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(_circles[i].Stroke))
                _circles.RemoveAt(i);
        }
    }

    // ══════════ 相似度门槛（灵敏度滑块）══════════

    /// <summary>规范图形与原手绘墨迹的相似度：1 −（墨迹点到图形轮廓的平均距离 ÷ 图形半对角线），clamp 0~1。
    /// 轮廓按步长采样至约 128 点；纯浮点运算（约 10⁴ 次距离计算），微秒级，远小于 WinRT 识别开销。</summary>
    private static double ComputeSimilarity(InkShapeMatch match, StrokeCollection replacement)
    {
        // 目标轮廓采样（replacement 即将上画的规范图形，其点集就是轮廓）
        var outline = new List<Point>();
        foreach (Stroke stroke in replacement)
        {
            var points = stroke.StylusPoints;
            if (points == null || points.Count == 0)
                continue;
            int step = Math.Max(1, points.Count / 128);
            for (int i = 0; i < points.Count; i += step)
                outline.Add(points[i].ToPoint());
        }
        if (outline.Count == 0)
            return 1.0;   // 无法比对时不拦截（理论上规范图形恒有点集）

        double halfDiagonal = Math.Sqrt(match.Width * match.Width + match.Height * match.Height) / 2.0;
        if (halfDiagonal <= 0)
            return 1.0;

        double total = 0;
        int count = 0;
        foreach (Stroke stroke in match.Strokes)
        {
            var points = stroke.StylusPoints;
            if (points == null || points.Count == 0)
                continue;
            foreach (StylusPoint stylusPoint in points)
            {
                var p = stylusPoint.ToPoint();
                double minSq = double.MaxValue;
                foreach (var o in outline)
                {
                    double dx = p.X - o.X;
                    double dy = p.Y - o.Y;
                    double distSq = dx * dx + dy * dy;
                    if (distSq < minSq)
                        minSq = distSq;
                }
                total += Math.Sqrt(minSq);
                count++;
            }
        }
        if (count == 0)
            return 1.0;

        return Math.Clamp(1.0 - total / count / halfDiagonal, 0.0, 1.0);
    }

    // ══════════ 形状生成（ICA InkToShapeProcess 各分支等价）══════════

    private StrokeCollection? BuildReplacement(InkShapeMatch match)
    {
        double scale = DisplayScale > 0 ? DisplayScale : 1.0;
        // 几何形状必须关闭墨迹平滑：FitToCurve 会把矩形/三角这类少量点的闭合几何拟合成圆角曲线
        // （对齐形状绘制 MW_ShapeDrawing 的约定）。必须先 Clone 再改——取到的是宿主共享实例，
        // 就地修改会把正常手写笔迹的平滑一起关掉。
        var attributes = _getDrawingAttributes().Clone();
        attributes.FitToCurve = false;
        return match.ShapeName switch
        {
            "Circle" => BuildCircle(match, attributes, scale),
            "Ellipse" => BuildEllipse(match, attributes, scale),
            "Triangle" => BuildTriangle(match, attributes, scale),
            "Rectangle" or "Square" or "Diamond" or "Parallelogram" or "Quadrilateral" or "Trapezoid"
                => BuildQuadrilateral(match, attributes, scale),
            "Line" => BuildLine(match, attributes, scale),
            _ => null
        };
    }

    /// <summary>圆：直径够大才纠正；与已纠正的圆做同心/内外切吸附（ICA InkToShapeProcess 圆分支）。</summary>
    private StrokeCollection? BuildCircle(InkShapeMatch match, DrawingAttributes attributes, double scale)
    {
        if (match.Width <= 75 / scale)
        {
            ConsoleLog.Write("墨迹纠正", $"圆太小不纠正: 直径={match.Width:F0} ≤ 门槛={(75 / scale):F0}");
            return null;
        }
        ConsoleLog.Write("墨迹纠正",
            $"圆过门: 直径={match.Width:F0}, 门槛={(75 / scale):F0}, scale={scale:F2}");

        var centroid = match.Center;
        foreach (var circle in _circles)
        {
            // 同心圆：直接沿用已有圆心
            if (Math.Abs(centroid.X - circle.Center.X) / match.Width < 0.12 &&
                Math.Abs(centroid.Y - circle.Center.Y) / match.Width < 0.12)
            {
                centroid = circle.Center;
                break;
            }

            double offsetX = centroid.X - circle.Center.X;
            double offsetY = centroid.Y - circle.Center.Y;
            double distance = Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
            double sinTheta = offsetY / distance;
            double cosTheta = offsetX / distance;

            // 外切圆：两圆外边缘相切
            double delta = match.Width / 2.0 + circle.R - distance;
            if (Math.Abs(delta) / match.Width < 0.1)
                centroid = new Point(centroid.X + delta * cosTheta, centroid.Y + delta * sinTheta);

            // 内切圆：小圆内切于大圆
            delta = Math.Abs(circle.R - match.Width / 2.0) - distance;
            if (Math.Abs(delta) / match.Width < 0.1)
                centroid = new Point(centroid.X + delta * cosTheta, centroid.Y + delta * sinTheta);
        }

        var stroke = CreateEllipseStroke(
            new Point(centroid.X - match.Width / 2, centroid.Y - match.Height / 2),
            new Point(centroid.X + match.Width / 2, centroid.Y + match.Height / 2),
            attributes);
        if (stroke == null)
            return null;

        _circles.Add(new CircleInfo(centroid, match.Width / 2.0, stroke));
        return new StrokeCollection { stroke };
    }

    /// <summary>椭圆：按长短半轴与圆心生成，并按长短轴方向旋转（ICA InkToShapeProcess 椭圆分支）。</summary>
    private StrokeCollection? BuildEllipse(InkShapeMatch match, DrawingAttributes attributes, double scale)
    {
        var points = match.HotPoints;
        if (points.Count < 4)
            return null;
        if (match.Width <= 75 / scale && !(match.Height > 75 / scale && points.Count == 4))
        {
            ConsoleLog.Write("墨迹纠正",
                $"椭圆太小不纠正: 宽={match.Width:F0}, 高={match.Height:F0}, 门槛={(75 / scale):F0}");
            return null;
        }
        ConsoleLog.Write("墨迹纠正",
            $"椭圆过门: 宽={match.Width:F0}, 高={match.Height:F0}, 门槛={(75 / scale):F0}, scale={scale:F2}");

        double halfMajor = GetDistance(points[0], points[2]) / 2;   // 长半轴
        double halfMinor = GetDistance(points[1], points[3]) / 2;   // 短半轴
        if (halfMajor < halfMinor)
            (halfMajor, halfMinor) = (halfMinor, halfMajor);

        var centroid = new Point((points[0].X + points[2].X) / 2, (points[0].Y + points[2].Y) / 2);
        var iniP = new Point(centroid.X - match.Width / 2, centroid.Y - match.Height / 2);
        var endP = new Point(centroid.X + match.Width / 2, centroid.Y + match.Height / 2);

        if (halfMajor > 0)
        {
            foreach (var circle in _circles)
            {
                // 同心椭圆
                if (Math.Abs(centroid.X - circle.Center.X) / halfMajor < 0.2 &&
                    Math.Abs(centroid.Y - circle.Center.Y) / halfMajor < 0.2)
                {
                    centroid = circle.Center;
                    iniP = new Point(centroid.X - match.Width / 2, centroid.Y - match.Height / 2);
                    endP = new Point(centroid.X + match.Width / 2, centroid.Y + match.Height / 2);

                    // 再判断与圆相切（长半轴对齐圆半径）
                    if (Math.Abs(halfMajor - circle.R) / halfMajor < 0.2)
                    {
                        if (match.Width >= match.Height)
                        {
                            iniP.X = centroid.X - circle.R;
                            endP.X = centroid.X + circle.R;
                            iniP.Y = centroid.Y - halfMinor;
                            endP.Y = centroid.Y + halfMinor;
                        }
                        else
                        {
                            iniP.Y = centroid.Y - circle.R;
                            endP.Y = centroid.Y + circle.R;
                            iniP.X = centroid.X - halfMajor;
                            endP.X = centroid.X + halfMajor;
                        }
                    }
                    break;
                }
            }
        }

        // 纠正垂直与水平关系（ICA FixPointsDirection）
        var fixedPoints = FixPointsDirection(points[0], points[2]);
        points[0] = fixedPoints[0];
        points[2] = fixedPoints[1];
        fixedPoints = FixPointsDirection(points[1], points[3]);
        points[1] = fixedPoints[0];
        points[3] = fixedPoints[1];

        var stroke = CreateEllipseStroke(iniP, endP, attributes);
        if (stroke == null)
            return null;

        // 长短轴方向：用 Atan2 求角度（ICA 用斜率 tan，±90° 与除零会失真）
        double theta = Math.Atan2(points[2].Y - points[0].Y, points[2].X - points[0].X);
        var transform = new Matrix();
        transform.RotateAt(theta * 180.0 / Math.PI, centroid.X, centroid.Y);
        stroke.Transform(transform, false);

        return new StrokeCollection { stroke };
    }

    /// <summary>三角形：跨度够大且有 3 个角点才纠正（ICA InkToShapeProcess 三角分支）。</summary>
    private StrokeCollection? BuildTriangle(InkShapeMatch match, DrawingAttributes attributes, double scale)
    {
        var points = match.HotPoints;
        if (points.Count != 3)
            return null;

        double spanX = Math.Max(Math.Max(points[0].X, points[1].X), points[2].X) -
                       Math.Min(Math.Min(points[0].X, points[1].X), points[2].X);
        double spanY = Math.Max(Math.Max(points[0].Y, points[1].Y), points[2].Y) -
                       Math.Min(Math.Min(points[0].Y, points[1].Y), points[2].Y);
        if (spanX < 100 / scale && spanY < 100 / scale)
        {
            ConsoleLog.Write("墨迹纠正",
                $"三角形太小不纠正: 跨度={spanX:F0}×{spanY:F0}, 门槛={(100 / scale):F0}");
            return null;
        }
        ConsoleLog.Write("墨迹纠正",
            $"三角形过门: 跨度={spanX:F0}×{spanY:F0}, 门槛={(100 / scale):F0}, scale={scale:F2}");

        var fixedPoints = FixPointsDirection(points[0], points[1]);
        points[0] = fixedPoints[0];
        points[1] = fixedPoints[1];
        fixedPoints = FixPointsDirection(points[0], points[2]);
        points[0] = fixedPoints[0];
        points[2] = fixedPoints[1];
        fixedPoints = FixPointsDirection(points[1], points[2]);
        points[1] = fixedPoints[0];
        points[2] = fixedPoints[1];

        var pointList = points.ToList();
        pointList.Add(points[0]);   // 闭合回起点：三角形必须显式回到首点，否则最后一条边不绘制
        // 不注入假压感：每点压力默认 0.5（中性宽度）→ 图形等宽无笔锋
        return Wrap(new Stroke(new StylusPointCollection(pointList))
        {
            DrawingAttributes = attributes.Clone()
        });
    }

    /// <summary>矩形/正方形/菱形/平行四边形/四边形/梯形：跨度够大且有 4 个角点才纠正（ICA InkToShapeProcess 四边形分支）。</summary>
    private StrokeCollection? BuildQuadrilateral(InkShapeMatch match, DrawingAttributes attributes, double scale)
    {
        var points = match.HotPoints;
        if (points.Count != 4)
            return null;

        double spanX = Math.Max(Math.Max(Math.Max(points[0].X, points[1].X), points[2].X), points[3].X) -
                       Math.Min(Math.Min(Math.Min(points[0].X, points[1].X), points[2].X), points[3].X);
        double spanY = Math.Max(Math.Max(Math.Max(points[0].Y, points[1].Y), points[2].Y), points[3].Y) -
                       Math.Min(Math.Min(Math.Min(points[0].Y, points[1].Y), points[2].Y), points[3].Y);
        if (spanX < 100 / scale && spanY < 100 / scale)
        {
            ConsoleLog.Write("墨迹纠正",
                $"四边形太小不纠正: 跨度={spanX:F0}×{spanY:F0}, 门槛={(100 / scale):F0}");
            return null;
        }
        ConsoleLog.Write("墨迹纠正",
            $"四边形过门: 跨度={spanX:F0}×{spanY:F0}, 门槛={(100 / scale):F0}, scale={scale:F2}");

        var fixedPoints = FixPointsDirection(points[0], points[1]);
        points[0] = fixedPoints[0];
        points[1] = fixedPoints[1];
        fixedPoints = FixPointsDirection(points[1], points[2]);
        points[1] = fixedPoints[0];
        points[2] = fixedPoints[1];
        fixedPoints = FixPointsDirection(points[2], points[3]);
        points[2] = fixedPoints[0];
        points[3] = fixedPoints[1];
        fixedPoints = FixPointsDirection(points[3], points[0]);
        points[3] = fixedPoints[0];
        points[0] = fixedPoints[1];

        var pointList = points.ToList();
        pointList.Add(points[0]);   // 闭合
        // 不注入假压感：每点压力默认 0.5（中性宽度）→ 图形等宽无笔锋
        return Wrap(new Stroke(new StylusPointCollection(pointList))
        {
            DrawingAttributes = attributes.Clone()
        });
    }

    /// <summary>直线：沿首尾两端点等距插值生成稠密点集——两点直线会让 ComputeSimilarity
    /// 只采到 2 个轮廓点（相似度恒 ≈0.5 永远过不了门槛），稠密插值使轮廓近似连续线段。</summary>
    private StrokeCollection? BuildLine(InkShapeMatch match, DrawingAttributes attributes, double scale)
    {
        var points = match.HotPoints;
        if (points.Count != 2)
            return null;

        var start = points[0];
        var end = points[1];
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 100 / scale)
        {
            ConsoleLog.Write("墨迹纠正", $"直线太短不纠正: 长度={length:F0} ≤ 门槛={(100 / scale):F0}");
            return null;   // 最短长度复检（对齐三角/四边形跨度惯例）
        }
        ConsoleLog.Write("墨迹纠正",
            $"直线过门: 长度={length:F0}, 门槛={(100 / scale):F0}, scale={scale:F2}");

        // 插值间距 = max(4px, 长度/128) → 点数约 64~128，正好喂饱 ComputeSimilarity 的 ~128 点轮廓采样
        double spacing = Math.Max(4.0, length / 128.0);
        int segments = Math.Max(1, (int)Math.Ceiling(length / spacing));
        var linePoints = new List<Point>(segments + 1);
        for (int i = 0; i <= segments; i++)
        {
            double t = (double)i / segments;
            linePoints.Add(new Point(start.X + dx * t, start.Y + dy * t));
        }

        // 不注入假压感：每点压力默认 0.5（中性宽度）→ 直线等宽无笔锋（对齐三角/四边形约定）
        return Wrap(new Stroke(new StylusPointCollection(linePoints))
        {
            DrawingAttributes = attributes.Clone()
        });
    }

    private static StrokeCollection Wrap(Stroke stroke) => new() { stroke };

    private static Stroke? CreateEllipseStroke(Point iniP, Point endP, DrawingAttributes attributes)
    {
        var pointList = GenerateEllipseGeometry(iniP, endP);
        if (pointList.Count == 0)
            return null;
        return new Stroke(new StylusPointCollection(pointList))
        {
            DrawingAttributes = attributes.Clone()
        };
    }

    // ══════════ 几何辅助（ICA MW_ShapeDrawing.cs 同名辅助等价）══════════

    private static double GetDistance(Point point1, Point point2)
    {
        return Math.Sqrt((point1.X - point2.X) * (point1.X - point2.X) +
                         (point1.Y - point2.Y) * (point1.Y - point2.Y));
    }

    /// <summary>把近似水平/垂直的边拉正（ICA FixPointsDirection 原样）。</summary>
    private static Point[] FixPointsDirection(Point p1, Point p2)
    {
        if (Math.Abs(p1.X - p2.X) / Math.Abs(p1.Y - p2.Y) > 8)
        {
            // 水平
            double x = Math.Abs(p1.Y - p2.Y) / 2;
            if (p1.Y > p2.Y)
            {
                p1.Y -= x;
                p2.Y += x;
            }
            else
            {
                p1.Y += x;
                p2.Y -= x;
            }
        }
        else if (Math.Abs(p1.Y - p2.Y) / Math.Abs(p1.X - p2.X) > 8)
        {
            // 垂直
            double x = Math.Abs(p1.X - p2.X) / 2;
            if (p1.X > p2.X)
            {
                p1.X -= x;
                p2.X += x;
            }
            else
            {
                p1.X += x;
                p2.X -= x;
            }
        }

        return new[] { p1, p2 };
    }

    /// <summary>椭圆点集（ICA GenerateEllipseGeometry 等价，取整圈）。</summary>
    private static List<Point> GenerateEllipseGeometry(Point st, Point ed)
    {
        double a = 0.5 * (ed.X - st.X);
        double b = 0.5 * (ed.Y - st.Y);
        var pointList = new List<Point>();
        for (double r = 0; r <= 2 * Math.PI; r += 0.01)
            pointList.Add(new Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
        return pointList;
    }

    /// <summary>已纠正的圆（ICA List&lt;Circle&gt; 等价）。</summary>
    private sealed class CircleInfo
    {
        public CircleInfo(Point center, double r, Stroke stroke)
        {
            Center = center;
            R = r;
            Stroke = stroke;
        }

        public Point Center { get; set; }
        public double R { get; }
        public Stroke Stroke { get; }
    }
}