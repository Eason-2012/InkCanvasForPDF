using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Input.StylusPlugIns;
using System.Windows.Media;
using System.Windows.Threading;

namespace InkCanvasForPDF.Services;

/// <summary>时间笔锋的一个采样点（笔线程写入、UI 线程读取，两者都须持有控制器锁）。</summary>
internal struct TimePressureSample
{
    public double X;   // 页面坐标（与 Stroke 同一空间，外层整层 ScaleTransform 负责屏幕缩放）
    public double Y;
    public double T;   // 相对落笔时刻的毫秒数
    public double P;   // 硬件压力（鼠标/触摸恒 0.5）
}

/// <summary>时间笔锋输入插件：在笔线程按包采集「坐标 + 时间戳 + 硬件压力」写入控制器带锁缓冲。
/// 仅在控制器标记为已武装（时间笔锋 + 画笔模式 + 非荧光笔）时采集。</summary>
internal sealed class TimePressurePlugIn : StylusPlugIn
{
    private readonly TimePressureController _controller;
    private bool _down;

    public TimePressurePlugIn(TimePressureController controller) => _controller = controller;

    protected override void OnStylusDown(RawStylusInput rawStylusInput)
    {
        if (!_controller.CollectArmed || _down)
            return;
        _down = true;
        _controller.BeginStroke();
        _controller.AppendSamples(rawStylusInput.GetStylusPoints());
    }

    protected override void OnStylusMove(RawStylusInput rawStylusInput)
    {
        if (!_down || !_controller.StrokeInProgress)
            return;
        _controller.AppendSamples(rawStylusInput.GetStylusPoints());
    }

    protected override void OnStylusUp(RawStylusInput rawStylusInput)
    {
        if (!_down)
            return;
        _down = false;
        if (!_controller.StrokeInProgress)
            return;
        _controller.AppendSamples(rawStylusInput.GetStylusPoints());
        _controller.MarkStrokeUp();   // 记录真实抬笔时刻（定格时间基准），必须在 NotifyWhenProcessed 之前
        // 抬笔后在 UI 线程补画最后一帧，使湿墨终态与随后的干墨尽量一致（减少替换瞬间的闪变）
        rawStylusInput.NotifyWhenProcessed(this);
    }

    protected override void OnStylusUpProcessed(object? callbackData, bool targetVerified)
        => _controller.RenderFinalFrame();
}

/// <summary>时间笔锋叠加层：承载「已定型段」（追加式，每段只画一次）与「生长段」（每帧重绘）两层视觉。
/// 位于页面 Grid 中 InkCanvas 之后 → 天然覆盖在干墨之上；不参与命中测试以免挡住墨迹交互。</summary>
public sealed class TimePressureOverlay : FrameworkElement
{
    private readonly ContainerVisual _root = new();
    private readonly DrawingVisual _growing = new();
    private readonly List<DrawingVisual> _committed = new();

    public TimePressureOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;   // 湿墨裁剪到本页：InkCanvas 自身 ClipToBounds=true 会裁干墨，叠加层若不裁，越界湿墨会盖到相邻页面（松手后随清空消失，形成"跨画布又消失"的假象）
        AddVisualChild(_root);
        _root.Children.Add(_growing);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _root;

    /// <summary>跟随宿主铺满页面（默认 MeasureOverride 返回 0，会让 Auto 尺寸的宿主量不出高度）。</summary>
    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
               double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

    /// <summary>追加一个用于承载「已定型段」的视觉（插在生长层之下，保证生长段永远在最上）。</summary>
    internal DrawingVisual AddCommittedVisual()
    {
        var visual = new DrawingVisual();
        _root.Children.Insert(_root.Children.Count - 1, visual);
        _committed.Add(visual);
        return visual;
    }

    /// <summary>打开生长层的绘制上下文（每帧调用会清空并重绘生长段）。</summary>
    internal DrawingContext OpenGrowing() => _growing.RenderOpen();

