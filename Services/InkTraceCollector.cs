using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace InkCanvasForPDF.Services;

/// <summary>在绘笔画点采集器（PDF 页画布专用）：按 UI 路由事件累积"第一支未抬起输入"的点集，
/// 供双指手势停墨时重建被中断的笔画（WPF 切 EditingMode 会直接丢弃湿墨，不重建就整笔消失）。
/// 每个画布一个，随画布 Loaded/Unloaded 创建与释放。</summary>
public sealed class InkTraceCollector : IDisposable
{
    private readonly InkCanvas _canvas;
    private readonly List<StylusPoint> _points = new();
    private readonly StylusDownEventHandler _downHandler;
    private readonly StylusEventHandler _moveHandler;
    private readonly StylusEventHandler _upHandler;
    private int _deviceId = -1;   // 正在采集的输入设备（-1 = 无在绘输入）

    public InkTraceCollector(InkCanvas canvas)
    {
        _canvas = canvas;
        _downHandler = OnDown;
        _moveHandler = OnMove;
        _upHandler = OnUp;
        // handledEventsToo：InkCanvas 内建墨迹处理可能已把事件标记为已处理，普通订阅会漏收
        canvas.AddHandler(UIElement.StylusDownEvent, _downHandler, true);
        canvas.AddHandler(UIElement.StylusMoveEvent, _moveHandler, true);
        canvas.AddHandler(UIElement.StylusUpEvent, _upHandler, true);
    }

    /// <summary>当前是否有在绘输入（点集非空）。</summary>
    public bool HasPending => _deviceId != -1 && _points.Count > 0;

    /// <summary>取走当前在绘笔画的采集点（取走即清空）。无在绘输入返回 false。</summary>
    public bool TryTake(out IReadOnlyList<StylusPoint> points)
    {
        bool had = HasPending;
        points = _points.ToArray();
        _points.Clear();
        _deviceId = -1;
        return had;
    }

    private void OnDown(object? sender, StylusDownEventArgs e)
    {
        if (_deviceId != -1)
            return;   // 只跟第一支未抬起的输入（= InkCanvas 正在收墨的那支）
        _deviceId = e.StylusDevice.Id;
        _points.Clear();
        _points.AddRange(e.GetStylusPoints(_canvas));
    }

    private void OnMove(object? sender, StylusEventArgs e)
    {
        if (_deviceId == -1 || e.StylusDevice.Id != _deviceId)
            return;
        _points.AddRange(e.GetStylusPoints(_canvas));
    }

    private void OnUp(object? sender, StylusEventArgs e)
    {
        if (e.StylusDevice.Id != _deviceId)
            return;
        _points.Clear();
        _deviceId = -1;
    }

    public void Dispose()
    {
        _canvas.RemoveHandler(UIElement.StylusDownEvent, _downHandler);
        _canvas.RemoveHandler(UIElement.StylusMoveEvent, _moveHandler);
        _canvas.RemoveHandler(UIElement.StylusUpEvent, _upHandler);
        _points.Clear();
        _deviceId = -1;
    }
}
