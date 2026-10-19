using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using InkCanvasForPDF.Models;
using iNKORE.UI.WPF.Modern.Controls;
using Newtonsoft.Json.Linq;

namespace InkCanvasForPDF.Services;

/// <summary>应用更新状态区的六种状态。</summary>
public enum UpdateState
{
    /// <summary>已是最新（ArrowSync，无进度条）。</summary>
    UpToDate,
    /// <summary>正在检查最新版本（ApprovalsApp，不确定进度条）。</summary>
    Checking,
    /// <summary>正在下载最新版本（ArrowDownload，确定进度条）。</summary>
    Downloading,
    /// <summary>更新发生错误（ErrorCircle，错误进度条）。</summary>
    Error,
    /// <summary>已关闭自动更新（Pause，中断进度条）。</summary>
    AutoUpdateOff,
    /// <summary>已下载最新版本（ArrowClockwise，无进度条，按钮=立即更新）。</summary>
    ReadyToInstall,
}

/// <summary>检查结果：新版本号（去 v 前缀）+ 对应架构安装包下载地址 + Release 正文 + 是否 Pre-release。</summary>
public sealed record UpdateCheckResult(string Version, string AssetUrl, string ReleaseBody, bool IsPrerelease);

/// <summary>更新服务（版本源 = GitHub Releases API，结构抄 ICA AutoUpdateHelper）：
/// 检查版本 → 低优先级后台下载安装包 → 状态机/进度通知 → 启动安装器。
/// 状态与进度为进程内静态共享，供设置页与 MainWindow 共同订阅。</summary>
public static class UpdateService
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/Eason-2012/InkCanvasForPDF/releases";

    /// <summary>共享 HttpClient（超时关闭，各请求自行用 CancellationToken 限时）。</summary>
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static bool _busy;

    /// <summary>当前状态机状态（仅 UI 线程读写）。</summary>
    public static UpdateState State { get; private set; } = UpdateState.UpToDate;

    /// <summary>下载进度百分比（0~100）。</summary>
    public static double DownloadProgress { get; private set; }

    /// <summary>最近一次错误的中文原因（Error 态副标题）。</summary>
    public static string ErrorMessage { get; private set; } = "";

    /// <summary>待下载/待安装的新版本号（Downloading 态副标题显示，无 v 前缀）。</summary>
    public static string PendingVersion { get; private set; } = "";

    /// <summary>状态变化通知（UI 线程触发）。</summary>
    public static event Action? StateChanged;

    /// <summary>下载进度变化通知（UI 线程触发）。</summary>
    public static event Action? ProgressChanged;

    /// <summary>本进程是否已启动过安装器（「立即更新」启动后退出时不再触发静默安装）。</summary>
    public static bool InstallLaunched { get; private set; }

    static UpdateService()
    {
        // GitHub API 必带 User-Agent，否则 403。
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("InkCanvasForPDF");
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    // ══════════ 路径 ══════════

    /// <summary>本机架构对应的安装包资产名（ICPDF_x64_setup.exe 等）。</summary>
    public static string SetupFileName => $"ICPDF_{GetArchitecture()}_setup.exe";

    /// <summary>下载的安装包路径（运行目录 Data\Updates\，与 Data\error.log 同约定）。</summary>
    public static string GetSetupPath() =>
        Path.Combine(AppContext.BaseDirectory, "Data", "Updates", SetupFileName);

    private static string GetArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        _ => "x64",
    };

    private static Version LocalVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    /// <summary>当前版本号（三段，如 0.20.0，与 Release tag 对齐）。</summary>
    public static string CurrentVersion => $"{LocalVersion.Major}.{LocalVersion.Minor}.{Math.Max(LocalVersion.Build, 0)}";

    // ══════════ 状态机 ══════════

    /// <summary>启动时按设置/待安装标记初始化状态（待装包存在 → ReadyToInstall，否则 UpToDate；
    /// 自动检查开关的影响由设置页渲染时映射，服务不感知）。</summary>
    public static void Initialize(AppSettings s)
    {
        if (!string.IsNullOrEmpty(s.DownloadedUpdateVersion) && File.Exists(GetSetupPath()))
        {
            PendingVersion = s.DownloadedUpdateVersion;
            State = UpdateState.ReadyToInstall;
        }
        else
        {
            State = UpdateState.UpToDate;
        }
    }

    private static void SetState(UpdateState state)
    {
        State = state;
        StateChanged?.Invoke();
    }

    /// <summary>是否存在已下载待安装的更新包（退出自动安装的触发条件）。</summary>
    public static bool HasPendingInstall(AppSettings s) =>
        !string.IsNullOrEmpty(s.DownloadedUpdateVersion) && File.Exists(GetSetupPath());

    /// <summary>启动自清理：待装版本 ≤ 当前版本（说明已装完）时清空标记并存盘，避免重复安装。</summary>
    public static void CleanupInstalledMarker(AppSettings s)
    {
        if (string.IsNullOrEmpty(s.DownloadedUpdateVersion))
            return;
        if (Version.TryParse(s.DownloadedUpdateVersion, out var v) && v <= LocalVersion)
        {
            s.DownloadedUpdateVersion = "";
            SettingsService.Save(s);
        }
    }

    /// <summary>更新通道/代理变更后使已下载待装包失效：清空标记（调用方负责存盘）并回到 UpToDate 态。</summary>
    public static void InvalidatePendingInstall(AppSettings s)
    {
        if (string.IsNullOrEmpty(s.DownloadedUpdateVersion))
            return;
        s.DownloadedUpdateVersion = "";
        PendingVersion = "";
        if (State == UpdateState.ReadyToInstall)
            SetState(UpdateState.UpToDate);
    }

    // ══════════ 检查 ══════════

    /// <summary>拉取 Releases 列表（带代理前缀）。</summary>
    private static async Task<JArray> FetchReleasesAsync(AppSettings s, CancellationToken ct)
    {
        var url = (s.UpdateProxy ?? "") + ReleasesApiUrl;
        using var response = await Client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return JArray.Parse(json);
    }

    /// <summary>调 GitHub Releases API 比较版本号；有更新返回结果，无更新返回 null。</summary>
    /// <exception cref="InvalidOperationException">Release 中缺少本机架构安装包。</exception>
    public static async Task<UpdateCheckResult?> CheckForUpdatesAsync(AppSettings s, CancellationToken ct)
    {
        var releases = await FetchReleasesAsync(s, ct);

        foreach (var item in releases.OfType<JObject>())
        {
            var isPrerelease = item.Value<bool?>("prerelease") ?? false;
            if (s.UpdateChannel == 0 && isPrerelease)   // 稳定通道跳过 Pre-release
                continue;

            var tag = item.Value<string>("tag_name") ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var remote))
                continue;                               // 列表按发布时间倒序，第一个可解析的即通道最新版

            if (remote <= LocalVersion)
                return null;                            // 已是最新

            var assetUrl = FindAsset(item, SetupFileName)
                ?? throw new InvalidOperationException($"版本 {tag} 未提供 {SetupFileName} 安装包");
            var body = item.Value<string>("body") ?? "";
            return new UpdateCheckResult(remote.ToString(), assetUrl, body, isPrerelease);
        }

        return null;
    }

    private static string? FindAsset(JObject release, string fileName)
    {
        foreach (var asset in release["assets"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            if (asset.Value<string>("name") == fileName)
                return asset.Value<string>("browser_download_url");
        }
        return null;
    }

    /// <summary>取指定版本 Release 的更新日志正文（供更新日志弹窗）；无匹配返回 null。</summary>
    public static async Task<string?> GetReleaseNotesAsync(AppSettings s, string version, CancellationToken ct = default)
    {
        var releases = await FetchReleasesAsync(s, ct);
        foreach (var item in releases.OfType<JObject>())
        {
            var tag = item.Value<string>("tag_name") ?? "";
            if (string.Equals(tag.TrimStart('v', 'V'), version, StringComparison.OrdinalIgnoreCase))
                return item.Value<string>("body") ?? "";
        }
        return null;
    }

    /// <summary>取当前通道最新 Release 的正文（供「查看更新日志」按钮，不比较版本号）；通道内无可解析版本返回 null。</summary>
    public static async Task<string?> GetLatestReleaseNotesAsync(AppSettings s, CancellationToken ct = default)
    {
        var releases = await FetchReleasesAsync(s, ct);
        foreach (var item in releases.OfType<JObject>())
        {
            var isPrerelease = item.Value<bool?>("prerelease") ?? false;
            if (s.UpdateChannel == 0 && isPrerelease)
                continue;
            var tag = item.Value<string>("tag_name") ?? "";
            if (Version.TryParse(tag.TrimStart('v', 'V'), out _))
                return item.Value<string>("body") ?? "";
        }
        return null;
    }

    // ══════════ 下载 ══════════

    /// <summary>低优先级后台线程流式下载安装包到 Data\Updates\，按字节回报进度；失败删除半截文件。</summary>
    public static Task DownloadAsync(UpdateCheckResult r, AppSettings s, IProgress<double>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            // 满足「下载更新要在低优先级后台完成」。
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

            var url = (s.UpdateProxy ?? "") + r.AssetUrl;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = Client.Send(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var target = GetSetupPath();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            long total = response.Content.Headers.ContentLength ?? -1;
            long done = 0;
            double lastReported = -1;
            var buffer = new byte[81920];

            try
            {
                using var source = response.Content.ReadAsStream();
                using var destination = File.Create(target);
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    destination.Write(buffer, 0, read);
                    done += read;
                    if (total > 0)
                    {
                        var percent = Math.Floor(done * 100.0 / total);
                        if (percent != lastReported)
                        {
                            lastReported = percent;
                            progress?.Report(percent);
                        }
                    }
                }
                progress?.Report(100);
            }
            catch
            {
                try { File.Delete(target); } catch { /* 半截文件清理失败不阻塞 */ }
                throw;
            }
        }, ct);

    // ══════════ 主流程（手动检查与启动自动检查共用）══════════

    /// <summary>检查 → 下载 → 状态流转的完整流程。</summary>
    /// <param name="s">当前设置（检查时间/待装标记经此存盘）。</param>
    /// <param name="manual">true=设置页手动检查（失败弹通知）；false=启动自动检查（失败静默记日志）。</param>
    /// <param name="notify">浮动通知回调（开始下载/下载完成始终播报，仅失败分手动自动）。</param>
    public static async Task RunCheckAndDownloadAsync(AppSettings s, bool manual, Action<string, InfoBarSeverity>? notify)
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            SetState(UpdateState.Checking);

            UpdateCheckResult? result;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                result = await CheckForUpdatesAsync(s, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Fail("连接 GitHub 超时", manual, notify);
                return;
            }
            catch (Exception ex)
            {
                Fail(FriendlyReason(ex), manual, notify);
                return;
            }

            s.LastUpdateCheckTime = DateTime.Now.ToString("yyyy/M/d HH:mm:ss");
            SettingsService.Save(s);

            if (result == null)
            {
                SetState(UpdateState.UpToDate);
                return;
            }

            PendingVersion = result.Version;

            // 包已在（同版本）：不重复下载，直接进入待安装态。
            if (s.DownloadedUpdateVersion == result.Version && File.Exists(GetSetupPath()))
            {
                SetState(UpdateState.ReadyToInstall);
                return;
            }

            notify?.Invoke("正在下载最新版本", InfoBarSeverity.Success);
            DownloadProgress = 0;
            SetState(UpdateState.Downloading);

            try
            {
                var progress = new Progress<double>(p =>
                {
                    DownloadProgress = p;
                    ProgressChanged?.Invoke();
                });
                await DownloadAsync(result, s, progress, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                Fail("下载已取消", manual, notify);
                return;
            }
            catch (Exception ex)
            {
                Fail(FriendlyReason(ex), manual, notify);
                return;
            }

            s.DownloadedUpdateVersion = result.Version;
            SettingsService.Save(s);
            notify?.Invoke("已下载最新版本更新包，关闭应用以完成更新", InfoBarSeverity.Success);
            SetState(UpdateState.ReadyToInstall);
        }
        finally
        {
            _busy = false;
        }

        void Fail(string reason, bool show, Action<string, InfoBarSeverity>? notifyCallback)
        {
            ErrorMessage = reason;
            SetState(UpdateState.Error);
            if (show)
                notifyCallback?.Invoke($"更新失败：{reason}", InfoBarSeverity.Error);
            else
                App.LogText("AutoUpdate 检查失败：" + reason);
        }
    }

    /// <summary>网络/IO 异常 → 用户可读的中文原因。</summary>
    private static string FriendlyReason(Exception ex) => ex switch
    {
        HttpRequestException => "无法连接到 GitHub（网络错误）",
        TaskCanceledException => "连接超时",
        IOException => "下载中断（网络错误）",
        InvalidOperationException => ex.Message,     // 资产缺失等带说明的业务异常
        _ => ex.Message,
    };

    // ══════════ 安装 ══════════

    /// <summary>启动安装包：restart=true → "/SILENT /restart=1"（立即更新：显示进度窗口，装完拉起应用）；
    /// restart=false → "/VERYSILENT"（退出自动更新：完全不显示窗口，装完不重启）。
    /// 等待所有实例退出与防多开由 Inno Setup 实现（PrepareToInstall 轮询 / [Code] CreateMutex）。</summary>
    public static void StartInstall(string setupPath, bool restart)
    {
        InstallLaunched = true;
        Process.Start(new ProcessStartInfo
        {
            FileName = setupPath,
            Arguments = restart ? "/SILENT /restart=1" : "/VERYSILENT",
            UseShellExecute = true,
        });
    }
}
