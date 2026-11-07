using System;
using System.Collections.Generic;
using System.Linq;
using System.Printing;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace InkCanvasForPDF.Services;

/// <summary>一次打印任务的不可变版面快照（打印期间不受控件变化影响）。</summary>
public readonly record struct PrintLayout(
    bool Landscape, int AlignH, int AlignV, int ScaleMode, double CustomPct,
    double MarginPx, int NUp, bool Duplex, bool Grayscale);

/// <summary>打印机枚举结果；Server 必须与 Queues 同生命周期（队列引用其服务器）。</summary>
public sealed record PrintQueueSet(LocalPrintServer? Server, List<PrintQueue> Queues, PrintQueue? DefaultQueue);

/// <summary>打印核心服务（无 UI）：打印机/纸张查询、版面合成、墨迹复合、XPS 后台打印。</summary>
public static class PrintService
{
    private static readonly Regex CamelCaseBoundaryRegex = new("(?<=[a-z])(?=[A-Z])", RegexOptions.Compiled);
    private static readonly Regex PaperNameBoundaryRegex = new(@"(?<=[a-z])(?=[A-Z])|(?<=\d)(?=[A-Z])", RegexOptions.Compiled);

    /// <summary>枚举本机与连接打印机；默认打印机不在列表时插到第一位。</summary>
    public static PrintQueueSet LoadPrintQueues()
    {
        LocalPrintServer? server = null;
        var queues = new List<PrintQueue>();
        try
        {
            server = new LocalPrintServer();
            foreach (var queue in server.GetPrintQueues(
                [EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]))
                queues.Add(queue);
        }
        catch { /* 打印后台不可用：走默认打印机兜底 */ }

        PrintQueue? def = null;
        try { def = LocalPrintServer.GetDefaultPrintQueue(); } catch { }
        if (def != null && !queues.Any(q => q.FullName == def.FullName))
            queues.Insert(0, def);

        return new PrintQueueSet(server, queues, def);
    }

    /// <summary>驱动支持的纸张列表（过滤掉无尺寸信息的项）。</summary>
    public static List<PageMediaSize> GetPaperSizes(PrintQueue? queue)
    {
        var list = new List<PageMediaSize>();
        try
        {
            if (queue != null)
                foreach (var media in queue.GetPrintCapabilities().PageMediaSizeCapability)
                {
                    if (media is null || !media.Width.HasValue || !media.Height.HasValue) continue;
                    list.Add(media);
                }
        }
        catch { /* 驱动不支持查询：只保留“与文件一致” */ }
        return list;
    }

    /// <summary>驱动支持的进纸盒列表（Unknown 噪声项丢弃）。</summary>
    public static List<InputBin> GetInputBins(PrintQueue? queue)
    {
        var list = new List<InputBin>();
        try
        {
            if (queue != null)
                foreach (var bin in queue.GetPrintCapabilities().InputBinCapability)
                {
                    if (bin == InputBin.Unknown) continue;
                    list.Add(bin);
                }
        }
        catch { /* 驱动不支持查询：只保留默认 */ }
        return list;
    }

    /// <summary>可打印区域（DIP）：按方向归一化；驱动器查询失败时回退 Letter 纵向。</summary>
    public static (double Width, double Height) PrintableArea(
        PrintQueue? queue, bool landscape, PageMediaSize? paperOverride,
        double firstPageDipW, double firstPageDipH)
    {
        double w = 816, h = 1056;   // Letter 纵向回退值
        try
        {
            if (queue != null)
            {
                var pd = new System.Windows.Controls.PrintDialog { PrintQueue = queue };
                // 纸张优先跟随文档页尺寸；手动选择纸张覆盖自动匹配。
                var docMedia = paperOverride ?? MediaSizeForDocument(queue, firstPageDipW, firstPageDipH);
                if (docMedia != null)
                {
                    var ticket = pd.PrintTicket;
                    ticket.PageMediaSize = docMedia;
                    pd.PrintTicket = ticket;
                }
                if (pd.PrintableAreaWidth > 0 && pd.PrintableAreaHeight > 0)
                {
                    w = pd.PrintableAreaWidth;
                    h = pd.PrintableAreaHeight;
                }
            }
        }
        catch { /* 保留回退值 */ }

        if (landscape) { if (w < h) (w, h) = (h, w); }
        else { if (w > h) (w, h) = (h, w); }
        return (w, h);
    }

