using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Input.StylusPlugIns;

namespace InkCanvasForPDF.Services;

/// <summary>在绘笔画点采集（笔线程）：始终记录"第一支未抬起输入"的原始点，
/// 供双指停墨时重建被 WPF 丢弃的湿墨。与 InkCanvas 原生收墨同源（RawStylusInput），
/// 触摸提升为 Stylus 时同样可见；UI 路由事件可能被内建处理吞掉，这里不会。
/// 每支输入只采首个设备，抬笔即清空；UI 线程取点需持锁。</summary>
public sealed class InkTraceStore
{
    private readonly object _sync = new();
    private readonly List<StylusPoint> _points = new();
    private int _deviceId = -1;

    /// <summary>当前是否有在绘输入（点集非空）。</summary>
    public bool HasPending
    {
        get { lock (_sync) return _deviceId != -1 && _points.Count > 0; }
    }

    /// <summary>取走当前在绘点集（取走即清空）。无在绘输入返回 false。</summary>
    public bool TryTake(out IReadOnlyList<StylusPoint> points)
    {
        lock (_sync)
        {
            bool had = _deviceId != -1 && _points.Count > 0;
            points = _points.ToArray();
            _points.Clear();
            _deviceId = -1;
            return had;
        }
    }

    internal void Begin(int deviceId, StylusPointCollection? pts)
    {
        lock (_sync)
        {
            if (_deviceId != -1)
                return;   // 已有在绘输入（第二指及后续不采）
            _deviceId = deviceId;
            _points.Clear();
            if (pts != null)
                _points.AddRange(pts);
        }
    }

    internal void Append(int deviceId, StylusPointCollection? pts)
    {
        lock (_sync)
        {
            if (_deviceId == -1 || deviceId != _deviceId || pts == null)
                return;
            _points.AddRange(pts);
        }
    }

    internal void End(int deviceId)
    {
        lock (_sync)
        {
            if (deviceId != _deviceId)
                return;
            _points.Clear();
            _deviceId = -1;
        }
    }
}

/// <summary>把笔线程原始包转发给 <see cref="InkTraceStore"/>（无 UI 依赖）。</summary>
internal sealed class InkTracePlugIn : StylusPlugIn
{
    private readonly InkTraceStore _store;

    public InkTracePlugIn(InkTraceStore store) => _store = store;

    protected override void OnStylusDown(RawStylusInput rawStylusInput)
        => _store.Begin(rawStylusInput.StylusDeviceId, rawStylusInput.GetStylusPoints());

    protected override void OnStylusMove(RawStylusInput rawStylusInput)
        => _store.Append(rawStylusInput.StylusDeviceId, rawStylusInput.GetStylusPoints());

    protected override void OnStylusUp(RawStylusInput rawStylusInput)
        => _store.End(rawStylusInput.StylusDeviceId);
}
