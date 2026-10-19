using System.IO;
using Microsoft.Win32;

namespace InkCanvasForPDF.Services;

/// <summary>.pdf 文件关联注册（仅 HKCU，无需管理员）：
/// 注册带 DefaultIcon 的正经 ProgID，让关联文件显示 pdf.ico 图标；
/// 不碰 UserChoice（hash 保护，第三方写必被系统回滚）——用户需在"选择其他应用"里
/// 重选一次本应用，UserChoice 指到本 ProgID 后图标即生效。</summary>
internal static class FileAssociationService
{
    private const string ProgId = "InkCanvasForPDF.PDF";

    /// <summary>注册/刷新 .pdf 关联用 ProgID（幂等：每次启动覆盖写同一组值，不累积）。
    /// 失败仅记日志，不阻塞启动。</summary>
    public static void Register()
    {
        try
        {
            string exePath = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "InkCanvasForPDF.exe");
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "pdf.ico");
            if (!File.Exists(iconPath)) return;   // 图标未随包发布则不写，避免指向空路径

            using var classes = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}");
            classes.SetValue(null, "PDF 文档");                        // 显示名
            using (var icon = classes.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, iconPath);                          // 文件图标 = pdf.ico
            using (var cmd = classes.CreateSubKey(@"shell\open\command"))
                cmd.SetValue(null, $"\"{exePath}\" \"%1\"");            // 启动参数打开

            // 挂进 .pdf 的候选 ProgID 列表（"选择其他应用"里出现本应用的正经 ProgID）
            using var pdf = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids");
            pdf.SetValue(ProgId, string.Empty);
        }
        catch (Exception ex)
        {
            App.Log("FileAssociationService.Register", ex);
        }
    }

    /// <summary>UserChoice 键路径（Windows 8+ 对其加显式 Deny SetValue，禁止第三方直写）。</summary>
    private const string UserChoicePath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice";

    /// <summary>把 .pdf 设为本应用默认：已是默认先判成功 → 写扩展名默认 ProgID →
    /// 尝试直写 UserChoice\ProgId → 被 Deny 拦截则删除 UserChoice 键回落到 HKCU 类默认 ProgID。
    /// 失败返回 false 并带提示信息（交调用方弹框引导手动设置）。</summary>
    public static bool TrySetAsDefaultPdf(out string? error)
    {
        error = null;
        try
        {
            Register();   // 确保 ProgID / 图标 / 打开命令为当前 exe 的最新值

            // 1) 已是默认 → 直接成功（UserChoice 读权限人人有，不会抛）
            using (var cur = Registry.CurrentUser.OpenSubKey(UserChoicePath))
            {
                if (ProgId.Equals(cur?.GetValue("ProgId") as string, StringComparison.OrdinalIgnoreCase))
                {
                    SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0x1000, IntPtr.Zero, IntPtr.Zero);
                    return true;
                }
            }

            // 2) 扩展名默认 ProgID（HKCU 优先于 HKLM；删除 UserChoice 后系统回落到它）
            using (var ext = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf"))
                ext.SetValue(null, ProgId);

            // 3) 尝试直写 UserChoice（兼容未被 Deny 的环境）
            try
            {
                using var uc = Registry.CurrentUser.CreateSubKey(UserChoicePath);
                uc.SetValue("ProgId", ProgId);
            }
            catch (UnauthorizedAccessException)
            {
                // 4) 被显式 Deny SetValue 拦截 → 删掉 UserChoice 键（父键 FullControl，Deny 不含 DELETE），
                //    不重建：无 UserChoice 时系统按 HKCR 合并规则回落到第 2 步的 HKCU 类默认。
                using var parent = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf", writable: true);
                parent?.DeleteSubKey("UserChoice");
            }

            SHChangeNotify(0x08000000, 0x1000, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        catch (Exception ex)
        {
            App.Log("FileAssociationService.TrySetAsDefaultPdf", ex);
            error = $"设置默认程序失败，请手动在系统设置中设置：{ex.Message}";
            return false;
        }
    }

    /// <summary>通知 Shell 关联已变更（刷新文件图标/打开方式缓存）。</summary>
    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
