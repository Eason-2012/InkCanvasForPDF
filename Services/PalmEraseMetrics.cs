using System;
using System.Windows.Input;

namespace InkCanvasForPDF.Services;

/// <summary>手掌擦除度量：判定「是否手掌」只看触点接触宽度是否超阈值；
/// 擦除洞尺寸 = 触点接触面积（等面积边长 √(W×H)），不再与橡皮大小绑定。
/// PDF 批注侧与画板共用，保证两侧手掌擦除判定与尺寸完全一致。</summary>
internal static class PalmEraseMetrics
{
    /// <summary>手掌擦除洞尺寸 = 触点接触面积的等面积边长 √(W×H)（原始触点值，
    /// 不乘 TouchMultiplier——对齐 ICA eraserMultiplier=1/TouchMultiplier 的抵消语义）。
    /// 返回值以触点所在坐标系（屏幕 DIP）计；PDF 侧需再 ÷ DisplayScale 换算到页面坐标。</summary>
    public static double GetTouchFootprintSize(TouchEventArgs e)
    {
        var b = e.GetTouchPoint(null).Bounds;
        return Math.Max(1.0, Math.Sqrt(b.Width * b.Height));   // 下限 1：防驱动报 0 尺寸令 StylusShape 抛异常
    }

    /// <summary>ICA GetTouchBoundWidth L281-289：Bounds.Width，四边红外取 √(W×H)，特殊屏 ×TouchMultiplier。</summary>
    public static double GetTouchBoundWidth(TouchEventArgs e)
    {
        var b = e.GetTouchPoint(null).Bounds;
        double v = Board.BoardView.Settings.Advanced.IsQuadIR
            ? Math.Sqrt(b.Width * b.Height)
            : b.Width;
        if (Board.BoardView.Settings.Advanced.IsSpecialScreen)
            v *= Board.BoardView.Settings.Advanced.TouchMultiplier;
        return v;
    }

    /// <summary>特殊屏 × 触摸倍数 = 0 时禁用擦除（ICA L66/L233 同款判据）。</summary>
    public static bool IsTouchEraseAllowed()
    {
        var a = Board.BoardView.Settings.Advanced;
        return a.TouchMultiplier != 0 || !a.IsSpecialScreen;
    }
}