    /// <summary>清空两层内容（原地清空，不增删视觉树，避免布局抖动）。</summary>
    public void Clear()
    {
        foreach (var visual in _committed)
        {
            _root.Children.Remove(visual);
            visual.RenderOpen().Close();
        }
        _committed.Clear();
        _growing.RenderOpen().Close();
    }
}

/// <summary>时间笔锋的自定义动态渲染器：武装状态下不调用 base（屏蔽 InkCanvas 默认湿墨，改由叠加层按时间生长绘制）；
/// 未武装时原样放行，点集/速率/关闭三种笔锋与真实压感笔的湿墨行为完全不变。</summary>
internal sealed class TimePressureDynamicRenderer : DynamicRenderer
{
    private readonly TimePressureController _controller;

    public TimePressureDynamicRenderer(TimePressureController controller) => _controller = controller;

    protected override void OnDraw(DrawingContext drawingContext, StylusPointCollection stylusPoints, Geometry geometry, Brush fillBrush)
    {
        if (_controller.CollectArmed)
            return;
        base.OnDraw(drawingContext, stylusPoints, geometry, fillBrush);
    }
}

/// <summary>时间笔锋控制器（每个页面画布一个）：
/// 采集点在笔线程写入、UI 线程按帧渲染生长动画、抬笔时把各点定格压力写回 Stroke。</summary>
public sealed class TimePressureController
{
    private const double TipWidthPx = 0;      // 实验：起始/末端最低宽度 = 精确 0（原 0.1；用户实测对比收笔末端形态）
    private const int CommitOverlap = 1;      // 提交段向前重叠的点数：折线几何逐段确定，重叠区两次绘制完全重合，1 点即可保证段间路径连续
    private const int CommitBatchPoints = 64; // 攒批提交阈值：成熟点攒够该数量才追加一个 committed 视觉（长笔画视觉数 ≈ 总点数/64，而非 ≈ 帧数）

    /// <summary>墨迹点长满时长（ms）：由设置「时间笔锋墨迹点长满时间」驱动（MainWindow 同步），数字越大笔锋越明显。</summary>
    public double GrowMs { get; set; } = 50.0;

    private readonly InkCanvas _canvas;
    private readonly TimePressureOverlay _overlay = new();
    private readonly object _sync = new();
    private readonly List<TimePressureSample> _samples = new();
    private readonly Stopwatch _clock = new();

    private volatile bool _collectArmed;      // UI 线程写、笔线程读
    private volatile bool _strokeInProgress;
    private bool _renderingHooked;            // 仅笔画进行中订阅 CompositionTarget.Rendering
    private int _committedBoundary;           // 已提交（冻结进 committed 层）的采样下标（不含），仅 UI 线程访问；
                                              // 每帧快照只取该边界之后的有限窗口（O(CommitBatchPoints+当帧新增)），不随整笔变长
    private double _upElapsedMs;              // 抬笔包到达时刻（MarkStrokeUp 写入）；0 = 未收到抬笔
    private double _lastBatchEndMs;           // 上一批采样的时间戳（笔线程专用，锁内访问）

    public TimePressureController(InkCanvas canvas) => _canvas = canvas;

    public TimePressureOverlay Overlay => _overlay;

    public bool CollectArmed => _collectArmed;

    public bool StrokeInProgress => _strokeInProgress;

    /// <summary>页面缩放（外层整层 ScaleTransform），用于把「屏幕针尖宽度」换算回页面坐标。</summary>
    public double DisplayScale { get; set; } = 1.0;

    /// <summary>「启用压感」开关：关闭时时间笔锋的封顶压力统一为 0.5（与既有归一语义一致）。</summary>
    public bool PressureEnabled { get; set; } = true;

    /// <summary>由 UI 线程在「时间笔锋 + 画笔模式 + 非荧光笔」条件变化时同步；失去武装即中断当前笔画与叠加层。</summary>
    public void SetCollectArmed(bool armed)
    {
        _collectArmed = armed;
        if (!armed)
            AbortStroke();
    }

