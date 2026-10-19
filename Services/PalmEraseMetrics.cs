using System;
using System.Windows.Input;

namespace InkCanvasForPDF.Services;

/// <summary>手掌擦除尺寸度量（照 ICA MW_TouchEvents L227-255 / L281-289）：
/// 尺寸 = 触点接触宽度 × k × 特殊屏补偿；k 由橡皮粗细滑块联动（ICA 用下拉框档位，本项目滑块 10 → k=1.0）。
/// PDF 批注侧与画板共用，保证两侧手掌擦除判定与尺寸完全一致。</summary>
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

    /// <summary>ICA L228-231：未把橡皮绑定到触摸倍数的特殊屏 → 反向补偿 1/TouchMultiplier。</summary>
    public static double GetEraserMultiplier()
    {
        var a = Board.BoardView.Settings.Advanced;
        return !a.EraserBindTouchMultiplier && a.IsSpecialScreen ? 1 / a.TouchMultiplier : 1.0;
    }

    /// <summary>k：橡皮粗细滑块显示值 ÷ 10（滑块 10 → 1.0，对齐 ICA 下拉框默认档 2）。</summary>
    public static double GetSizeK(double eraserSliderValue) => Math.Max(0.1, eraserSliderValue / 10.0);

    /// <summary>手掌擦除尺寸 = boundsWidth × k × eraserMultiplier。</summary>
    public static double GetPalmEraseSize(TouchEventArgs e, double eraserSliderValue)
        => GetTouchBoundWidth(e) * GetSizeK(eraserSliderValue) * GetEraserMultiplier();

    /// <summary>特殊屏 × 触摸倍数 = 0 时禁用擦除（ICA L66/L233 同款判据）。</summary>
    public static bool IsTouchEraseAllowed()
    {
        var a = Board.BoardView.Settings.Advanced;
        return a.TouchMultiplier != 0 || !a.IsSpecialScreen;
    }
}
