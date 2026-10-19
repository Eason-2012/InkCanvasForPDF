using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace InkCanvasForPDF.Services;

/// <summary>程序化生成的墨迹光标（笔尖点/黑板擦），尺寸 = 实际粗细的屏幕尺寸，热点 = 中心。
/// WPF 内置光标只乘 InkCanvas 自身的 Layout/Render 变换、不乘祖先的整层 ScaleTransform
/// （dotnet/wpf EditBehavior.GetElementTransformMatrix 只读 this.InkCanvas.Layout/RenderTransform），
/// 整层 DisplayScale 缩放下内置光标与实际笔迹/擦除区域必然错位（"小一圈"），故用自定义光标接管。
/// 位图 → HCURSOR 流程抄 KillerPDF PageDragCursor（CreateDIBSection + CreateIconIndirect）。</summary>
internal sealed class InkCircleCursor : IDisposable
{
    /// <summary>黑板擦宽高比（设计尺寸 38×56，外观对齐 ICCCE 黑板擦）。</summary>
    public const double EraserBoardAspect = 56.0 / 38.0;

    private readonly SafeCursorHandle _handle;

    private InkCircleCursor(SafeCursorHandle handle)
    {
        _handle = handle;
        Cursor = CursorInteropHelper.Create(handle);
    }

    /// <summary>WPF 光标对象。</summary>
    public Cursor Cursor { get; }

    /// <summary>黑板擦高度 = 宽 × 56/38（擦除形状与光标共用同一换算）。</summary>
    public static double EraserBoardHeight(double widthDips) => widthDips * EraserBoardAspect;

    /// <summary>批注笔尖光标：与画笔同色的实心圆点（ARGB 原样使用，荧光笔透明度一致），
    /// 直径 = 笔迹的屏幕实际宽度。</summary>
    public static InkCircleCursor? CreatePenDot(double diameterDips, double dpiScale, Color color)
    {
        return Create(diameterDips, diameterDips, dpiScale, (dc, rect) =>
            dc.DrawEllipse(new SolidColorBrush(color), null,
                new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2),
                rect.Width / 2, rect.Height / 2));
    }

    /// <summary>黑板擦光标：米白圆角矩形 + 灰描边 + 双竖条纹（38:56，自行绘制），
    /// 宽 = 擦除宽、高 = 宽 × 56/38，可见外缘恰好 = 擦除矩形。</summary>
    public static InkCircleCursor? CreateEraserBoard(double widthDips, double dpiScale)
    {
        double h = EraserBoardHeight(widthDips);
        return Create(widthDips, h, dpiScale, (dc, rect) =>
        {
            // 设计基准 38×56，所有尺寸按设计单位归一后乘缩放（矩形等比，sx == sy）
            double sx = rect.Width / 38.0;
            double sy = rect.Height / 56.0;
            double r = 4 * sx;                          // 圆角半径（设计 r=4）

            // 1. 主体：米白填充圆角矩形
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xF2, 0xEE, 0xEB)), null,
                new RectangleGeometry(rect, r, r));

            // 2. 灰描边（设计厚 1）：内缩半个笔宽，可见外缘 = 擦除矩形边界
            double stroke = sx;
            var inset = new Rect(rect.Left + stroke / 2, rect.Top + stroke / 2,
                rect.Width - stroke, rect.Height - stroke);
            double ir = Math.Max(0, r - stroke / 2);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(0xCD, 0xCD, 0xCD)), stroke),
                new RectangleGeometry(inset, ir, ir));

            // 3. 双竖条纹：胶囊，x 中心 14.5/23.5、y 17→39（设计单位），浅灰填充 + 25% 灰细描边
            var stripeBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0xCF, 0xCD));
            var stripePen = new Pen(new SolidColorBrush(Color.FromArgb(64, 0x6F, 0x6F, 0x6F)), sx);
            double halfStripe = 2.5 * sx;
            double y0 = rect.Top + 17 * sy;
            double y1 = rect.Top + 39 * sy;
            foreach (double cxD in new[] { 14.5, 23.5 })
            {
                double cx = rect.Left + cxD * sx;
                var capsule = new Rect(cx - halfStripe, y0, halfStripe * 2, y1 - y0);
                dc.DrawGeometry(stripeBrush, stripePen, new RectangleGeometry(capsule, halfStripe, halfStripe));
            }
        });
    }

    /// <summary>生成宽 widthDips × 高 heightDips（屏幕 DIP）的光标；draw 收到的 Rect 为图形区（不含边距）。</summary>
    private static InkCircleCursor? Create(double widthDips, double heightDips, double dpiScale, Action<DrawingContext, Rect> draw)
    {
        double w = Math.Clamp(widthDips, 4, 128);      // 上限 128 DIP：防超大位图（Windows 光标惯例上限内）
        double h = Math.Clamp(heightDips, 4, 128);
        const double pad = 2;                          // DIP 边距，容纳描边外扩

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            draw(dc, new Rect(pad, pad, w, h));
        }

        int px = Math.Max(1, (int)Math.Ceiling((w + pad * 2) * dpiScale));
        int py = Math.Max(1, (int)Math.Ceiling((h + pad * 2) * dpiScale));
        var bitmap = new RenderTargetBitmap(px, py, 96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return CreateFromBitmap(bitmap);
    }

    private static InkCircleCursor? CreateFromBitmap(BitmapSource bitmap)
    {
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        // RenderTargetBitmap 只能输出 Pbgra32（预乘 alpha），而 Windows 光标按直通 alpha 解读 32bpp DIB：
        // 半透明色（荧光笔）会被二次合成而发暗失真，此处逐像素还原直通 alpha（a=0 保持 0）。
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if (a > 0 && a < 255)
            {
                pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);         // B
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a); // G
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a); // R
            }
        }

        var info = new BITMAPINFO
        {
            Header = new BITMAPINFOHEADER
            {
                Size = Marshal.SizeOf<BITMAPINFOHEADER>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32
            }
        };
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero || bits == IntPtr.Zero) return null;
        IntPtr mask = IntPtr.Zero;
        try
        {
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            mask = CreateBitmap(width, height, 1, 1, IntPtr.Zero);
            var icon = new ICONINFO
            {
                IsIcon = 0,
                HotspotX = width / 2,
                HotspotY = height / 2,
                MaskBitmap = mask,
                ColorBitmap = color
            };
            IntPtr cursor = CreateIconIndirect(ref icon);
            return cursor == IntPtr.Zero
                ? null
                : new InkCircleCursor(new SafeCursorHandle(cursor));
        }
        finally
        {
            if (mask != IntPtr.Zero) DeleteObject(mask);
            DeleteObject(color);
        }
    }

    public void Dispose()
    {
        Cursor.Dispose();
        _handle.Dispose();
    }

    private sealed class SafeCursorHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeCursorHandle(IntPtr cursor) : base(true) => SetHandle(cursor);

        protected override bool ReleaseHandle() => DestroyIcon(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int ImageSize;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ColorsUsed;
        public int ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER Header;
        public int Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr MaskBitmap;
        public IntPtr ColorBitmap;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BITMAPINFO bitmapInfo,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(
        int width,
        int height,
        uint planes,
        uint bitsPerPixel,
        IntPtr bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO iconInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
