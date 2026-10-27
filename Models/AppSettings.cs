using System.Text.Json.Serialization;

namespace InkCanvasForPDF.Models;

/// <summary>应用设置（JSON 属性名对齐 Ink-Canvas-Artistry Settings.cs，序列化到运行目录 Data\Settings.json）。</summary>
public class AppSettings
{
    /// <summary>是否显示画笔光标（仅批注 Ink 模式强制钢笔光标；抄 ICA isShowCursor）。</summary>
    [JsonPropertyName("isShowCursor")]
    public bool IsShowCursor { get; set; } = false;

    /// <summary>墨迹平滑：通过贝塞尔曲线美化墨迹（FitToCurve）。</summary>
    [JsonPropertyName("isFitToCurve")]
    public bool IsFitToCurve { get; set; } = true;

    /// <summary>墨迹纠正（InkToShape，抄 ICA Settings.InkToShape）；全局开关，PDF 墨迹栏与画板墨迹面板共享。默认关闭（抬笔纠正）。</summary>
    [JsonPropertyName("isInkToShapeEnabled")]
    public bool IsInkToShapeEnabled { get; set; } = false;

    /// <summary>多指书写：true=允许多指同时在 PDF 上书写（停用第 2 指停墨抑制）；false=第 2 指停墨让位双指手势。</summary>
    [JsonPropertyName("isMultiFingerInkEnabled")]
    public bool IsMultiFingerInkEnabled { get; set; } = false;

    /// <summary>墨迹识别相似度门槛（0~100，直接作为百分比门槛；值越大纠正越严格，默认 75）。</summary>
    [JsonPropertyName("inkToShapeSensitivity")]
    public int InkToShapeSensitivity { get; set; } = 75;

    /// <summary>停顿强制识别：书写中停顿超过设定时间即强制墨迹纠正；独立于 isInkToShapeEnabled。默认开启（停顿纠正）。</summary>
    [JsonPropertyName("isPauseInkToShapeEnabled")]
    public bool IsPauseInkToShapeEnabled { get; set; } = true;

    /// <summary>停顿强制识别相似度门槛（0~100，直接作为百分比门槛；默认 75）。</summary>
    [JsonPropertyName("pauseInkToShapeSensitivity")]
    public int PauseInkToShapeSensitivity { get; set; } = 75;

    /// <summary>停顿触发强制识别时间（ms，50~5000，默认 1000）。</summary>
    [JsonPropertyName("pauseInkToShapeDelayMs")]
    public int PauseInkToShapeDelayMs { get; set; } = 1000;

    /// <summary>停顿容错值（px，0~100）：停顿期间位移 ≤ 该值不重置计时；默认 5。</summary>
    [JsonPropertyName("pauseInkToShapeTolerancePx")]
    public int PauseInkToShapeTolerancePx { get; set; } = 5;

    /// <summary>墨迹类型记忆：0=硬笔（关压感），1=软笔（开压感），2=荧光笔（上次启用的页签）。</summary>
    [JsonPropertyName("inkType")]
    public int InkType { get; set; } = 0;

    /// <summary>画笔共用记忆色索引（硬笔/软笔共用，0~8 画笔色；默认 1=红）。</summary>
    [JsonPropertyName("penColor")]
    public int PenColor { get; set; } = 1;

    /// <summary>画笔共用记忆粗细（硬笔/软笔共用，1~30，默认 5）。</summary>
    [JsonPropertyName("penWidth")]
    public double PenWidth { get; set; } = 5;

    /// <summary>荧光笔记忆色索引：101~106（默认 103=黄）。</summary>
    [JsonPropertyName("highlighterColor")]
    public int HighlighterColor { get; set; } = 103;

    /// <summary>荧光笔记忆粗细（1~30，默认 5）。</summary>
    [JsonPropertyName("highlighterWidth")]
    public double HighlighterWidth { get; set; } = 5;

    /// <summary>橡皮粗细滑块值（1~30，实际擦除直径 = 15 + 3.5 × 值 → 18.5~120）。</summary>
    [JsonPropertyName("eraserSizeSetting")]
    public int EraserSizeSetting { get; set; } = 10;

    /// <summary>橡皮模式记忆：false=面积擦，true=墨迹擦（PDF 橡皮菜单与画板橡皮菜单共享）。</summary>
    [JsonPropertyName("eraserModeByStrokeSetting")]
    public bool EraserModeByStrokeSetting { get; set; } = false;

