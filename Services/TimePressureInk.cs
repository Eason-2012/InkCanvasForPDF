using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// 按 stylus 设备 id 路由到各自会话——多设备（手掌/第二触点/多指）互不串样。
/// 采集设备由控制器判定：时间笔锋/硬笔屏蔽（主设备）或多指自绘（副设备/全部设备）。</summary>
internal sealed class TimePressurePlugIn : StylusPlugIn
{
    private readonly TimePressureController _controller;
    private readonly Dictionary<int, int> _active = new();   // 落笔中的全部设备（含未采集）→ tabletDeviceId

    public TimePressurePlugIn(TimePressureController controller) => _controller = controller;

    protected override void OnStylusDown(RawStylusInput rawStylusInput)
    {
        int id = rawStylusInput.StylusDeviceId;
        if (_active.ContainsKey(id))
        {
            // 同一设备号再次落笔 = 上一次的抬笔事件丢失（会话僵死、叠加层湿墨常驻）：
            // 先丢弃旧会话再开新笔，避免旧湿墨跟着粗细/颜色变化且无法撤销。
            _controller.AbortStroke(id);
            _active.Remove(id);
        }
        _active[id] = rawStylusInput.TabletDeviceId;
        // 未接收集的设备（多指书写关闭时的第二触点/手掌）只登记、不采集——绝不写入他人会话
        if (!_controller.ShouldCollect(id, rawStylusInput.TabletDeviceId))
            return;
        _controller.BeginStroke(id, rawStylusInput.TabletDeviceId);
        _controller.AppendSamples(id, rawStylusInput.GetStylusPoints());
    }

    protected override void OnStylusMove(RawStylusInput rawStylusInput)
    {
        int id = rawStylusInput.StylusDeviceId;
        if (!_active.ContainsKey(id) || !_controller.HasSession(id))
            return;   // 只处理本设备自己的会话
        _controller.AppendSamples(id, rawStylusInput.GetStylusPoints());
    }

    protected override void OnStylusUp(RawStylusInput rawStylusInput)
    {
        int id = rawStylusInput.StylusDeviceId;
        if (!_active.TryGetValue(id, out int tabletId))
            return;
        if (_controller.HasSession(id))
        {
            _controller.AppendSamples(id, rawStylusInput.GetStylusPoints());
            _controller.MarkStrokeUp(id);   // 记录真实抬笔时刻（定格时间基准），必须在 NotifyWhenProcessed 之前
        }
        // 抬笔后在 UI 线程补最后一帧 / 提交自绘笔画（未采集设备也要通知，用于清主设备标记）
        rawStylusInput.NotifyWhenProcessed(new DeviceUpInfo(id, tabletId));
    }

    protected override void OnStylusUpProcessed(object? callbackData, bool targetVerified)
    {
        if (callbackData is not DeviceUpInfo info)
            return;
        _active.Remove(info.DeviceId);
        _controller.OnDeviceUpProcessed(info.DeviceId, info.TabletDeviceId);
    }

    private sealed record DeviceUpInfo(int DeviceId, int TabletDeviceId);
}

/// <summary>时间笔锋叠加层：承载「已定型段」（追加式，每段只画一次）与「生长段」（每帧重绘）两层视觉。
/// 每个 stylus 设备一组独立视觉层（多指/手掌互不覆盖）；不参与命中测试以免挡住墨迹交互。</summary>
public sealed class TimePressureOverlay : FrameworkElement
{
    private readonly ContainerVisual _root = new();
    private readonly Dictionary<int, SessionLayer> _layers = new();

    /// <summary>单个设备的视觉层：容器 + 生长视觉 + 已定型视觉列表（生长层永远在同类已定型段之上）。</summary>
    internal sealed class SessionLayer
    {
        internal readonly ContainerVisual Root = new();
        internal readonly DrawingVisual Growing = new();
        internal readonly List<DrawingVisual> Committed = new();
    }

    public TimePressureOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;   // 湿墨裁剪到本页：InkCanvas 自身 ClipToBounds=true 会裁干墨，叠加层若不裁，越界湿墨会盖到相邻页面（松手后随清空消失，形成"跨画布又消失"的假象）
        AddVisualChild(_root);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _root;

    /// <summary>跟随宿主铺满页面（默认 MeasureOverride 返回 0，会让 Auto 尺寸的宿主量不出高度）。</summary>
    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
               double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

    /// <summary>为设备建立（或重建）视觉层：同 id 的旧层先移除，避免残留。</summary>
    internal SessionLayer AddSessionLayer(int deviceId)
    {
        RemoveSessionLayer(deviceId);
        var layer = new SessionLayer();
        layer.Root.Children.Add(layer.Growing);
        _root.Children.Add(layer.Root);
        _layers[deviceId] = layer;
        return layer;
    }

