using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
                //    .NET 的 DeleteSubKey 在本机被拒（见 error.log），改走只校验子键 DELETE 权限的直删路径；
                //    删没删掉以回读为准，删不掉就原样抛出 → 外层红条文案不变。
                if (TryDeleteUserChoice())
                {
                    SHChangeNotify(0x08000000, 0x1000, IntPtr.Zero, IntPtr.Zero);
                    return true;
                }
                throw;
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

    /// <summary>删除 UserChoice 键：.NET DeleteSubKey 在本机被拒，依次尝试 RegDeleteKeyEx 直删、
    /// reg.exe delete（独立进程隐藏窗口），成败一律以回读键是否还在为准；过程异常只记日志。</summary>
    private static bool TryDeleteUserChoice()
    {
        // A) Win32 直删：只校验子键 DELETE 权限（父键 FullControl 已授予）
        try
        {
            const int errorFileNotFound = 2;
            int hr = RegDeleteKeyEx(new IntPtr(unchecked((int)0x80000001)) /*HKEY_CURRENT_USER*/,
                UserChoicePath, 0, 0);
            if (hr == 0 || hr == errorFileNotFound)
                return !UserChoiceExists();
            App.LogText($"FileAssociationService.DeleteUserChoice RegDeleteKeyEx hr={hr}");
        }
        catch (Exception ex)
        {
            App.Log("FileAssociationService.DeleteUserChoice.RegDeleteKeyEx", ex);
        }

        // B) reg.exe 兜底（与 .NET 无关的独立进程）
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = $"delete \"HKCU\\{UserChoicePath}\" /f",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is not null)
            {
                p.WaitForExit(5000);
                if (p.ExitCode == 0)
                    return !UserChoiceExists();
                App.LogText($"FileAssociationService.DeleteUserChoice reg.exe exit={p.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            App.Log("FileAssociationService.DeleteUserChoice.reg", ex);
        }

        return !UserChoiceExists();
    }

    /// <summary>UserChoice 键当前是否存在（删除是否生效的唯一判据）。</summary>
    private static bool UserChoiceExists()
    {
        using var k = Registry.CurrentUser.OpenSubKey(UserChoicePath);
        return k != null;
    }

    /// <summary>呼出系统「打开方式」对话框（占位 .pdf 仅取扩展名；用户选本应用后由系统写入带合法 hash 的
    /// UserChoice——第三方直写/删键必被 Deny 拦截，只能走系统入口）。
    /// 主路径本进程直调 SHOpenWithDialog：owner 保证对话框在主窗口之上、阻塞至关闭、返回 HRESULT 可落日志；
    /// hr≠0 回退 rundll32 OpenAs_RunDLL。Win10 起系统忽略注册类标志，"始终使用"是否提供由系统选择器决定。</summary>
    public static void OpenChooseAppDialog(IntPtr ownerHwnd)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "InkCanvasForPDF-associate.pdf");
            if (!File.Exists(path))
                File.WriteAllText(path, "%PDF-1.4\n%%EOF\n");

            var info = new OpenAsInfo
            {
                File = path,
                Class = null,                 // null = 用 File 的扩展名
                Flags = 0x00000001,           // OAIF_ALLOW_REGISTRATION：启用"始终使用"复选框（Win10 起被系统忽略，传之无害）；不传 OAIF_EXEC 避免打开占位文件
            };
            int hr = SHOpenWithDialog(ownerHwnd, ref info);   // 阻塞至对话框关闭（UI 线程嵌套消息循环，浮动通知计时器照常）
            if (hr == 0)
                return;
            App.LogText($"FileAssociationService.OpenChooseAppDialog SHOpenWithDialog hr=0x{hr:X8}");

            // 兜底：rundll32 标准启动方式（勿加 CreateNoWindow/UseShellExecute=false——子进程窗口可能被置隐藏/非前台）
            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"shell32.dll,OpenAs_RunDLL \"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            App.Log("FileAssociationService.OpenChooseAppDialog", ex);
        }
    }

    /// <summary>OPENASINFO（shlobj_core.h）：pcszFile + pcszClass + oaifInFlags，字段顺序即内存布局。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public uint Flags;
    }

    /// <summary>通知 Shell 关联已变更（刷新文件图标/打开方式缓存）。</summary>
    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>显示系统「打开方式」对话框（shlobj_core.h，Vista+；阻塞至关闭，返回 HRESULT）。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OpenAsInfo poainfo);

    /// <summary>Win32 直删子键（0 位句柄 = HKEY_CURRENT_USER，需子键 DELETE 权限）。</summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegDeleteKeyEx(IntPtr hKey, string lpSubKey, int samDesired, int reserved);
}
