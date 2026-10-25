using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Collections.Generic;

namespace InkCanvasForPDF.Services;

/// <summary>
/// 停顿强制识别追踪器（自有实现）：挂到任意 <see cref="InkCanvas"/>，监测"书写中停顿"——
/// 落笔期间位移持续 ≤ 容错值（px）达到触发时间（ms）即回调宿主做"试识别"：用采集点集构建的临时
/// 笔画先判定，命中才由宿主断笔提交并替换图形；未命中处理器返回 false，本类不做任何提交，
/// 笔迹保持连续不断，<see cref="_pending"/> 继续累积，下次停顿以整条笔画重试。本类只负责
/// "何时触发"，不感知识别与替换；一次落笔内笔继续画（位移超容错）会自动重新计时，可多次触发。
/// 配置经委托读取活值，设置修改即时生效，无需重启或重挂。
/// </summary>
public sealed class PauseInkToShapeTracker : IDisposable
{
    private readonly InkCanvas _canvas;
    private readonly Func<bool> _isEnabled;
    private readonly Func<int> _getDelayMs;
    private readonly Func<int> _getTolerancePx;
    private readonly Func<bool> _canTrigger;

    /// <summary>停顿试识别处理器（参数 = 设备 Id、本次落笔采集到的点；返回 true = 宿主已提交固化，本类清空采集点从零重采）。</summary>
    private readonly Func<int, IReadOnlyList<StylusPoint>, Task<bool>> _tryPauseRecognizeAsync;

    private readonly DispatcherTimer _timer;

    /// <summary>追踪状态：-1 = 未追踪；-2 = 鼠标；≥0 = Stylus 设备 id。</summary>
    private int _trackedDeviceId = -1;
    private Point _anchor;

    /// <summary>本次落笔已采集的墨迹点：识别未命中时保留累积（整条笔画重试），命中固化后清空重采。</summary>
    private readonly List<StylusPoint> _pending = new();

    /// <param name="canvas">被监测的画布。</param>
    /// <param name="isEnabled">设置总开关（独立于墨迹纠正总开关）。</param>
    /// <param name="getDelayMs">触发时间（ms）。</param>
    /// <param name="getTolerancePx">停顿容错值（px）：停顿期间位移 ≤ 该值不重置计时。</param>
    /// <param name="canTrigger">宿主触发门（编辑模式/形状模式/荧光笔/多触摸等），每次落笔与触发前复查。</param>
    /// <param name="tryPauseRecognizeAsync">停顿试识别处理器：宿主用临时笔画试识别，命中才断笔提交并替换；
    /// 返回 true 表示已提交固化（本类清空采集点），false 表示未命中（不断笔，采集点保留累积）。</param>
    public PauseInkToShapeTracker(
        InkCanvas canvas,
        Func<bool> isEnabled,
        Func<int> getDelayMs,
        Func<int> getTolerancePx,
        Func<bool> canTrigger,
        Func<int, IReadOnlyList<StylusPoint>, Task<bool>> tryPauseRecognizeAsync)
    {
        _canvas = canvas;
        _isEnabled = isEnabled;
        _getDelayMs = getDelayMs;
        _getTolerancePx = getTolerancePx;
        _canTrigger = canTrigger;
        _tryPauseRecognizeAsync = tryPauseRecognizeAsync;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _timer.Tick += PauseTimer_Tick;

        canvas.StylusDown += Canvas_StylusDown;
        canvas.StylusMove += Canvas_StylusMove;
        canvas.StylusUp += Canvas_StylusUp;
        // 鼠标兜底：WPF 中鼠标输入不产生 Stylus 事件（仅触摸/数位笔有），纯鼠标书写必须挂 Mouse 通路；
        // 用 Preview 保证在 InkCanvas 内建墨迹处理之前拿到事件；带平板服务的机器上鼠标会被提升为笔
        // （StylusDevice != null），此时交给 Stylus 通路，避免同一次输入被两条通路重复追踪。
        canvas.PreviewMouseDown += Canvas_MouseDown;
        canvas.PreviewMouseMove += Canvas_MouseMove;
        canvas.PreviewMouseUp += Canvas_MouseUp;
        canvas.LostMouseCapture += Canvas_LostMouseCapture;
    }

    private void Canvas_StylusDown(object sender, StylusDownEventArgs e)
    {
        _trackedDeviceId = -1;
        _pending.Clear();
        if (!_isEnabled() || !_canTrigger())
            return;
        _trackedDeviceId = e.StylusDevice.Id;   // 抢占追踪权（覆盖可能已开始的鼠标追踪）
        _anchor = e.GetPosition(_canvas);
        _pending.AddRange(e.GetStylusPoints(_canvas));
        RestartTimer();
    }

