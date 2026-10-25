using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;

namespace InkCanvasForPDF;

/// <summary>未处理异常错误弹窗（独立 iuwm 现代窗口 + 云母背景，非对话框）：
/// 仅由 App 的未处理异常处理器调用，非模态 Show，单实例复用。</summary>
public partial class ErrorWindow : Window
{
    /// <summary>当前打开的弹窗实例（连续异常更新同一窗口，避免刷屏）。</summary>
    private static ErrorWindow? _current;

    public ErrorWindow()
    {
        InitializeComponent();
    }

    /// <summary>显示（或更新）错误弹窗。调用方自行兜底 try/catch，防止弹窗自身异常再入异常循环。</summary>
    internal static void ShowError(string source, Exception? ex)
    {
        var text = BuildErrorText(source, ex);
        if (_current != null)
        {
            _current.ErrorTextBox.Text = text;
            _current.Activate();
            return;
        }

        var window = new ErrorWindow { ErrorTextBox = { Text = text } };
        window.Closed += (_, _) => _current = null;
        _current = window;
        window.Show();   // 非模态：不阻塞主消息循环，关闭后应用继续运行
    }

    /// <summary>拼装可复制给开发者的错误信息（版本/时间/来源/系统/异常全量）。</summary>
    private static string BuildErrorText(string source, Exception? ex)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("InkCanvasForPDF v").Append(Services.UpdateService.CurrentVersion).AppendLine();
        sb.Append("时间：").AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.Append("来源：").AppendLine(source);
        sb.Append("系统：").Append(Environment.OSVersion)
          .Append(" / .NET ").AppendLine(Environment.Version.ToString());
        sb.Append("架构：").AppendLine(RuntimeInformation.ProcessArchitecture.ToString());
        if (ex != null)
        {
            sb.AppendLine(ex.ToString());
        }
        return sb.ToString();
    }

    /// <summary>复制信息：把错误详情全文写入剪贴板（失败仅记日志）。</summary>
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ErrorTextBox.Text);
        }
        catch (Exception ex)
        {
            App.Log("ErrorWindow.Copy", ex);
        }
    }

    /// <summary>向开发者报告问题：用系统默认浏览器打开 Tag 中的 Issue 地址。</summary>
    private void ReportIssue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink { Tag: string url })
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