    /// <summary>在驱动支持的纸张中匹配文档首页尺寸（6 DIP ≈ 1/16 inch 容差，方向无关）。
    /// 找不到时返回 null，由打印机默认纸张兜底。</summary>
    public static PageMediaSize? MediaSizeForDocument(PrintQueue? queue, double pageDipW, double pageDipH)
    {
        try
        {
            if (queue is null || pageDipW <= 0 || pageDipH <= 0) return null;
            double pw = pageDipW, ph = pageDipH;
            if (pw > ph) (pw, ph) = (ph, pw);
            foreach (var media in queue.GetPrintCapabilities().PageMediaSizeCapability)
            {
                if (media is null || !media.Width.HasValue || !media.Height.HasValue) continue;
                double w = media.Width.Value, h = media.Height.Value;
                if (w > h) (w, h) = (h, w);
                if (Math.Abs(w - pw) <= 6 && Math.Abs(h - ph) <= 6) return media;
            }
        }
        catch { /* 驱动异常：保持打印机默认 */ }
        return null;
    }

    /// <summary>“NorthAmericaLetter” → “North America Letter (8.5 x 11 in)”；ISO/JIS 用毫米。</summary>
    public static string PaperDisplayName(PageMediaSize media)
    {
        string raw = media.PageMediaSizeName?.ToString() ?? "";
        string name = raw;
        if (name.StartsWith("ISO", StringComparison.Ordinal)) name = "ISO " + name[3..];
        else if (name.StartsWith("JIS", StringComparison.Ordinal)) name = "JIS " + name[3..];
        name = PaperNameBoundaryRegex.Replace(name, " ");
        string dims;
        if (raw.StartsWith("NorthAmerica", StringComparison.Ordinal))
        {
            double win = media.Width!.Value / 96.0, hin = media.Height!.Value / 96.0;
            dims = $"{win:0.##} x {hin:0.##} in";
        }
        else
        {
            double wmm = media.Width!.Value / 96.0 * 25.4, hmm = media.Height!.Value / 96.0 * 25.4;
            dims = $"{wmm:0} x {hmm:0} mm";
        }
        return name.Length > 0 ? $"{name} ({dims})" : dims;
    }

    /// <summary>进纸盒显示名：驼峰边界补空格。</summary>
    public static string InputBinDisplayName(InputBin bin)
        => CamelCaseBoundaryRegex.Replace(bin.ToString(), " ");

