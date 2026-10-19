namespace InkCanvasForPDF.Services;

/// <summary>原始位图辅助（抄自 KillerPDF BitmapHelpers，精简为渲染所需）。
/// 纯函数：仅处理 BGRA 像素缓冲，不涉及窗口状态。</summary>
internal static class BitmapHelpers
{
    /// <summary>BGRA 位图顺时针旋转 by degrees（0/90/180/270）。随引擎抄入，当前未接线。</summary>
    internal static (byte[] bytes, int w, int h) RotateBitmap(byte[] src, int w, int h, int degrees)
    {
        degrees = ((degrees % 360) + 360) % 360;
        if (degrees == 0) return (src, w, h);
        int newW = (degrees == 90 || degrees == 270) ? h : w;
        int newH = (degrees == 90 || degrees == 270) ? w : h;
        byte[] dst = new byte[newW * newH * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int srcIdx = (y * w + x) * 4;
                int dstX, dstY;
                switch (degrees)
                {
                    case 90: dstX = h - 1 - y; dstY = x; break;   // CW
                    case 180: dstX = w - 1 - x; dstY = h - 1 - y; break;
                    default: dstX = y; dstY = w - 1 - x; break;   // 270 CW
                }
                int dstIdx = (dstY * newW + dstX) * 4;
                dst[dstIdx] = src[srcIdx];
                dst[dstIdx + 1] = src[srcIdx + 1];
                dst[dstIdx + 2] = src[srcIdx + 2];
                dst[dstIdx + 3] = src[srcIdx + 3];
            }
        }
        return (dst, newW, newH);
    }
}
