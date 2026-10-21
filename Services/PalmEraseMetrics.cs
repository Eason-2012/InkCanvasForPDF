using System;
using System.Windows.Input;

namespace InkCanvasForPDF.Services;

/// <summary>手掌擦除判定度量：判定「是否手掌」只看触点接触宽度是否超阈值；
/// 擦除尺寸与橡皮大小（EraserSize / EraserDiameter）绑定，不再按触点面积换算。
/// PDF 批注侧与画板共用，保证两侧手掌擦除判定完全一致。</summary>
internal static class PalmEraseMetrics
{
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
