using System;
using System.Windows;
using System.Windows.Controls;
using iNKORE.UI.WPF.Modern.Controls;
using InkCanvasForPDF.Controls;

namespace InkCanvasForPDF;

public partial class MainWindow
{
    /// <summary>打印 PDF（文件菜单与工具栏图标按钮共用）：
    /// 在主窗口内弹出 iUWM ContentDialog，按钮为「打印」「取消」。</summary>
    private async void PrintPdf_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasPdf || _viewModel.Pages.Count == 0)
            return;

        PrintDialogView view;
        try
        {
            var source = _viewModel.CreatePrintSource();
            view = new PrintDialogView(source);
        }
        catch (Exception ex)
        {
            App.Log("PrintPdf.CreateSource", ex);
            ShowFloatingTip("打开打印对话框失败，请查看错误日志", InfoBarSeverity.Error);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "打印 PDF",
            Content = view,
            PrimaryButtonText = "打印",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Owner = this,
        };
        dialog.IsPrimaryButtonEnabled = false;   // 预览渲染完成前不可打印

        view.PrintEnabledChanged += enabled => dialog.IsPrimaryButtonEnabled = enabled;
        // 打印中不允许关闭（XPS 作业仍在后台写）。
        dialog.Closing += (_, args) =>
        {
            if (view.IsPrinting) args.Cancel = true;
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            args.Cancel = true;
            try
            {
                if (await view.RunPrintAsync())
                    args.Cancel = false;   // 成功才关闭对话框
            }
            finally
            {
                deferral.Complete();
            }
        };

        // iUWM ContentDialog 默认最大宽高 548×756，打印预览内容更宽；
        // 仅放宽最大宽高，弹窗实际宽高仍由内容决定。
        bool hadMaxW = Application.Current.Resources.Contains("ContentDialogMaxWidth");
        bool hadMaxH = Application.Current.Resources.Contains("ContentDialogMaxHeight");
        object? oldMaxW = Application.Current.Resources["ContentDialogMaxWidth"];
        object? oldMaxH = Application.Current.Resources["ContentDialogMaxHeight"];
        ConfigurePrintDialogMaxSize(dialog);
        dialog.Opened += (_, _) => ConfigurePrintDialogMaxSize(dialog);
        dialog.Loaded += (_, _) => ConfigurePrintDialogMaxSize(dialog);

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            App.Log("PrintPdf.Dialog", ex);
            ShowFloatingTip("打印对话框打开失败，请查看错误日志", InfoBarSeverity.Error);
        }
        finally
        {
            RestoreResource("ContentDialogMaxWidth", hadMaxW, oldMaxW);
            RestoreResource("ContentDialogMaxHeight", hadMaxH, oldMaxH);
            view.Cancel();   // 停止后台预览渲染并释放打印服务器

            if (view.PrintedPageCount > 0)
                ShowFloatingTip($"已发送 {view.PrintedPageCount} 页到打印机", InfoBarSeverity.Success);
        }
    }

    /// <summary>只放宽打印对话框的最大宽高：弹窗实际尺寸仍跟随内容，
    /// 仅替换 iUWM 默认 548×756 上限。模板部件在 Opened/Loaded 后才可获取，故会多次调用。</summary>
    private void ConfigurePrintDialogMaxSize(ContentDialog dialog)
    {
        double maxW = Math.Max(840, ActualWidth - 64);
        double maxH = Math.Max(480, ActualHeight - 64);

        // 对话框自身资源作用域（DynamicResource 回溯链）
        dialog.Resources["ContentDialogMaxWidth"] = maxW;
        dialog.Resources["ContentDialogMaxHeight"] = maxH;

        // 应用级资源兜底（finally 中还原）
        Application.Current.Resources["ContentDialogMaxWidth"] = maxW;
        Application.Current.Resources["ContentDialogMaxHeight"] = maxH;

        // 模板部件兜底：只改最大宽高，不写死实际宽高
        if (dialog.Template?.FindName("BackgroundElement", dialog) is Border background)
        {
            background.MaxWidth = maxW;
            background.MaxHeight = maxH;
        }
    }

    private static void RestoreResource(string key, bool hadLocalValue, object? value)
    {
        if (hadLocalValue)
            Application.Current.Resources[key] = value;
        else
            Application.Current.Resources.Remove(key);   // 移除临时本地覆盖，恢复主题字典原值
    }
}
