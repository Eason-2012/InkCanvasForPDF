using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using InkCanvasForPDF.Services;
using Sage.MarkdownRenderer;
using SageThemeMode = Sage.MarkdownRenderer.Html.ThemeMode;

namespace InkCanvasForPDF.Controls;

/// <summary>更新日志 Markdown 视图：Sage.MarkdownRenderer 经 WinForms WebView2 渲染，
/// 由 WindowsFormsHost 嵌入 WPF 弹窗。必须在 UI 线程创建。</summary>
public sealed class ChangelogMarkdownView : IDisposable
{
    public WindowsFormsHost Host { get; }
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _webView;
    private readonly MarkdownViewManager _manager;

    private ChangelogMarkdownView(Microsoft.Web.WebView2.WinForms.WebView2 webView,
        MarkdownViewManager manager, WindowsFormsHost host)
    {
        _webView = webView;
        _manager = manager;
        Host = host;
    }

    /// <summary>创建并初始化 WebView2（用户数据目录 = 运行目录 Data\WebView2）。</summary>
    public static async Task<ChangelogMarkdownView> CreateAsync(double width, double height)
    {
        var webView = new Microsoft.Web.WebView2.WinForms.WebView2
        {
            Dock = System.Windows.Forms.DockStyle.Fill,
        };
        var host = new WindowsFormsHost { Width = width, Height = height, Child = webView };
        var options = new MarkdownViewManagerOptions
        {
            Theme = ThemeService.IsDark ? SageThemeMode.Dark : SageThemeMode.Light,
            AutoLoadWelcomePage = false,
            DisableDefaultContextMenu = true,
            DisableDevTools = true,
            DisableScriptDialogs = true,
            UseExternalBrowserForLinks = true,
        };
        var manager = new MarkdownViewManager(webView, options);
        var dataFolder = Path.Combine(AppContext.BaseDirectory, "Data", "WebView2");
        Directory.CreateDirectory(dataFolder);
        await manager.InitializeAsync(dataFolder);
        return new ChangelogMarkdownView(webView, manager, host);
    }

    public Task ShowMarkdownAsync(string markdown) => _manager.ShowMarkdownAsync(markdown);

    /// <summary>WebView2 不可用时的纯文本回退视图（最大高 420，可滚动）。</summary>
    public static ScrollViewer CreatePlainTextFallback(string text) =>
        new()
        {
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(8),
            },
        };

    public void Dispose() => _webView.Dispose();
}
