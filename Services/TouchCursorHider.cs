using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace InkCanvasForPDF.Services;

/// <summary>触摸输入的光标接管：触摸来源的鼠标消息显示全透明光标（隐藏系统触摸指针），
/// 真鼠标消息恢复应用原光标；橡皮 / 选择 / 开启画笔光标时保持原样。
/// 来源判别必须在窗口消息层做——触摸提升为鼠标后 WPF 事件的 StylusDevice 为 null，无法区分鼠标与触摸。</summary>
internal sealed class TouchCursorHider : IDisposable
{
    private const int WM_MOUSEMOVE = 0x0200;
    private const long TouchMouseSignature = 0xFF515700;   // 触摸/笔注入鼠标消息的 GetMessageExtraInfo 签名（低 32 位）
    private const long SignatureMask = 0xFFFFFF00;

    private readonly Window _window;
    private readonly Func<bool> _shouldKeepCursor;   // true = 当前状态应保留光标（橡皮 / 选择 / 开启画笔光标）
    private readonly Func<double> _getDpiScale;
    private InkCircleCursor? _invisible;
    private HwndSource? _source;
    private bool _lastFromTouch;

    public TouchCursorHider(Window window, Func<bool> shouldKeepCursor, Func<double> getDpiScale)
    {
        _window = window;
        _shouldKeepCursor = shouldKeepCursor;
        _getDpiScale = getDpiScale;
    }

    /// <summary>挂载窗口消息钩子（SourceInitialized 后调用一次，重复调用幂等）。</summary>
    public void Attach()
    {
        if (_source != null) return;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEMOVE)
        {
            long extra = unchecked((long)GetMessageExtraInfo().ToInt64());
            bool fromTouch = (extra & SignatureMask) == TouchMouseSignature;
            _lastFromTouch = fromTouch;
            Apply(fromTouch);
        }
        return IntPtr.Zero;
    }

    /// <summary>触摸事件兜底（未提升为鼠标消息的触摸设备），由窗口 PreviewTouchDown 调用。</summary>
    public void OnTouchInput()
    {
        _lastFromTouch = true;
        Apply(true);
    }

    /// <summary>工具/开关状态变化后重放（切橡皮、开关「显示画笔光标」即时生效）。</summary>
    public void Refresh()
    {
        if (_lastFromTouch) Apply(true);
    }

    private void Apply(bool fromTouch)
    {
        if (fromTouch && !_shouldKeepCursor())
        {
            _invisible ??= InkCircleCursor.CreateInvisible(_getDpiScale());
            Mouse.OverrideCursor = _invisible?.Cursor;
        }
        else if (Mouse.OverrideCursor != null)
        {
            Mouse.OverrideCursor = null;   // 恢复各元素自身光标（PDF/画板位图光标、系统 Pen 等既有逻辑不变）
        }
    }

    public void Dispose()
    {
        if (Mouse.OverrideCursor != null) Mouse.OverrideCursor = null;
        _source?.RemoveHook(WndProc);
        _source = null;
        _invisible?.Dispose();
        _invisible = null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetMessageExtraInfo();
}
