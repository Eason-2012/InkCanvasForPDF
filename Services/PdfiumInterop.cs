using System.Runtime.InteropServices;
using Docnet.Core;

namespace InkCanvasForPDF.Services;

// PDFium 直连 P/Invoke（抄自 KillerPDF 的 PdfiumInterop，精简为渲染核心）。
//
// 线程模型：PDFium 是单线程的。Docnet 在其内部静态锁（Docnet.Core.DocLib.Lock）上串行每个原生调用；
// 本类所有直接 pdfium.dll 调用必须持有同一把锁（PdfiumLock），否则后台缩略图渲染与 UI 页面渲染
// 可能同时进入 PDFium——原生堆损坏（退出码 0xc0000374）。Raw 后缀的 extern 只能从持锁上下文调用。
#pragma warning disable SYSLIB1054
internal static class PdfiumInterop
{
    /// <summary>与 Docnet 共用的 PDFium 全局锁（反射 DocLib.Lock，保证与 Docnet 原生调用互斥）。</summary>
    internal static readonly object PdfiumLock =
        typeof(DocLib).GetField("Lock",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null) ?? new object();

    // ---- 文档 / 页生命周期 ---------------------------------------------------------------

    [DllImport("pdfium.dll", EntryPoint = "FPDF_LoadDocument", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDF_LoadDocumentRaw(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);

    internal static IntPtr FPDF_LoadDocument(string filePath, string? password)
    { lock (PdfiumLock) return FPDF_LoadDocumentRaw(filePath, password); }

    [DllImport("pdfium.dll", EntryPoint = "FPDF_GetLastError", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FPDF_GetLastErrorRaw();

    /// <summary>取最近一次原生调用的错误码（须在失败调用后、同锁内立即读取）。4=FPDF_ERR_PASSWORD。</summary>
    internal static int FPDF_GetLastError()
    { lock (PdfiumLock) return FPDF_GetLastErrorRaw(); }

    [DllImport("pdfium.dll", EntryPoint = "FPDF_CloseDocument", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_CloseDocumentRaw(IntPtr document);
    internal static void FPDF_CloseDocument(IntPtr document)
    { lock (PdfiumLock) FPDF_CloseDocumentRaw(document); }

    [DllImport("pdfium.dll", EntryPoint = "FPDF_GetPageCount", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FPDF_GetPageCountRaw(IntPtr document);
    internal static int FPDF_GetPageCount(IntPtr document)
    { lock (PdfiumLock) return FPDF_GetPageCountRaw(document); }

    [DllImport("pdfium.dll", EntryPoint = "FPDF_LoadPage", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDF_LoadPageRaw(IntPtr document, int page_index);
    internal static IntPtr FPDF_LoadPage(IntPtr document, int page_index)
    { lock (PdfiumLock) return FPDF_LoadPageRaw(document, page_index); }

    [DllImport("pdfium.dll", EntryPoint = "FPDF_ClosePage", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_ClosePageRaw(IntPtr page);
    internal static void FPDF_ClosePage(IntPtr page)
    { lock (PdfiumLock) FPDF_ClosePageRaw(page); }

    // ---- 页面尺寸 ------------------------------------------------------------------------

    [DllImport("pdfium.dll", EntryPoint = "FPDF_GetPageWidth", CallingConvention = CallingConvention.Cdecl)]
    private static extern double FPDF_GetPageWidthRaw(IntPtr page);

    [DllImport("pdfium.dll", EntryPoint = "FPDF_GetPageHeight", CallingConvention = CallingConvention.Cdecl)]
    private static extern double FPDF_GetPageHeightRaw(IntPtr page);

    /// <summary>取某页原生尺寸（pt）。持锁 LoadPage→读尺寸→ClosePage；句柄无效返回 (0,0)。</summary>
    internal static (double W, double H) GetPageSize(IntPtr document, int pageIndex)
    {
        lock (PdfiumLock)
        {
            if (document == IntPtr.Zero) return (0, 0);
            IntPtr page = FPDF_LoadPageRaw(document, pageIndex);
            if (page == IntPtr.Zero) return (0, 0);
            try { return (FPDF_GetPageWidthRaw(page), FPDF_GetPageHeightRaw(page)); }
            finally { FPDF_ClosePageRaw(page); }
        }
    }

    // ---- 页面渲染 ------------------------------------------------------------------------

    [DllImport("pdfium.dll", EntryPoint = "FPDFBitmap_CreateEx", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDFBitmap_CreateExRaw(
        int width, int height, int format, IntPtr firstScan, int stride);

    [DllImport("pdfium.dll", EntryPoint = "FPDFBitmap_Destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDFBitmap_DestroyRaw(IntPtr bitmap);

    [DllImport("pdfium.dll", EntryPoint = "FPDFBitmap_FillRect", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDFBitmap_FillRectRaw(
        IntPtr bitmap, int left, int top, int width, int height, uint color);

    [DllImport("pdfium.dll", EntryPoint = "FPDF_RenderPageBitmap", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_RenderPageBitmapRaw(
        IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY,
        int rotate, int flags);

    private const int FPDFBitmapBgra = 4;
    private const int FpdfAnnot = 0x01;
    private const int FpdfLcdText = 0x02;

    /// <summary>将一页栅格化为 BGRA 像素（叠白 + 注释外观 + LCD 文本），始终按页面未旋转方向输出。
    /// 旋转由 PdfRenderService 在像素层完成。
    /// 持锁 LoadPage→渲染→ClosePage；句柄无效或渲染失败返回 null。
    /// 缩略图渲染使用此入口（小缓冲，不值得走池）。</summary>
    internal static byte[]? FPDF_RenderPage(IntPtr document, int pageIndex, int width, int height)
    {
        lock (PdfiumLock)
        {
            if (document == IntPtr.Zero || width <= 0 || height <= 0) return null;
            var bytes = System.GC.AllocateUninitializedArray<byte>(checked(width * 4 * height));
            return RenderPageIntoLocked(document, pageIndex, width, height, bytes) ? bytes : null;
        }
    }

    /// <summary>同 <see cref="FPDF_RenderPage"/>，但复用调用方提供的缓冲（长度 ≥ width*height*4），
    /// 避免每页新分配 ≈20MB 的大对象堆垃圾。成功返回 true，失败不写入可用内容。</summary>
    internal static bool FPDF_RenderPageInto(IntPtr document, int pageIndex, int width, int height, byte[] target)
    {
        lock (PdfiumLock)
        {
            if (document == IntPtr.Zero || width <= 0 || height <= 0) return false;
            if (target.Length < checked(width * 4 * height)) return false;
            return RenderPageIntoLocked(document, pageIndex, width, height, target);
        }
    }

    /// <summary>栅格化核心（调用方必须持 PdfiumLock）：LoadPage → 铺白 + 渲染 → ClosePage。
    /// 输出 BGRA，stride = width*4。</summary>
    private static bool RenderPageIntoLocked(IntPtr document, int pageIndex, int width, int height, byte[] target)
    {
        try
        {
            IntPtr page = FPDF_LoadPageRaw(document, pageIndex);
            if (page == IntPtr.Zero) return false;
            try
            {
                int stride = checked(width * 4);
                var pinned = GCHandle.Alloc(target, GCHandleType.Pinned);
                try
                {
                    IntPtr bitmap = FPDFBitmap_CreateExRaw(
                        width, height, FPDFBitmapBgra, pinned.AddrOfPinnedObject(), stride);
                    if (bitmap == IntPtr.Zero) return false;
                    try
                    {
                        FPDFBitmap_FillRectRaw(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                        FPDF_RenderPageBitmapRaw(bitmap, page, 0, 0, width, height, 0,
                            FpdfAnnot | FpdfLcdText);
                    }
                    finally { FPDFBitmap_DestroyRaw(bitmap); }
                    return true;
                }
                finally { pinned.Free(); }
            }
            finally { FPDF_ClosePageRaw(page); }
        }
        catch { return false; }
    }
}
#pragma warning restore SYSLIB1054