    /// <summary>中断当前笔画：丢弃采样、退订渲染帧、清空叠加层（模式切换/失捕获/画布卸载等多入口调用）。</summary>
    public void AbortStroke()
    {
        if (_strokeInProgress || _committedBoundary > 0)
            ResetStrokeState();
    }

    // ───────────────────────────── 笔线程采集入口 ─────────────────────────────

    internal void BeginStroke()
    {
        lock (_sync)
        {
            _samples.Clear();
            _strokeInProgress = true;
            _upElapsedMs = 0;
            _lastBatchEndMs = 0;
            _clock.Restart();
        }
        // 视觉与帧订阅只能在 UI 线程动
        var dispatcher = _overlay.Dispatcher;
        if (dispatcher.CheckAccess())
            StartRendering();
        else
            dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(StartRendering));
    }

    internal void AppendSamples(StylusPointCollection points)
    {
        if (points == null || points.Count == 0)
            return;
        lock (_sync)
        {
            if (!_strokeInProgress)
                return;
            // 批次内逐点分摊时间戳：输入管线会成批递交采样（收笔减速段/抬笔包常合并），
            // 若批内所有点共用同一时刻，点龄相同 → 定格压力相同 → 末端宽度平台收不到针尖。
            // 将 N 个点分布在 [上一批时刻, 本批时刻] 区间上，每点获得独立单调的诞生时刻。
            double tEnd = _clock.Elapsed.TotalMilliseconds;
            double tStart = _lastBatchEndMs;
            if (tEnd <= tStart)
                tEnd = tStart + 0.01;   // 同批同刻兜底：保持每点时间戳严格递增
            int n = points.Count;
            for (int i = 0; i < n; i++)
            {
                double t = tStart + (tEnd - tStart) * (i + 1) / n;
                _samples.Add(new TimePressureSample { X = points[i].X, Y = points[i].Y, T = t, P = points[i].PressureFactor });
            }
            _lastBatchEndMs = tEnd;
        }
    }

    /// <summary>记录抬笔包到达时刻（笔线程 OnStylusUp 调用，紧跟真实物理抬笔）。
    /// 定格必须以该时刻为"现在"——若用 UI 线程 StrokeCollected 的执行时刻，
    /// 墨水管线延迟（30~100ms+）会灌大末点点龄，导致快速甩笔也定格成全宽圆点。</summary>
    internal void MarkStrokeUp()
    {
        lock (_sync)
        {
            if (_strokeInProgress)
                _upElapsedMs = _clock.Elapsed.TotalMilliseconds;
        }
    }

    // ───────────────────────────── UI 线程渲染 ─────────────────────────────

    private void StartRendering()
    {
        _overlay.Clear();
        _committedBoundary = 0;
        if (_renderingHooked)
            return;
        CompositionTarget.Rendering += OnFrame;
        _renderingHooked = true;
    }

    private void OnFrame(object? sender, EventArgs e) => RenderFrame(useUpTime: false);

    /// <summary>抬笔后在 UI 线程补画最后一帧：以抬笔时刻计算点龄，使湿墨终态与随后的干墨完全一致（无替换跳变）。</summary>
    internal void RenderFinalFrame() => RenderFrame(useUpTime: true);

    private void RenderFrame(bool useUpTime)
    {
        List<TimePressureSample> snapshot;
        int snapFrom;
        double now;
        lock (_sync)
        {
            if (!_strokeInProgress)
            {
                UnhookRendering();
                return;
            }
            // 有限窗口快照：只取「已提交边界 - 重叠点」之后的未提交段——已提交段永不重算，
            // 锁内拷贝与随后的成熟扫描均为 O(窗口)（≈ CommitBatchPoints + 当帧新增），
            // 不再随整笔采样变长（持锁时间短 → 笔线程 AppendSamples 不再被 UI 线程长时间阻塞）。
            snapFrom = Math.Max(0, _committedBoundary - CommitOverlap);
            snapshot = _samples.GetRange(snapFrom, _samples.Count - snapFrom);
            // 定格/末帧以抬笔包时刻为基准（排除 UI 线程处理延迟）；书写中用活时钟
            now = useUpTime && _upElapsedMs > 0 ? _upElapsedMs : _clock.Elapsed.TotalMilliseconds;
        }

        int count = snapshot.Count;
        if (count == 0)
            return;

        // 1) 已长满段（点龄 ≥ GrowMs）：窗口内局部下标从已提交边界起扫描（窗口之前的点必然已成熟/已提交）。
        // 攒够 CommitBatchPoints 才追加一个 committed 视觉——冻结式提交不变（已提交段永不重算，
        // 晃动仍只限制在生长段内），但长笔画的 DrawingVisual 数量从 ≈ 帧数 降为 ≈ 总点数/64。
        // 未提交的成熟点继续画在生长层：age ≥ GrowMs 时公式结果恒等于 cap，与提交后完全一致——攒批无视觉差异。
        int localBoundary = _committedBoundary - snapFrom;
        int localMature = localBoundary;
        while (localMature < count && (now - snapshot[localMature].T) >= GrowMs)
            localMature++;
        int pending = localMature - localBoundary;   // 已成熟未提交的点数
        bool flush = useUpTime ? pending >= 1 : pending >= CommitBatchPoints;
        int growingFrom;
        if (flush)
        {
            // 快照起点即「旧边界 - 重叠点」→ 提交段在窗口内恒为 [0, localMature-1]（含重叠点衔接）。
            var visual = _overlay.AddCommittedVisual();
            using (var dc = visual.RenderOpen())
                DrawStroke(dc, snapshot, 0, localMature - 1, now, capOnly: true);
            _committedBoundary = snapFrom + localMature;
            growingFrom = Math.Max(0, localMature - CommitOverlap);
        }
        else
        {
            growingFrom = Math.Max(0, localBoundary - CommitOverlap);
        }

        // 2) 生长段（重叠点起到窗口末尾）：每帧按各点龄重算压力重绘
        if (count - 1 - growingFrom >= 1)
        {
            using var dc = _overlay.OpenGrowing();
            DrawStroke(dc, snapshot, growingFrom, count - 1, now, capOnly: false);
        }
    }

    private void DrawStroke(DrawingContext dc, List<TimePressureSample> snapshot, int from, int to, double now, bool capOnly)
    {
        if (to - from < 1)
            return;
        double tip = TipPressure();
        var points = new StylusPointCollection();
        for (int i = from; i <= to; i++)
        {
            var sample = snapshot[i];
            double cap = CapPressure(sample);
            double pressure = capOnly
                ? cap
                : tip + (cap - tip) * Math.Min(1.0, Math.Max(0.0, (now - sample.T) / GrowMs));
            points.Add(new StylusPoint(sample.X, sample.Y, (float)pressure));
        }
        new Stroke(points, WetAttributes()).Draw(dc);
    }

    // ───────────────────────────── 抬笔定格 ─────────────────────────────

    /// <summary>抬笔定格：把每个墨迹点的最终压力写回 Stroke（供 StrokeCollected 的 case 3 调用）。
    /// 点龄 = 抬笔时刻 − 该点诞生时刻；点数与采样数不一致时按首尾时间线性插值映射，采样缺失时保持原样。</summary>
    public void FreezeFinalize(Stroke stroke, bool pressureEnabled)
    {
        double now;
        bool hasUpTime;
        List<TimePressureSample> snapshot;
        lock (_sync)
        {
            snapshot = new List<TimePressureSample>(_samples);
            // 以抬笔包时刻为"现在"：UI 线程收到 StrokeCollected 时已隔墨水管线延迟（30~100ms+），
            // 用活时钟会把末点点龄灌大 → 快速甩笔也定格成全宽圆点；无抬笔记录时回退活时钟兜底。
            hasUpTime = _upElapsedMs > 0;
            now = hasUpTime ? _upElapsedMs : _clock.Elapsed.TotalMilliseconds;
        }

        var stylusPoints = stroke.StylusPoints;
        int count = stylusPoints.Count;
        // 未采到任何样本（例如武装状态晚于落笔生效）：不改写压力，保持原样，避免把整条笔画压成针尖细线
        if (count > 0 && snapshot.Count > 0)
        {
            double tip = TipPressure();
            bool haveSamples = snapshot.Count >= 2;
            double span = haveSamples ? Math.Max(1e-6, snapshot[snapshot.Count - 1].T - snapshot[0].T) : 1e-6;
            double t0 = haveSamples ? snapshot[0].T : 0.0;

            var rebuilt = new StylusPointCollection();
            for (int i = 0; i < count; i++)
            {
                double birth;
                double hardware;
                if (snapshot.Count == count)
                {
                    birth = snapshot[i].T;
                    hardware = snapshot[i].P;
                }
                else if (haveSamples)
                {
                    double ratio = count == 1 ? 0.0 : (double)i / (count - 1);
                    birth = t0 + span * ratio;
                    hardware = snapshot[(int)Math.Round(ratio * (snapshot.Count - 1))].P;
                }
                else
                {
                    birth = 0.0;
                    hardware = 0.5;
                }

                double cap = pressureEnabled ? hardware : 0.5;
                if (double.IsNaN(cap) || cap <= 0)
                    cap = 0.5;
                cap = Math.Max(cap, tip);

                double age = now - birth;
                double pressure = tip + (cap - tip) * Math.Min(1.0, Math.Max(0.0, age / GrowMs));
                rebuilt.Add(new StylusPoint(stylusPoints[i].X, stylusPoints[i].Y, (float)pressure));
            }
            stroke.StylusPoints = rebuilt;
        }

        ResetStrokeState();
    }

    private void ResetStrokeState()
    {
        lock (_sync)
        {
            _samples.Clear();
            _strokeInProgress = false;
            _upElapsedMs = 0;
            _lastBatchEndMs = 0;
            _clock.Reset();
        }
        _committedBoundary = 0;
        UnhookRendering();
        _overlay.Clear();
    }

    private void UnhookRendering()
    {
        if (!_renderingHooked)
            return;
        CompositionTarget.Rendering -= OnFrame;
        _renderingHooked = false;
    }

    // ───────────────────────────── 压力换算 ─────────────────────────────

    /// <summary>起始针尖压力：把「0.1 屏幕 DIP」换算成页面坐标宽度，再折算为压力系数。</summary>
    private double TipPressure()
    {
        // 直接现读画布 DA：画板（Board）就地修改同一 DA 实例（颜色/粗细），按实例引用缓存会拿到旧值
        double pageWidth = Math.Max(0.01, _canvas.DefaultDrawingAttributes.Width);
        double tipPageWidth = TipWidthPx / Math.Max(1.0, DisplayScale);
        return 0.5 * tipPageWidth / pageWidth;
    }

    /// <summary>封顶压力 = 该点硬件压力（压感关闭时统一 0.5），且不低于起始针尖。</summary>
    private double CapPressure(TimePressureSample sample)
    {
        double tip = TipPressure();
        double cap = PressureEnabled ? sample.P : 0.5;
        if (double.IsNaN(cap) || cap <= 0)
            cap = 0.5;
        return Math.Max(cap, tip);
    }

    /// <summary>湿墨渲染用折线属性：每次从画布 DA 现克隆（颜色/粗细实时跟随，含画板的就地修改），
    /// 并强制 FitToCurve=false——WPF 的 FitToCurve 拟合依赖段首切线，分段拟合会得到互不重合的曲线
    /// （重叠区出现"平滑+原始"双线）；折线几何逐段确定，重叠区完全重合，且追加新点不改变已画部分。
    /// 平滑（用户「墨迹平滑」设置）由干墨在松手后呈现。</summary>
    private DrawingAttributes WetAttributes()
    {
        var wet = _canvas.DefaultDrawingAttributes.Clone();
        wet.FitToCurve = false;
        return wet;
    }
}