    private void Canvas_StylusMove(object sender, StylusEventArgs e)
    {
        if (_trackedDeviceId < 0 || e.StylusDevice.Id != _trackedDeviceId)
            return;
        _pending.AddRange(e.GetStylusPoints(_canvas));
        TrackMove(e.GetPosition(_canvas));
    }

    private void Canvas_StylusUp(object sender, StylusEventArgs e)
    {
        if (e.StylusDevice.Id != _trackedDeviceId)
            return;
        StopTracking();
    }

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // 已被 Stylus 通路追踪，或本次鼠标输入来自笔设备（提升）→ 交给 Stylus 通路
        if (_trackedDeviceId != -1 || e.StylusDevice != null)
            return;
        if (e.ChangedButton != MouseButton.Left)
            return;
        if (!_isEnabled() || !_canTrigger())
            return;
        _trackedDeviceId = -2;
        _anchor = e.GetPosition(_canvas);
        _pending.Clear();
        _pending.Add(new StylusPoint(_anchor.X, _anchor.Y, 0.5f));   // 鼠标无压感，恒 0.5
        RestartTimer();
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_trackedDeviceId != -2)
            return;
        var pos = e.GetPosition(_canvas);
        _pending.Add(new StylusPoint(pos.X, pos.Y, 0.5f));
        TrackMove(pos);
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_trackedDeviceId != -2 || e.ChangedButton != MouseButton.Left)
            return;
        StopTracking();
    }

    private void Canvas_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_trackedDeviceId != -2)
            return;
        StopTracking();
    }

    /// <summary>位移超容错 = 仍在书写 → 挪锚点并重启计时；容错内视为手抖，计时继续。</summary>
    private void TrackMove(Point pos)
    {
        double dx = pos.X - _anchor.X;
        double dy = pos.Y - _anchor.Y;
        if (dx * dx + dy * dy > (double)_getTolerancePx() * _getTolerancePx())
        {
            _anchor = pos;
            RestartTimer();
        }
    }

    /// <summary>按当前触发时间（重）启计时；触发时间变化后的下一笔生效。</summary>
    private void RestartTimer()
    {
        int delayMs = Math.Clamp(_getDelayMs(), 50, 5000);
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _timer.Start();
    }

    /// <summary>停顿计时到点：复查门 → 快照采集点交给宿主试识别（fire-and-forget，异常静默）。
    /// 未命中不发生任何提交（笔迹不断，采集点保留累积）；命中固化后从零重新采集，
    /// 同一次落笔若继续书写并再次停顿，只对新段重新识别，不重复处理已固化部分。</summary>
    private void PauseTimer_Tick(object? sender, EventArgs e)
    {
        _timer.Stop();
        int deviceId = _trackedDeviceId;
        if (deviceId == -1 || !_isEnabled() || !_canTrigger())
            return;   // -1 = 未追踪；-2（鼠标）与 ≥0（Stylus）都是有效追踪状态

        var points = _pending.ToArray();
        ConsoleLog.Write("停顿墨迹纠正", $"停顿触发: 点数={points.Length}, 设备={deviceId}");
        _ = TryPauseAsync(deviceId, points);
    }

    private async Task TryPauseAsync(int deviceId, IReadOnlyList<StylusPoint> points)
    {
        try
        {
            bool committed = await _tryPauseRecognizeAsync(deviceId, points).ConfigureAwait(true);
            ConsoleLog.Write("停顿墨迹纠正", $"停顿试识别结果: committed={committed}");
            if (committed && deviceId == _trackedDeviceId)
                _pending.Clear();   // 已提交固化：从零重新采集；未命中保留累积，下次停顿以整条笔画重试
        }
        catch (Exception ex)
        {
            // 停顿强制识别属书写增强：任何异常静默放弃，不断笔
            ConsoleLog.Write("停顿墨迹纠正", $"停顿试识别异常: {ex}");
        }
    }

    /// <summary>指定设备当前是否处于被追踪的落笔会话中（宿主在异步识别命中后复查，防止抬笔后重复提交）。</summary>
    internal bool IsTracking(int deviceId) => _trackedDeviceId == deviceId;

    private void StopTracking()
    {
        _trackedDeviceId = -1;
        _pending.Clear();
        _timer.Stop();
    }

    public void Dispose()
    {
        StopTracking();
        _canvas.StylusDown -= Canvas_StylusDown;
        _canvas.StylusMove -= Canvas_StylusMove;
        _canvas.StylusUp -= Canvas_StylusUp;
        _canvas.PreviewMouseDown -= Canvas_MouseDown;
        _canvas.PreviewMouseMove -= Canvas_MouseMove;
        _canvas.PreviewMouseUp -= Canvas_MouseUp;
        _canvas.LostMouseCapture -= Canvas_LostMouseCapture;
    }
}
