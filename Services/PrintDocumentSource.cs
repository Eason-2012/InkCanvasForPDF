using InkCanvasForPDF.Models;

namespace InkCanvasForPDF.Services;

/// <summary>打印用单页信息：物理页尺寸（未旋转）与显示尺寸（按页面旋转交换宽高）。</summary>
public sealed record PrintPageInfo(
    int Index,
    PageModel Model,
    double ContentDipW,
    double ContentDipH,
    double DisplayDipW,
    double DisplayDipH,
    int Rotation);

/// <summary>打印数据源：PDF 渲染服务 + 当前文档全部页（含墨迹与尺寸）。仅 UI 线程创建一次。</summary>
public sealed class PrintDocumentSource
{
    public PdfRenderService Pdf { get; }

    public IReadOnlyList<PrintPageInfo> Pages { get; }

    public PrintDocumentSource(PdfRenderService pdf, IReadOnlyList<PrintPageInfo> pages)
    {
        Pdf = pdf;
        Pages = pages;
    }
}