    /// <summary>清空墨迹后自动切换模式（PDF 橡皮菜单滑动清空）：0=保持在橡皮，1=切换到鼠标，2=切换到墨迹，3=切换到选择。默认 1=鼠标。</summary>
    [JsonPropertyName("clearInkAutoSwitchMode")]
    public int ClearInkAutoSwitchMode { get; set; } = 1;

    /// <summary>画板橡皮菜单滑动清空后自动切换模式，取值同上；默认 2=墨迹（与画板清屏前先回画笔的既有行为一致）。与 PDF 侧不互通。</summary>
    [JsonPropertyName("boardClearInkAutoSwitchMode")]
    public int BoardClearInkAutoSwitchMode { get; set; } = 2;

    /// <summary>手掌擦除：书写模式下大面积接触（手掌）触发面积擦（仅 EraseByPoint，尺寸=触点宽×k）。PDF 与画板共享。默认启用。</summary>
    [JsonPropertyName("isPalmEraseEnabled")]
    public bool IsPalmEraseEnabled { get; set; } = true;

    /// <summary>笔尖模式：手掌擦除开启时生效——改用笔尖档接触面积阈值（关=手指档）。手掌擦除关闭时 UI 禁用。</summary>
    [JsonPropertyName("isNibModeEnabled")]
    public bool IsNibModeEnabled { get; set; } = false;

    /// <summary>笔尖模式接触面积阈值（ICA nibModeBoundsWidth 默认 10）。</summary>
    [JsonPropertyName("nibModeBoundsWidth")]
    public int NibModeBoundsWidth { get; set; } = 10;

    /// <summary>手指档接触面积阈值（ICA fingerModeBoundsWidth 默认 30）。</summary>
    [JsonPropertyName("fingerModeBoundsWidth")]
    public int FingerModeBoundsWidth { get; set; } = 30;

    /// <summary>是否显示浮动工具栏文字注释（抄 ICA isEnableDisPlayFloatBarText）。</summary>
    [JsonPropertyName("isEnableDisPlayFloatBarText")]
    public bool IsEnableDisPlayFloatBarText { get; set; } = false;

    /// <summary>批注笔锋：0=点集笔锋，1=速率笔锋，2=关闭笔锋，3=时间笔锋（新装机默认时间笔锋；下拉框第一位的显示映射见 SettingsView.PenStyleMap）。</summary>
    [JsonPropertyName("inkStyle")]
    public int InkStyle { get; set; } = 3;

    /// <summary>时间笔锋墨迹点长满时间（ms，10~1000）：数字越大笔锋越明显；仅时间笔锋模式生效。</summary>
    [JsonPropertyName("timePenGrowMs")]
    public int TimePenGrowMs { get; set; } = 50;

    /// <summary>荧光笔启用压力感应：开启后荧光笔笔画粗细跟随压力大小动态变化（默认关闭 = 等粗）。</summary>
    [JsonPropertyName("highlighterPressureEnabled")]
    public bool HighlighterPressureEnabled { get; set; } = false;

    /// <summary>批注画笔粗细（1~30）。</summary>
    [JsonPropertyName("inkWidth")]
    public double InkWidth { get; set; } = 3;

    /// <summary>批注颜色索引：0~8 画笔色，101~106 荧光笔色（默认 1=红）。</summary>
    [JsonPropertyName("inkColor")]
    public int InkColor { get; set; } = 1;

    /// <summary>荧光笔不透明度（20~240，对应 alpha；荧光笔页签专属记忆）。</summary>
    [JsonPropertyName("inkAlpha")]
    public int InkAlpha { get; set; } = 120;

    /// <summary>调色盘主题：true=亮系，false=暗系（默认暗系）。</summary>
    [JsonPropertyName("useLightThemeColor")]
    public bool UseLightThemeColor { get; set; } = false;

    /// <summary>颜色主题：0=浅色，1=深色，2=跟随系统（抄 ICA Settings.Appearance.Theme；默认跟随系统）。</summary>
    [JsonPropertyName("theme")]
    public int Theme { get; set; } = 2;

    /// <summary>形状面板固定状态（true=固定：仅图形/批注按钮可关面板；false=自动隐藏：点形状或点外部即关）。</summary>
    [JsonPropertyName("isShapePanelPinned")]
    public bool IsShapePanelPinned { get; set; } = false;

    /// <summary>保留双曲线渐近线（PDF 与画板通用）：0=是（保留）、1=否（画完即移除）、2=每次询问。</summary>
    [JsonPropertyName("hyperbolaAsymptoteOption")]
    public int HyperbolaAsymptoteOption { get; set; } = 2;   // 默认每次询问（对齐画板旧默认 Ask）