    /// <summary>移除设备的视觉层（原地清空，不增删其他层）。</summary>
    internal void RemoveSessionLayer(int deviceId)
    {
        if (!_layers.Remove(deviceId, out var layer))
            return;
        _root.Children.Remove(layer.Root);
        ClearLayer(layer);
    }

    /// <summary>追加一个用于承载「已定型段」的视觉（插在生长层之下，保证生长段永远在最上）。</summary>
    internal DrawingVisual AddCommittedVisual(SessionLayer layer)
    {
        var visual = new DrawingVisual();
        layer.Root.Children.Insert(layer.Root.Children.Count - 1, visual);
        layer.Committed.Add(visual);
        return visual;
    }

    /// <summary>打开该设备生长层的绘制上下文（每帧调用会清空并重绘生长段）。</summary>
    internal DrawingContext OpenGrowing(SessionLayer layer) => layer.Growing.RenderOpen();

    /// <summary>清空全部设备的内容（原地清空，不增删视觉树，避免布局抖动）。</summary>
    public void Clear()
    {
        foreach (var deviceId in new List<int>(_layers.Keys))
            RemoveSessionLayer(deviceId);
    }

    private static void ClearLayer(SessionLayer layer)
    {
        foreach (var visual in layer.Committed)
        {
            layer.Root.Children.Remove(visual);
            visual.RenderOpen().Close();
        }
        layer.Committed.Clear();
        layer.Growing.RenderOpen().Close();
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
        if (_controller.CollectArmed || _controller.ShieldArmed)
            return;
        base.OnDraw(drawingContext, stylusPoints, geometry, fillBrush);
    }
}

/// <summary>时间笔锋控制器（每个画布一个）：按 stylus 设备分会话——采集点在笔线程写入、
/// UI 线程按帧渲染生长动画、抬笔时把各点定格压力写回 Stroke 或自建 Stroke 交宿主入集合。</summary>
public sealed class TimePressureController
{
    private const double TipWidthPx = 0;      // 实验：起始/末端最低宽度 = 精确 0（原 0.1；用户实测对比收笔末端形态）
    private const int CommitOverlap = 1;      // 提交段向前重叠的点数：折线几何逐段确定，重叠区两次绘制完全重合，1 点即可保证段间路径连续
    private const int CommitBatchPoints = 64; // 攒批提交阈值：成熟点攒够该数量才追加一个 committed 视觉（长笔画视觉数 ≈ 总点数/64，而非 ≈ 帧数）

    /// <summary>采样回跳诊断开关：定位"假线"用，确认后可关（命中时写 Data\error.log）。</summary>
    private const bool LogSampleAnomalies = true;
    private const int MaxAnomalyLogPerStroke = 20;

    /// <summary>自绘笔画标记：宿主据此外键从 Stroke 取回 stylus 设备 id（供定格/清场）。</summary>
    public static readonly Guid SelfDrawnDeviceGuid = new("7B0A7E4C-6B2A-4E1F-9E0C-3B1F2C5A8D41");

    /// <summary>墨迹点长满时长（ms）：由设置「时间笔锋墨迹点长满时间」驱动（MainWindow 同步），数字越大笔锋越明显。</summary>
    public double GrowMs { get; set; } = 100.0;

    private readonly InkCanvas _canvas;
    private readonly TimePressureOverlay _overlay = new();
    private readonly object _sync = new();
    private readonly Dictionary<int, DeviceSession> _sessions = new();
    private readonly HashSet<int> _downDevices = new();   // 当前落笔中的设备（含未采集），用于认主
    private readonly Dictionary<int, bool> _stylusTablets = new();   // tabletDeviceId → 是否数位笔（缓存）

    private int _primaryDeviceId = -1;        // InkCanvas 原生收墨的那支（首个落笔设备）
    private long _sessionToken;               // 会话令牌自增（锁内）
    private volatile bool _collectArmed;      // 时间笔锋（主设备）：UI 线程写、笔线程读
    private volatile bool _shieldArmed;       // 硬笔 0/1 屏蔽压感（主设备）：UI 线程写、笔线程读
    private volatile bool _multiDeviceEnabled;// PDF 多指书写：副设备由叠加层自绘
    private volatile bool _selfDrawAllDevices;// 画板多触摸：全部设备由叠加层自绘
    private bool _renderingHooked;            // 仅笔画进行中订阅 CompositionTarget.Rendering

