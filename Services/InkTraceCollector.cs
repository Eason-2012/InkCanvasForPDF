using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace InkCanvasForPDF.Services;

/// <summary>在绘笔画点采集器：停墨重建用。Stylus 通道优先（触摸提升成 Stylus 的点带真实压力，
/// 与 InkCanvas 原生收墨同源）；Touch 通道仅兜底（少数设备触摸不引发 Stylus 路由事件，此时只能拿位置、压力取原值 0.5）。
/// 每支输入只采第一次落下的设备，抬笔即清空。</summary>
public sealed class InkTraceCollector : IDisposable
{
    private readonly InkCanvas _canvas;
    private readonly List<StylusPoint> _points = new();
    private readonly EventHandler<TouchEventArgs> _touchDown, _touchMove, _touchUp;
    private readonly StylusDownEventHandler _stylusDown;
    private readonly StylusEventHandler _stylusMove, _stylusUp;
    private int _touchId = -1;    // 触摸通道当前设备（-1 = 无）
    private int _stylusId = -1;   // 数位笔通道当前设备（-1 = 无）

    public InkTraceCollector(InkCanvas canvas)
    {
        _canvas = canvas;
        _touchDown = OnTouchDown;
        _touchMove = OnTouchMove;
        _touchUp = OnTouchUp;
        _stylusDown = OnStylusDown;
        _stylusMove = OnStylusMove;
        _stylusUp = OnStylusUp;
        // handledEventsToo：InkCanvas 内建墨迹处理可能已把事件标记为已处理，普通订阅会漏收
        canvas.AddHandler(UIElement.TouchDownEvent, _touchDown, true);
        canvas.AddHandler(UIElement.TouchMoveEvent, _touchMove, true);
        canvas.AddHandler(UIElement.TouchUpEvent, _touchUp, true);
        canvas.AddHandler(UIElement.StylusDownEvent, _stylusDown, true);
        canvas.AddHandler(UIElement.StylusMoveEvent, _stylusMove, true);
        canvas.AddHandler(UIElement.StylusUpEvent, _stylusUp, true);
    }

    /// <summary>当前是否有在绘输入（点集非空）。</summary>
    public bool HasPending => _points.Count > 0 && (_touchId != -1 || _stylusId != -1);

    /// <summary>取走当前在绘笔画的采集点（取走即清空）。无在绘输入返回 false。</summary>
    public bool TryTake(out IReadOnlyList<StylusPoint> points)
    {
        bool had = HasPending;
        points = _points.ToArray();
        _points.Clear();
        _touchId = -1;
        _stylusId = -1;
        return had;
    }

    // ── Touch 兜底通道（部分设备触摸不引发 Stylus 路由事件，只能拿位置、压力取原值 0.5）──

    private void OnTouchDown(object? sender, TouchEventArgs e)
    {
        if (_touchId != -1 || _stylusId != -1)
            return;   // 已有在绘输入（第二指/已由 Stylus 接管）：不采
        _touchId = e.TouchDevice.Id;
        _points.Clear();
        var p = e.GetTouchPoint(_canvas).Position;
        _points.Add(new StylusPoint(p.X, p.Y, 0.5f));   // 兜底通道拿不到压力：取原值 0.5（与无压感设备原生一致）
    }

    private void OnTouchMove(object? sender, TouchEventArgs e)
    {
        if (_stylusId != -1)
            return;   // Stylus 通道已接管（带压力），Touch 只兜底
        if (_touchId == -1 || e.TouchDevice.Id != _touchId)
            return;
        var p = e.GetTouchPoint(_canvas).Position;
        _points.Add(new StylusPoint(p.X, p.Y, 0.5f));
    }

    private void OnTouchUp(object? sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Id != _touchId)
            return;
        _touchId = -1;
        if (_stylusId == -1)
            _points.Clear();   // 已切到 Stylus：点集归 Stylus 通道管，不能在这里清
    }

    // ── Stylus 通道（首选：触摸提升成 Stylus 的点带真实压力，与 InkCanvas 原生收墨同源）──

    private void OnStylusDown(object? sender, StylusDownEventArgs e)
    {
        if (_stylusId != -1)
            return;   // 已有在绘笔
        _stylusId = e.StylusDevice.Id;
        _touchId = -1;     // Stylus 更可靠：接管采集，丢弃 Touch 已采的无压力点
        var pts = e.GetStylusPoints(_canvas);
        if (pts.Count > 0)
        {
            _points.Clear();
            _points.AddRange(pts);
        }
    }

    private void OnStylusMove(object? sender, StylusEventArgs e)
    {
        if (_stylusId == -1 || e.StylusDevice.Id != _stylusId)
            return;
        _points.AddRange(e.GetStylusPoints(_canvas));
    }

    private void OnStylusUp(object? sender, StylusEventArgs e)
    {
        if (e.StylusDevice.Id != _stylusId)
            return;
        _points.Clear();
        _stylusId = -1;
    }

    public void Dispose()
    {
        _canvas.RemoveHandler(UIElement.TouchDownEvent, _touchDown);
        _canvas.RemoveHandler(UIElement.TouchMoveEvent, _touchMove);
        _canvas.RemoveHandler(UIElement.TouchUpEvent, _touchUp);
        _canvas.RemoveHandler(UIElement.StylusDownEvent, _stylusDown);
        _canvas.RemoveHandler(UIElement.StylusMoveEvent, _stylusMove);
        _canvas.RemoveHandler(UIElement.StylusUpEvent, _stylusUp);
        _points.Clear();
        _touchId = -1;
        _stylusId = -1;
    }
}