    /// <summary>应用强调色（hex，如 #005FB0；作用于 iNKORE 控件选中态/开关/滑块）。</summary>
    [JsonPropertyName("accentColor")]
    public string AccentColor { get; set; } = "#005FB0";

    /// <summary>PDF 墨迹栏缩放（0.5~1.5，应用到 ViewboxFloatingBarScaleTransform）。</summary>
    [JsonPropertyName("viewboxFloatingBarScale")]
    public double FloatingBarScale { get; set; } = 1.0;

    /// <summary>PDF 墨迹栏底部边距（px，默认 16 与原 XAML 硬编码一致）。</summary>
    [JsonPropertyName("viewboxFloatingBarBottomMargin")]
    public double FloatingBarBottomMargin { get; set; } = 16;

    /// <summary>PDF 墨迹栏右侧边距（px，默认 16 与原 XAML 硬编码一致）。</summary>
    [JsonPropertyName("viewboxFloatingBarRightMargin")]
    public double FloatingBarRightMargin { get; set; } = 16;

    /// <summary>PDF 墨迹栏显示背景色（抄 ICA isColorfulViewboxFloatingBar，渐变画刷）。</summary>
    [JsonPropertyName("isColorfulViewboxFloatingBar")]
    public bool IsColorfulViewboxFloatingBar { get; set; } = false;

    /// <summary>PDF 墨迹栏背景透明度（0~1，默认 0.75 与原视觉一致；仅非彩色模式生效）。</summary>
    [JsonPropertyName("viewboxFloatingBarOpacity")]
    public double FloatingBarOpacity { get; set; } = 0.75;

    /// <summary>黑板墨迹栏透明度（0~1，默认 0.75 与原视觉一致，作用于三条 BoardBar 整体）。</summary>
    [JsonPropertyName("viewboxBlackboardOpacity")]
    public double BlackboardBarOpacity { get; set; } = 0.75;

    /// <summary>黑板墨迹栏缩放（0.5~1.5，应用到画板三组 ViewboxBlackboard*ScaleTransform）。</summary>
    [JsonPropertyName("viewboxBlackboardScale")]
    public double BlackboardBarScale { get; set; } = 1.0;

    /// <summary>黑板墨迹栏底部边距（px，默认 5）。</summary>
    [JsonPropertyName("viewboxBlackboardBottomMargin")]
    public double BlackboardBarBottomMargin { get; set; } = 5;

    // ══ 数据存储 ══

    /// <summary>自动保存墨迹书写数据（侧车存储到 Data\Ink，不修改原 PDF）。关闭=完全停用（不读不写）。</summary>
    [JsonPropertyName("isAutoSaveInk")]
    public bool IsAutoSaveInk { get; set; } = true;

    /// <summary>启动时自动删除超过指定天数的墨迹书写数据。</summary>
    [JsonPropertyName("isAutoCleanInk")]
    public bool IsAutoCleanInk { get; set; } = false;

    /// <summary>自动删除墨迹书写数据的保留天数（1~3650，默认 30）。</summary>
    [JsonPropertyName("autoCleanInkDays")]
    public int AutoCleanInkDays { get; set; } = 30;

    // ══ 应用更新 ══

    /// <summary>启用自动检查更新：启动时自动检查并下载更新，退出应用时自动安装更新。</summary>
    [JsonPropertyName("isAutoCheckUpdate")]
    public bool IsAutoCheckUpdate { get; set; } = true;

    /// <summary>更新通道：0=稳定通道（仅正式 Release），1=测试通道（含 Pre-release）。</summary>
    [JsonPropertyName("updateChannel")]
    public int UpdateChannel { get; set; } = 0;

    /// <summary>更新代理 URL 前缀（拼在 GitHub 请求地址前），默认 gh-proxy.org，留空为裸连 GitHub。</summary>
    [JsonPropertyName("updateProxy")]
    public string UpdateProxy { get; set; } = "https://gh-proxy.org/";

    /// <summary>上次检查更新时间（展示用，yyyy/M/d HH:mm:ss）。</summary>
    [JsonPropertyName("lastUpdateCheckTime")]
    public string LastUpdateCheckTime { get; set; } = "";

    /// <summary>已下载待安装的更新版本（空=无待安装更新；版本 ≤ 当前版本时启动自清理）。</summary>
    [JsonPropertyName("downloadedUpdateVersion")]
    public string DownloadedUpdateVersion { get; set; } = "";

    /// <summary>已展示过更新日志弹窗的版本（空=从未展示；与当前版本不同且存在对应 Release 时首启弹窗）。</summary>
    [JsonPropertyName("shownChangelogVersion")]
    public string ShownChangelogVersion { get; set; } = "";
}