    /// <summary>单设备会话（笔线程采集、UI 线程渲染，状态改动均持锁）。</summary>
    private sealed class DeviceSession
    {
        public int DeviceId;
        public int TabletDeviceId;
        public readonly List<TimePressureSample> Samples = new();
        public readonly Stopwatch Clock = new();
        public double UpElapsedMs;      // 0 = 未收到抬笔
        public double LastBatchEndMs;
        public int CommittedBoundary;   // 已提交（冻结进 committed 层）的采样下标（不含）
        public TimePressureOverlay.SessionLayer? Layer;
        public int AnomalyLogCount;
        public bool InProgress;
        public bool Translucent;   // 半透明笔（荧光笔）：不做分批提交，避免接缝处重复绘制导致 alpha 叠加圆点
        public long Token;   // 会话令牌：延迟清理只在令牌仍匹配时生效，防误杀同设备的下一笔
    }

    public TimePressureController(InkCanvas canvas) => _canvas = canvas;

    public TimePressureOverlay Overlay => _overlay;

    public bool CollectArmed => _collectArmed;

    public bool ShieldArmed => _shieldArmed;

    /// <summary>PDF 多指书写：副设备（非主设备）也由叠加层实时绘制、抬笔自建 Stroke。
    /// 关闭（含切到橡皮/选择/形状）时立即中止在绘会话，防残留湿墨被提交。</summary>
    public bool MultiDeviceEnabled
    {
        get => _multiDeviceEnabled;
        set
        {
            if (_multiDeviceEnabled == value)
                return;
            _multiDeviceEnabled = value;
            if (!value)
                AbortStroke();
        }
    }

    /// <summary>画板多触摸：全部设备都由叠加层绘制（触摸经 StrokeVisual 自绘、数位笔仍走原生命中管线，抬笔按设备类型决定是否自提交）。</summary>
    public bool SelfDrawAllDevices
    {
        get => _selfDrawAllDevices;
        set
        {
            if (_selfDrawAllDevices == value)
                return;
            _selfDrawAllDevices = value;
            if (value)
                RefreshTabletTypes();   // 需要区分数位笔/触摸设备（数位笔走原生命中管线，避免双份湿墨）
            else
                AbortStroke();          // 切到橡皮/选择/形状：丢弃在绘会话与叠加层湿墨，防残留被提交
        }
    }

    /// <summary>预扫描平板类型（必须在 UI 线程）：多触摸模式下据此区分数位笔与触摸设备。</summary>
    public void RefreshTabletTypes()
    {
        try
        {
            foreach (TabletDevice tablet in Tablet.TabletDevices)
            {
                lock (_sync)
                    _stylusTablets[tablet.Id] = tablet.Type == TabletDeviceType.Stylus;
            }
        }
        catch
        {
            // 平板服务不可用时保持既有缓存（未知按触摸处理）
        }
    }

    /// <summary>页面缩放（外层整层 ScaleTransform），用于把「屏幕针尖宽度」换算回页面坐标。</summary>
    public double DisplayScale { get; set; } = 1.0;

    /// <summary>「启用压感」开关：关闭时时间笔锋的封顶压力统一为 0.5（与既有归一语义一致）。</summary>
    public bool PressureEnabled { get; set; } = true;

    /// <summary>自绘笔画提交事件（UI 线程）：宿主 Add 到 Strokes 并走既有收笔处理（笔锋改写/墨迹纠正/撤销）。</summary>
    public event Action<Stroke, int>? StrokeReady;

    /// <summary>由 UI 线程在「时间笔锋 + 画笔模式 + 非荧光笔」条件变化时同步；失去武装即中断全部会话。</summary>
    public void SetCollectArmed(bool armed)
    {
        if (_collectArmed == armed)
            return;
        _collectArmed = armed;
        if (!armed)
            AbortStroke();
    }

    /// <summary>屏蔽模式（0/1 笔锋 + 硬笔）：屏蔽默认湿墨、叠加层等宽自绘（无生长动画），
    /// 抬笔由 StrokeCollected 清场（干墨笔锋改写照常）；失去武装即中断全部会话。</summary>
    public void SetShieldArmed(bool armed)
    {
        if (_shieldArmed == armed)
            return;
        _shieldArmed = armed;
        if (!armed)
            AbortStroke();
    }

    /// <summary>中断全部会话（模式切换/画布卸载/第 2 指停墨等多入口调用）。</summary>
    public void AbortStroke()
    {
        List<int> ids;
        lock (_sync)
        {
            ids = new List<int>(_sessions.Keys);
            _primaryDeviceId = -1;
            _downDevices.Clear();
        }
        foreach (var id in ids)
            EndSession(id);
    }

    /// <summary>中断指定设备的会话（手掌擦除等：只丢弃该设备的湿墨，不影响其他手指）。</summary>
    public void AbortStroke(int deviceId) => EndSession(deviceId);