    /// <summary>解析 “1-3,5” 范围；空白 = 全部页；无匹配返回空集合。</summary>
    public static List<int> ParseRange(string? text, int count)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0) return [.. Enumerable.Range(0, count)];

        var set = new SortedSet<int>();
        foreach (var raw in text.Split(','))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            if (part.Contains('-'))
            {
                var seg = part.Split('-');
                if (seg.Length == 2 &&
                    int.TryParse(seg[0].Trim(), out int a) &&
                    int.TryParse(seg[1].Trim(), out int b))
                {
                    if (a > b) (a, b) = (b, a);
                    // 先夹紧两端再循环：避免 “1-2147483647” 在 int.MaxValue 处回绕成死循环。
                    if (a < 1) a = 1;
                    if (b > count) b = count;
                    for (int i = a; i <= b; i++) set.Add(i - 1);
                }
            }
            else if (int.TryParse(part, out int v))
            {
                if (v >= 1 && v <= count) set.Add(v - 1);
            }
        }
        return [.. set];
    }

    /// <summary>当前 N-up 的列/行网格（随纸张方向）。</summary>
    public static (int Cols, int Rows) NupGrid(PrintLayout layout) => layout.NUp switch
    {
        2 => layout.Landscape ? (2, 1) : (1, 2),
        4 => (2, 2),
        6 => layout.Landscape ? (3, 2) : (2, 3),
        9 => (3, 3),
        _ => (1, 1)
    };

    /// <summary>栅格像素 → DIP 缩放系数（适应页面 / 实际大小 / 自定义百分比）。</summary>
    public static double ScaleFor(
        double pageDipW, int rasterW, int rasterH, double areaW, double areaH, PrintLayout layout)
    {
        double actual = pageDipW / Math.Max(1, rasterW);
        return layout.ScaleMode switch
        {
            1 => actual,
            2 => actual * (layout.CustomPct / 100.0),
            _ => Math.Min(areaW / Math.Max(1, rasterW), areaH / Math.Max(1, rasterH))
        };
    }

    /// <summary>页面在可打印区域内的水平偏移。</summary>
    public static double OffsetH(double areaW, double imgW, PrintLayout layout)
        => layout.AlignH == 0 ? 0 : layout.AlignH == 2 ? areaW - imgW : (areaW - imgW) / 2;

    /// <summary>页面在可打印区域内的垂直偏移。</summary>
    public static double OffsetV(double areaH, double imgH, PrintLayout layout)
        => layout.AlignV == 0 ? 0 : layout.AlignV == 2 ? areaH - imgH : (areaH - imgH) / 2;

    /// <summary>按当前版面合成一张纸（白色底 + 若干页位图，含页边距/对齐/N-up）。
    /// 预览与打印共用，保证“预览所见 = 打印所得”。</summary>
    public static Grid ComposeSheet(
        IReadOnlyList<int> idxs, double areaW, double areaH,
        BitmapSource?[] pages, int[] rasterW, int[] rasterH,
        double[] pageDipW, double[] pageDipH, PrintLayout layout)
    {
        var sheet = new Grid
        {
            Width = areaW, Height = areaH, Background = Brushes.White, ClipToBounds = true,
            UseLayoutRounding = true, SnapsToDevicePixels = true
        };
        var canvas = new Canvas();
        double margin = layout.MarginPx;

        if (layout.NUp <= 1)
        {
            if (idxs.Count > 0)
            {
                int idx = idxs[0];
                if (pages[idx] is null) { sheet.Children.Add(canvas); return sheet; }
                double scale = ScaleFor(pageDipW[idx], rasterW[idx], rasterH[idx],
                    areaW - 2 * margin, areaH - 2 * margin, layout);
                double imgW = rasterW[idx] * scale, imgH = rasterH[idx] * scale;
                // 页面几乎填满可打印区域时吸附 +1 溢出血，盖住浮点缝隙造成的 1px 白边。
                if (imgW >= (areaW - 2 * margin) - 1.5) imgW = areaW - 2 * margin + 1;
                if (imgH >= (areaH - 2 * margin) - 1.5) imgH = areaH - 2 * margin + 1;
                BitmapSource source = layout.Grayscale
                    ? CreateGrayscaleBitmap(pages[idx]!) : pages[idx]!;
                var image = new Image { Source = source, Width = imgW, Height = imgH };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                Canvas.SetLeft(image, margin + OffsetH(areaW - 2 * margin, imgW, layout));
                Canvas.SetTop(image, margin + OffsetV(areaH - 2 * margin, imgH, layout));
                canvas.Children.Add(image);
            }
        }
        else
        {
            var (cols, rows) = NupGrid(layout);
            const double gap = 6;
            double cellW = (areaW - 2 * margin) / cols, cellH = (areaH - 2 * margin) / rows;
            for (int i = 0; i < idxs.Count && i < cols * rows; i++)
            {
                int idx = idxs[i];
                if (pages[idx] is null) continue;
                int row = i / cols, col = i % cols;
                double availW = Math.Max(1, cellW - gap), availH = Math.Max(1, cellH - gap);
                double scale = Math.Min(availW / Math.Max(1, rasterW[idx]),
                    availH / Math.Max(1, rasterH[idx]));
                double imgW = rasterW[idx] * scale, imgH = rasterH[idx] * scale;
                BitmapSource source = layout.Grayscale
                    ? CreateGrayscaleBitmap(pages[idx]!) : pages[idx]!;
                var image = new Image { Source = source, Width = imgW, Height = imgH };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                Canvas.SetLeft(image, margin + col * cellW + (cellW - imgW) / 2);
                Canvas.SetTop(image, margin + row * cellH + (cellH - imgH) / 2);
                canvas.Children.Add(image);
            }
        }

        sheet.Children.Add(canvas);
        return sheet;
    }

    /// <summary>彩色位图转灰度（BT.601 加权，保留 alpha；结果冻结）。</summary>
    public static BitmapSource CreateGrayscaleBitmap(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = checked(bgra.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * bgra.PixelHeight)];
        bgra.CopyPixels(pixels, stride, 0);
        for (int index = 0; index < pixels.Length; index += 4)
        {
            byte gray = (byte)((pixels[index + 2] * 77
                + pixels[index + 1] * 150 + pixels[index] * 29 + 128) >> 8);
            pixels[index] = gray;
            pixels[index + 1] = gray;
            pixels[index + 2] = gray;
        }

        BitmapSource result = BitmapSource.Create(
            bgra.PixelWidth, bgra.PixelHeight, bgra.DpiX, bgra.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    /// <summary>把该页墨迹按内容坐标复合到 PDF 位图上，并按页面显示旋转角旋转到输出方向。
    /// 后台线程调用：位图已冻结，DrawingVisual 在本线程创建。
    /// strokes 接受 StrokeCollection 或调用方快照的笔画数组（缩略图传 UI 线程引用数组快照）。</summary>
    public static BitmapSource CompositePageWithInk(
        BitmapSource pageBitmap, IReadOnlyList<Stroke> strokes,
        double contentDipW, double contentDipH,
        double inkCoordinateW, double inkCoordinateH, int rotation)
    {
        int contentW = pageBitmap.PixelWidth, contentH = pageBitmap.PixelHeight;
        bool swap = rotation is 90 or 270;
        int outputW = swap ? contentH : contentW;
        int outputH = swap ? contentW : contentH;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (rotation != 0)
            {
                var group = new TransformGroup();
                group.Children.Add(new RotateTransform(rotation));
                // TransformGroup 按「先旋转、再平移」组合；平移把旋转后的内容移回画布原点。
                // 90°/270° 输出宽高交换：90° 需右移 contentH，270° 需下移 contentW（写反会整页移出画布 → 空白）。
                var translate = rotation switch
                {
                    90 => new TranslateTransform(contentH, 0),
                    180 => new TranslateTransform(contentW, contentH),
                    270 => new TranslateTransform(0, contentW),
                    _ => new TranslateTransform(0, 0)
                };
                group.Children.Add(translate);
                dc.PushTransform(group);
            }

            dc.DrawImage(pageBitmap, new Rect(0, 0, contentW, contentH));

            if (strokes.Count > 0 && inkCoordinateW > 0 && inkCoordinateH > 0)
            {
                dc.PushTransform(new ScaleTransform(
                    contentW / inkCoordinateW, contentH / inkCoordinateH));
                foreach (var stroke in strokes)
                    stroke.Draw(dc);   // 与 StrokeCollection.Draw(dc) 等价（其内部即逐笔 Stroke.Draw）
                dc.Pop();
            }

            if (rotation != 0) dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(outputW, outputH, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>在独立 STA 线程合成 FixedDocument 并交给 XPS 写入器。</summary>
    public static Task<bool> SpoolAsync(
        List<int> indices, int copies, double areaW, double areaH,
        BitmapSource?[] pages, int[] rasterW, int[] rasterH,
        double[] pageDipW, double[] pageDipH,
        PrintTicket ticket, string queueName, PrintLayout layout)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    done.TrySetResult(await ComposeAndSpool(
                        indices, copies, areaW, areaH, pages, rasterW, rasterH,
                        pageDipW, pageDipH, ticket, queueName, layout));
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        });
        // XPS 序列化与打印后台在底层会访问单元线程 COM，必须 STA。
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "InkCanvasForPDF print spool";
        thread.Start();
        return done.Task;
    }

    private static async Task<bool> ComposeAndSpool(
        List<int> indices, int copies, double areaW, double areaH,
        BitmapSource?[] pages, int[] rasterW, int[] rasterH,
        double[] pageDipW, double[] pageDipH,
        PrintTicket ticket, string queueName, PrintLayout layout)
    {
        var fixedDoc = new FixedDocument();
        int sheetsPerCopy = (indices.Count + layout.NUp - 1) / layout.NUp;
        bool padForDuplex = layout.Duplex && copies > 1 && (sheetsPerCopy % 2 == 1);

        for (int copy = 0; copy < copies; copy++)
        {
            for (int start = 0; start < indices.Count; start += layout.NUp)
            {
                var chunk = indices.Skip(start).Take(layout.NUp).ToList();
                var page = new FixedPage { Width = areaW, Height = areaH };
                var sheet = ComposeSheet(chunk, areaW, areaH, pages, rasterW, rasterH,
                    pageDipW, pageDipH, layout);
                FixedPage.SetLeft(sheet, 0);
                FixedPage.SetTop(sheet, 0);
                page.Children.Add(sheet);
                page.Measure(new Size(areaW, areaH));
                page.Arrange(new Rect(new Point(), new Size(areaW, areaH)));

                var content = new PageContent();
                ((IAddChild)content).AddChild(page);
                fixedDoc.Pages.Add(content);
            }

            if (padForDuplex && copy < copies - 1)
            {
                var blank = new FixedPage { Width = areaW, Height = areaH };
                blank.Measure(new Size(areaW, areaH));
                blank.Arrange(new Rect(new Point(), new Size(areaW, areaH)));
                var content = new PageContent();
                ((IAddChild)content).AddChild(blank);
                fixedDoc.Pages.Add(content);
            }
        }

        // PrintQueue 的句柄属于创建它的线程：打印线程按名称重新打开自己的队列。
        using var server = new LocalPrintServer();
        using var queue = ResolveQueue(server, queueName);

        var writer = PrintQueue.CreateXpsDocumentWriter(queue);
        var spooled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.WritingCompleted += (_, ev) =>
        {
            if (ev.Error is not null) spooled.TrySetException(ev.Error);
            else if (ev.Cancelled) spooled.TrySetResult(false);
            else spooled.TrySetResult(true);
        };
        // 写 FixedDocument 本身而不是其 DocumentPaginator：后者会再包一层 FixedPage，
        // 触发“FixedPage cannot contain another FixedPage”。
        writer.WriteAsync(fixedDoc, ticket);
        return await spooled.Task;
    }

    /// <summary>按名称在调用线程重新打开打印队列；直接打开失败时回退到同机队列枚举匹配。</summary>
    private static PrintQueue ResolveQueue(LocalPrintServer server, string fullName)
    {
        try { return server.GetPrintQueue(fullName); }
        catch
        {
            var match = server
                .GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
                .FirstOrDefault(q => q.FullName == fullName);
            if (match != null) return match;
            throw;
        }
    }
}
