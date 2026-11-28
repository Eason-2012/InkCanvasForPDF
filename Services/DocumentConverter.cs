using System.Runtime.InteropServices;

namespace InkCanvasForPDF.Services;

public static class DocumentConverter
{
    public static void Convert(bool useWPS, string inputPath, string outputPath)
    {
        Type? appCom = useWPS ? Type.GetTypeFromProgID("KWPS.Application")
                       ?? Type.GetTypeFromProgID("WPS.Application")
                       : Type.GetTypeFromProgID("Word.Application");
        if (appCom == null)
            throw new Exception("找不到对应的COM接口");
        dynamic? app = null;
        dynamic? doc = null;
        try
        {
            app = Activator.CreateInstance(appCom);
            if (app == null)
                throw new Exception("无法新建COM实例");
            app.Visible = false;
            app.DisplayAlerts = 0;
            doc = app.Documents.Open(inputPath, false, true);

            try
            {
                doc.ExportAsFixedFormat(outputPath, 17);
            }
            catch
            {
                doc.SaveAs(outputPath, 17);
            }
        }
        finally
        {
            try { doc?.Close(0); } catch { }
            Marshal.ReleaseComObject(doc);
            try { app?.Quit(0); } catch { }
            Marshal.ReleaseComObject(app);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}