    /// <summary>指定设备当前是否有在绘会话（画板宿主据此跳过 StrokeVisual 自绘）。</summary>
    public bool HasSession(int deviceId)
    {
        lock (_sync)
            return _sessions.TryGetValue(deviceId, out var session) && session.InProgress;
    }

    /// <summary>清掉指定设备的会话与视觉（幂等；宿主在收笔处理后调用）。</summary>
    public void EndStroke(int deviceId) => EndSession(deviceId);

    /// <summary>仅在"该设备有会话但从未收到抬笔"时结束（丢失 StylusUp 的僵死会话）。
    /// 正常抬笔已在 MarkStrokeUp 写入抬笔时刻，不会命中，避免误杀正在书写的笔画。</summary>
    public void AbortStrokeIfPending(int deviceId)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(deviceId, out var session) || session.UpElapsedMs > 0)
                return;
        }
        EndSession(deviceId);
    }

    // ───────────────────────────── 笔线程采集入口 ─────────────────────────────

    /// <summary>该设备是否归叠加层采集渲染。首个落笔设备记为主设备（InkCanvas 原生收墨的那支）；
    /// 多指/多触摸模式下副设备（或全部设备）也采集。</summary>
    internal bool ShouldCollect(int deviceId, int tabletDeviceId)
    {
        lock (_sync)
        {
            _downDevices.Add(deviceId);
            // 主设备 = 当前落笔中的第一支（InkCanvas 原生收墨的那支）。上一支已抬起则改认本支，
            // 保证连续快速落笔都能拿到"主设备"身份（不影响尚未定格的上一条笔画：定格按 id 取会话）。
            if (_primaryDeviceId == -1 || !_downDevices.Contains(_primaryDeviceId))
                _primaryDeviceId = deviceId;
            bool primary = deviceId == _primaryDeviceId;
            if (_selfDrawAllDevices)
            {
                // 数位笔在非武装状态走原生命中管线（自身有湿墨），采集会叠出双份墨；
                // 触摸设备没有原生命中湿墨，必须由叠加层绘制。
                bool stylus = _stylusTablets.TryGetValue(tabletDeviceId, out bool isStylus) && isStylus;
                if (stylus && !_collectArmed && !_shieldArmed)
                    return false;
                return true;
            }
            // 副设备：多指书写（必须先于武装判断——否则样式 3 / 硬笔 0/1 时副设备永远不被采集）
            if (!primary && _multiDeviceEnabled)
                return true;
            if (_collectArmed || _shieldArmed)
                return primary;
            return false;
        }
    }

    internal void BeginStroke(int deviceId, int tabletDeviceId)
    {
        var session = new DeviceSession { DeviceId = deviceId, TabletDeviceId = tabletDeviceId, InProgress = true };
        lock (_sync)
        {
            session.Token = ++_sessionToken;
            _sessions[deviceId] = session;   // 同设备重复落笔：旧会话被替换（视觉层在 StartRendering 内重建）
            session.Clock.Restart();
        }
        OnUi(() => StartRendering(deviceId));
    }

    internal void AppendSamples(int deviceId, StylusPointCollection points)
    {
        if (points == null || points.Count == 0)
            return;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(deviceId, out var session) || !session.InProgress)
                return;

            // 批内逐点分摊时间戳：输入管线会成批递交采样（收笔减速段/抬笔包常合并），
            // 若批内所有点共用同一时刻，点龄相同 → 定格压力相同 → 末端宽度平台收不到针尖。
            // 按批内累计空间距离比例分摊到 [上一批时刻, 本批时刻]：批内点距极不均匀
            // （急停密集、甩笔稀疏），按序号均分会让点龄与真实书写进度错位，
            // 宽度变化集中在错误位置 → 沿笔画出现无规律忽粗忽细。
            double tEnd = session.Clock.Elapsed.TotalMilliseconds;
            double tStart = session.LastBatchEndMs;
            if (tEnd <= tStart)
                tEnd = tStart + 0.01;   // 同批同刻兜底：保持每点时间戳严格递增

            // 先剔除与上一已收点重合的点（连续重复），再按空间距离分摊时间
            var clean = new List<(double X, double Y, float P)>(points.Count);
            foreach (var point in points)
            {
                double x = point.X;
                double y = point.Y;
                double px;
                double py;
                if (clean.Count > 0)
                {
                    (px, py, _) = clean[^1];
                }
                else if (session.Samples.Count > 0)
                {
                    var lastSample = session.Samples[^1];
                    px = lastSample.X;
                    py = lastSample.Y;
                }
                else
                {
                    clean.Add((x, y, point.PressureFactor));
                    continue;
                }
                if (Math.Abs(x - px) < 0.02 && Math.Abs(y - py) < 0.02)
                    continue;   // 与上一已收点重合：丢弃（不产生时间戳，避免零长线段）
                // 回跳点：与上一已收点远跳（>10px）且落在 6 点之前的旧轨迹 3px 内 → 丢弃该点。
                // 触摸链路偶发把旧位置的点（如落笔点）再送一次，留着会让叠加层折线跳回再跳回 → 假线；
                // 单指时干墨由 InkCanvas 自己的点生成（这里只影响湿墨），多指自绘时干墨来自采样，必须在这里就拦住。
                if (PointJumpedBack(session, x, y, px, py))
                {
                    LogAnomaly(session, deviceId, x, y);
                    continue;
                }
                clean.Add((x, y, point.PressureFactor));
            }
            int n = clean.Count;
            if (n == 0)
                return;

            // 总距离（页面坐标欧氏距离；全批重合点时退回序号均分）
            double total = 0;
            for (int i = 1; i < n; i++)
            {
                double dx = clean[i].X - clean[i - 1].X;
                double dy = clean[i].Y - clean[i - 1].Y;
                total += Math.Sqrt(dx * dx + dy * dy);
            }
            bool byDistance = total > 1e-9;
            double cum = 0;
            double tPrev = tStart;
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                {
                    double dx = clean[i].X - clean[i - 1].X;
                    double dy = clean[i].Y - clean[i - 1].Y;
                    cum += Math.Sqrt(dx * dx + dy * dy);
                }
                double frac = byDistance ? cum / total : (double)(i + 1) / n;
                double t = tStart + (tEnd - tStart) * frac;
                if (t <= tPrev)
                    t = tPrev + 1e-3;   // 严格递增兜底（批内重合/等距点同刻）
                tPrev = t;
                session.Samples.Add(new TimePressureSample { X = clean[i].X, Y = clean[i].Y, T = t, P = clean[i].P });
            }
            // 记实际末点时刻（非 tEnd）：兜底超调时仍保证跨批严格单调；正常末点 frac=1 → tPrev==tEnd
            session.LastBatchEndMs = tPrev;
        }
    }

    /// <summary>逐点回跳判定：待收点与上一已收点距离 > 25 px（远超正常连续输入的包间距），且落在
    /// 至少 6 点之前的某个旧采样 3 px 内。合法的自交/快速回描最多丢 1 个点（折线略稀疏），不会断笔；
    /// 25 px 门槛同时把扫描频率压到很低（只有异常远跳才做旧轨迹搜索）。</summary>
    private static bool PointJumpedBack(DeviceSession session, double x, double y, double px, double py)
    {
        double gx = x - px;
        double gy = y - py;
        if (gx * gx + gy * gy <= 625)
            return false;   // 与上一已收点 ≤ 25 px
        int count = session.Samples.Count;
        if (count < 6)
            return false;
        int limit = count - 6;
        for (int i = 0; i < limit; i++)
        {
            var sample = session.Samples[i];
            double dx = x - sample.X;
            double dy = y - sample.Y;
            if (dx * dx + dy * dy < 9)
                return true;   // 3 px 内命中旧轨迹
        }
        return false;
    }

    private void LogAnomaly(DeviceSession session, int deviceId, double x, double y)
    {
        if (!LogSampleAnomalies || session.AnomalyLogCount >= MaxAnomalyLogPerStroke)
            return;
        session.AnomalyLogCount++;
        App.LogText($"[TimePen] drop jump dev={deviceId} at=({x:F1},{y:F1}) settled={session.Samples.Count}");
    }

    /// <summary>记录抬笔包到达时刻（笔线程 OnStylusUp 调用，紧跟真实物理抬笔）。
    /// 定格必须以该时刻为"现在"——若用 UI 线程 StrokeCollected 的执行时刻，
    /// 墨水管线延迟（30~100ms+）会灌大末点点龄，导致快速甩笔也定格成全宽圆点。</summary>
    internal void MarkStrokeUp(int deviceId)
    {
        lock (_sync)
        {
            if (_sessions.TryGetValue(deviceId, out var session) && session.InProgress)
                session.UpElapsedMs = session.Clock.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>抬笔处理（UI 线程，OnStylusUpProcessed 调用）：自绘设备提交笔画；主设备（原生收墨）只补最后一帧。</summary>
    internal void OnDeviceUpProcessed(int deviceId, int tabletDeviceId)
    {
        lock (_sync)
            _downDevices.Remove(deviceId);
        if (ShouldCommitOnUp(deviceId, tabletDeviceId))
        {
            CommitSelfDrawnStroke(deviceId);
            return;
        }
        RenderSession(deviceId, useUpTime: true);
        // 原生收墨设备：会话要留到随后的 StrokeCollected（case 3 的 FreezeFinalize 还要读采样），
        // 因此放到 Background 优先级清场——输入管线（含 StrokeCollected）优先级更高，必先执行。
        // 令牌校验：若期间同一设备已开始下一笔，不误杀新会话。
        long token;
        lock (_sync)
            token = _sessions.TryGetValue(deviceId, out var session) ? session.Token : -1;
        OnUi(() => _overlay.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => EndSessionIfToken(deviceId, token))));
    }

    /// <summary>延迟清场：仅当该设备的会话仍是当初那一个（令牌一致）时才结束。</summary>
    private void EndSessionIfToken(int deviceId, long token)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(deviceId, out var session) || session.Token != token)
                return;
        }
        EndSession(deviceId);
    }

    /// <summary>抬笔是否由控制器自建 Stroke 入集合：多触摸模式下触摸设备为真、数位笔为假；
    /// 其余模式仅副设备为真（主设备由 InkCanvas 原生提交）。</summary>
    internal bool ShouldCommitOnUp(int deviceId, int tabletDeviceId)
    {
        lock (_sync)
        {
            if (_selfDrawAllDevices)
                return !IsStylusTablet(tabletDeviceId);
            return deviceId != _primaryDeviceId;
        }
    }

    /// <summary>数位笔平板判定（缓存）：多触摸模式下数位笔仍走原生命中管线，触摸设备才自绘提交。</summary>
    private bool IsStylusTablet(int tabletDeviceId)
    {
        lock (_sync)
        {
            if (_stylusTablets.TryGetValue(tabletDeviceId, out bool cached))
                return cached;
        }
        bool isStylus = false;
        try
        {
            foreach (TabletDevice tablet in Tablet.TabletDevices)
            {
                if (tablet.Id != tabletDeviceId)
                    continue;
                isStylus = tablet.Type == TabletDeviceType.Stylus;
                break;
            }
        }
        catch
        {
            // 平板服务不可用时按触摸处理（宁可自绘提交，也不丢墨）
        }
        lock (_sync)
            _stylusTablets[tabletDeviceId] = isStylus;
        return isStylus;
    }

    /// <summary>用会话采样自建 Stroke（原始压力）并交给宿主：宿主 Add 到 Strokes → StrokeCollected
    /// （InkStyle 3 在其中按设备定格）→ 再回到这里清场。</summary>
    private void CommitSelfDrawnStroke(int deviceId)
    {
        Stroke? stroke = null;
        lock (_sync)
        {
            if (_sessions.TryGetValue(deviceId, out var session) && session.Samples.Count >= 2)
            {
                var points = new StylusPointCollection(session.Samples.Count);
                foreach (var sample in session.Samples)
                    points.Add(new StylusPoint(sample.X, sample.Y, (float)sample.P));
                stroke = new Stroke(points)
                {
                    DrawingAttributes = _canvas.DefaultDrawingAttributes.Clone()
                };
                stroke.AddPropertyData(SelfDrawnDeviceGuid, deviceId);
            }
        }
        if (stroke == null)
        {
            EndSession(deviceId);
            return;
        }
        StrokeReady?.Invoke(stroke, deviceId);
        EndSession(deviceId);   // 幂等：style 3 已在 FreezeFinalize 内清过
    }

    // ───────────────────────────── UI 线程渲染 ─────────────────────────────

    private void OnUi(Action action)
    {
        var dispatcher = _overlay.Dispatcher;
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
    }

    private void StartRendering(int deviceId)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(deviceId, out var session))
                return;
            session.CommittedBoundary = 0;
            // 半透明笔（荧光笔）判定只能在 UI 线程读画布 DA；半透明笔不做分批提交（接缝重复会叠出深色圆点）
            session.Translucent = _canvas.DefaultDrawingAttributes.Color.A < 255;
            session.Layer = _overlay.AddSessionLayer(deviceId);   // 同 id 旧层由 AddSessionLayer 先移除
        }
        if (_renderingHooked)
            return;
        CompositionTarget.Rendering += OnFrame;
        _renderingHooked = true;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        int[] ids;
        lock (_sync)
        {
            if (_sessions.Count == 0)
            {
                UnhookRendering();
                return;
            }
            ids = new int[_sessions.Count];
            _sessions.Keys.CopyTo(ids, 0);
        }
        foreach (int id in ids)
            RenderSession(id, useUpTime: false);
    }

    /// <summary>渲染单设备会话：抬笔后（useUpTime）以抬笔时刻计算点龄，使湿墨终态与随后的干墨完全一致。</summary>
    private void RenderSession(int deviceId, bool useUpTime)
    {
        List<TimePressureSample> snapshot;
        int snapFrom;
        int localBoundary;
        double now;
        TimePressureOverlay.SessionLayer? layer;
        bool translucent;
        double growMs = GrowMs;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(deviceId, out var session) || !session.InProgress)
                return;
            // 有限窗口快照：只取「已提交边界 - 重叠点」之后的未提交段——已提交段永不重算，
            // 锁内拷贝与随后的成熟扫描均为 O(窗口)（≈ CommitBatchPoints + 当帧新增），
            // 不再随整笔采样变长（持锁时间短 → 笔线程 AppendSamples 不再被 UI 线程长时间阻塞）。
            snapFrom = Math.Max(0, session.CommittedBoundary - CommitOverlap);
            snapshot = session.Samples.GetRange(snapFrom, session.Samples.Count - snapFrom);
            localBoundary = session.CommittedBoundary - snapFrom;
            // 定格/末帧以抬笔包时刻为基准（排除 UI 线程处理延迟）；书写中用活时钟
            now = useUpTime && session.UpElapsedMs > 0 ? session.UpElapsedMs : session.Clock.Elapsed.TotalMilliseconds;
            layer = session.Layer;
            translucent = session.Translucent;
        }

        int count = snapshot.Count;
        if (layer == null || count == 0)
            return;

        // 1) 已长满段（点龄 ≥ GrowMs）：窗口内局部下标从已提交边界起扫描（窗口之前的点必然已成熟/已提交）。
        // 攒够 CommitBatchPoints 才追加一个 committed 视觉——冻结式提交不变（已提交段永不重算），
        // 但长笔画的 DrawingVisual 数量从 ≈ 帧数 降为 ≈ 总点数/64。
        // 未提交的成熟点继续画在生长层：age ≥ GrowMs 时公式结果恒等于 cap，与提交后完全一致——攒批无视觉差异。
        int localMature = localBoundary;
        while (localMature < count && (now - snapshot[localMature].T) >= growMs)
            localMature++;
        int pending = localMature - localBoundary;   // 已成熟未提交的点数
        // 半透明笔永不提交：已定型段/生长段在接缝处会重复绘制同一个圆头端点，
        // 荧光笔 alpha 叠加后就是周期性深色圆点。整笔只留生长层，单一连续路径每帧重绘。
        bool flush = !translucent && (useUpTime ? pending >= 1 : pending >= CommitBatchPoints);
        int growingFrom;
        if (flush)
        {
            // 快照起点即「旧边界 - 重叠点」→ 提交段在窗口内恒为 [0, localMature-1]（含重叠点衔接）。
            var visual = _overlay.AddCommittedVisual(layer);
            using (var dc = visual.RenderOpen())
                DrawStroke(dc, snapshot, 0, localMature - 1, now, capOnly: true, growMs);
            lock (_sync)
            {
                if (_sessions.TryGetValue(deviceId, out var session) && session.InProgress)
                    session.CommittedBoundary = snapFrom + localMature;
            }
            growingFrom = Math.Max(0, localMature - CommitOverlap);
        }
        else
        {
            growingFrom = Math.Max(0, localBoundary - CommitOverlap);
        }

        // 2) 生长段（重叠点起到窗口末尾）：每帧按各点龄重算压力重绘
        if (count - 1 - growingFrom >= 1)
        {
            using var dc = _overlay.OpenGrowing(layer);
            DrawStroke(dc, snapshot, growingFrom, count - 1, now, capOnly: false, growMs);
        }
    }

    private void DrawStroke(DrawingContext dc, List<TimePressureSample> snapshot, int from, int to,
        double now, bool capOnly, double growMs)
    {
        if (to - from < 1)
            return;
        // 非时间笔锋（屏蔽模式/副设备自绘）：湿墨从落笔起即等宽封顶，无生长动画（生长是时间笔锋专属）
        bool instant = !_collectArmed;
        double tip = TipPressure();
        var points = new StylusPointCollection();
        for (int i = from; i <= to; i++)
        {
            var sample = snapshot[i];
            double cap = CapPressure(sample);
            double pressure = capOnly || instant
                ? cap
                : tip + (cap - tip) * Math.Min(1.0, Math.Max(0.0, (now - sample.T) / Math.Max(1.0, growMs)));
            points.Add(new StylusPoint(sample.X, sample.Y, (float)pressure));
        }
        new Stroke(points, WetAttributes()).Draw(dc);
    }

    // ───────────────────────────── 抬笔定格 ─────────────────────────────

    /// <summary>主设备重载：取主设备会话；画板多触摸模式下原生收墨的是数位笔（可能是副会话），
    /// 先按"数位笔平板 + 最近抬笔"选；都不匹配时退回主设备/唯一会话，仍无则不改写、保持原样。</summary>
    public void FreezeFinalize(Stroke stroke, bool pressureEnabled)
    {
        int deviceId = -1;
        lock (_sync)
        {
            if (_selfDrawAllDevices)
            {
                double latestUp = -1;
                foreach (var pair in _sessions)
                {
                    if (!_stylusTablets.TryGetValue(pair.Value.TabletDeviceId, out bool isStylus) || !isStylus)
                        continue;   // 触摸设备的笔画由叠加层自绘提交，不走这里
                    if (pair.Value.UpElapsedMs > latestUp)
                    {
                        latestUp = pair.Value.UpElapsedMs;
                        deviceId = pair.Key;
                    }
                }
            }
            if (deviceId == -1 && _primaryDeviceId != -1 && _sessions.ContainsKey(_primaryDeviceId))
            {
                deviceId = _primaryDeviceId;
            }
            else if (deviceId == -1 && _sessions.Count == 1)
            {
                foreach (int id in _sessions.Keys)
                    deviceId = id;
            }
            else if (deviceId == -1 && _sessions.Count > 1)
            {
                double latestUp = -1;
                foreach (var pair in _sessions)
                {
                    if (pair.Value.UpElapsedMs > latestUp)
                    {
                        latestUp = pair.Value.UpElapsedMs;
                        deviceId = pair.Key;
                    }
                }
            }
        }
        if (deviceId == -1)
            return;
        FreezeFinalize(deviceId, stroke, pressureEnabled);
    }

    /// <summary>抬笔定格：把每个墨迹点的最终压力写回 Stroke（供 StrokeCollected 的 case 3 调用）。
    /// 点龄 = 抬笔时刻 − 该点诞生时刻；点数与采样数不一致时按首尾时间线性插值映射，采样缺失时保持原样。</summary>
    public void FreezeFinalize(int deviceId, Stroke stroke, bool pressureEnabled)
    {
        List<TimePressureSample>? snapshot = null;
        double now = 0;
        double growMs = GrowMs;
        lock (_sync)
        {
            if (_sessions.TryGetValue(deviceId, out var session))
            {
                snapshot = new List<TimePressureSample>(session.Samples);
                // 以抬笔包时刻为"现在"：UI 线程收到 StrokeCollected 时已隔墨水管线延迟（30~100ms+），
                // 用活时钟会把末点点龄灌大 → 快速甩笔也定格成全宽圆点；无抬笔记录时回退活时钟兜底。
                now = session.UpElapsedMs > 0 ? session.UpElapsedMs : session.Clock.Elapsed.TotalMilliseconds;
            }
        }

        var stylusPoints = stroke.StylusPoints;
        int count = stylusPoints.Count;
        // 未采到任何样本（例如武装状态晚于落笔生效）：不改写压力，保持原样，避免把整条笔画压成针尖细线
        if (snapshot is { Count: > 0 } && count > 0)
        {
            double tip = TipPressure();
            bool haveSamples = snapshot.Count >= 2;
            double span = haveSamples ? Math.Max(1e-6, snapshot[^1].T - snapshot[0].T) : 1e-6;
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
                double pressure = tip + (cap - tip) * Math.Min(1.0, Math.Max(0.0, age / Math.Max(1.0, growMs)));
                rebuilt.Add(new StylusPoint(stylusPoints[i].X, stylusPoints[i].Y, (float)pressure));
            }
            stroke.StylusPoints = rebuilt;
        }

        EndSession(deviceId);
    }

    private void EndSession(int deviceId)
    {
        TimePressureOverlay.SessionLayer? layer = null;
        bool empty;
        lock (_sync)
        {
            if (_sessions.Remove(deviceId, out var session))
            {
                layer = session.Layer;
                session.Layer = null;
                session.InProgress = false;
            }
            if (_primaryDeviceId == deviceId)
                _primaryDeviceId = -1;
            empty = _sessions.Count == 0;
        }
        if (layer != null)
            OnUi(() => _overlay.RemoveSessionLayer(deviceId));
        if (empty)
            OnUi(UnhookRendering);
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
        double tipPageWidth = TipWidthPx / DisplayScale;
        return 0.5 * tipPageWidth / pageWidth;
    }

    /// <summary>封顶压力 = 该点硬件压力（压感关闭/荧光笔时统一 0.5），且不低于起始针尖。</summary>
    private double CapPressure(TimePressureSample sample)
    {
        double tip = TipPressure();
        // 荧光笔无笔锋：粗细恒定，不吃硬件压力（与 IgnorePressure 一致）
        double cap = _canvas.DefaultDrawingAttributes.IsHighlighter || !PressureEnabled ? 0.5 : sample.P;
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
