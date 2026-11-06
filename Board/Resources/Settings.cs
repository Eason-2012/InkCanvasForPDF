using System.Text.Json.Serialization;

namespace InkCanvasForPDF.Board
{
    /// <summary>画板设置（ICA Settings 精简移植；2026-11 并入 Data\Settings.json 的 board 节，
    /// 原 exe 根目录独立 Settings.json 已废弃）。仅保留画板代码实际读写的字段，
    /// PPT/更新/随机点名/外观等 0 引用死字段已删除。</summary>
    public class Settings
    {
        [JsonPropertyName("advanced")]
        public Advanced Advanced { get; set; } = new Advanced();
        [JsonPropertyName("automation")]
        public Automation Automation { get; set; } = new Automation();
        [JsonPropertyName("behavior")]
        public PowerPointSettings PowerPointSettings { get; set; } = new PowerPointSettings();
        [JsonPropertyName("canvas")]
        public Canvas Canvas { get; set; } = new Canvas();
        [JsonPropertyName("gesture")]
        public Gesture Gesture { get; set; } = new Gesture();
    }

    public class Canvas
    {
        // 不入 Settings.json：以下墨迹菜单记忆随 view_state 按 PDF 存墨迹库
        [JsonIgnore]
        public double InkWidth { get; set; } = 2.5;
        [JsonIgnore]
        public int InkAlpha { get; set; } = 140;
        [JsonPropertyName("inkStyle")]
        public int InkStyle { get; set; } = 3; // 3=时间笔锋（新装机默认；下拉框显示顺序映射见 BoardPenStyleMap，0=点集 1=速率 2=关闭）
        [JsonPropertyName("usingWhiteboard")]
        public bool UsingWhiteboard { get; set; }

        // ────── 墨迹菜单按类型记忆（画板与 PDF 侧互相独立；不入 Settings.json，随 view_state 按 PDF 存墨迹库）──────
        [JsonIgnore]
        public int LastPenKind { get; set; } = 0; // 上次启用的画笔类型：0=硬笔（关压感） 1=软笔（开压感） 2=荧光笔
        [JsonIgnore]
        public int PenColor { get; set; } = 5; // 画笔共用记忆色索引（硬笔/软笔共用，0~8；进画板恢复记忆色，仅非法/与画布同色时黑白兜底）
        [JsonIgnore]
        public double PenWidth { get; set; } = 2.5; // 画笔共用记忆粗细（语义同 InkWidth = 滑块值一半 → 默认滑块 5）
        [JsonIgnore]
        public int HighlighterColor { get; set; } = 103; // 荧光笔记忆色索引（101~106）
        [JsonIgnore]
        public double HighlighterWidth { get; set; } = 2.5; // 荧光笔记忆粗细（= 滑块值一半 → 默认滑块 5）
    }

    public class Gesture
    {
        [JsonIgnore]
        public bool IsEnableTwoFingerGesture => IsEnableTwoFingerZoom || IsEnableTwoFingerTranslate || IsEnableTwoFingerRotation;
        [JsonIgnore]
        public bool IsEnableTwoFingerGestureTranslateOrRotation => IsEnableTwoFingerTranslate || IsEnableTwoFingerRotation;
        // 手势菜单 4 开关：不入 Settings.json，随 view_state 按 PDF 存墨迹库
        [JsonIgnore]
        public bool IsEnableMultiTouchMode { get; set; } = true;
        [JsonIgnore]
        public bool IsEnableTwoFingerZoom { get; set; } = true;
        [JsonIgnore]
        public bool IsEnableTwoFingerTranslate { get; set; } = true;
        [JsonIgnore]
        public bool IsEnableTwoFingerRotation { get; set; } = false;
        [JsonPropertyName("isEnableTwoFingerRotationOnSelection")]
        public bool IsEnableTwoFingerRotationOnSelection { get; set; } = false;
    }

    /// <summary>PPT/演示相关（原 behavior 节）：仅保留画板触摸控制实际读取的 2 项，其余 14 项死字段已删。</summary>
    public class PowerPointSettings
    {
        [JsonPropertyName("isEnableTwoFingerGestureInPresentationMode")]
        public bool IsEnableTwoFingerGestureInPresentationMode { get; set; } = false;
        [JsonPropertyName("isEnableFingerGestureSlideShowControl")]
        public bool IsEnableFingerGestureSlideShowControl { get; set; } = true;
    }

    public class Automation
    {
        [JsonPropertyName("isSaveScreenshotsInDateFolders")]
        public bool IsSaveScreenshotsInDateFolders { get; set; } = false;

        [JsonPropertyName("isAutoSaveStrokesAtScreenshot")]
        public bool IsAutoSaveStrokesAtScreenshot { get; set; } = false;

        [JsonPropertyName("isAutoSaveStrokesAtClear")]
        public bool IsAutoSaveStrokesAtClear { get; set; } = false;

        [JsonPropertyName("minimumAutomationStrokeNumber")]
        public int MinimumAutomationStrokeNumber { get; set; } = 0;

        [JsonPropertyName("autoSavedStrokesLocation")]
        public string AutoSavedStrokesLocation { get; set; } = @"D:\Ink Canvas";
    }

    public class Advanced
    {
        [JsonPropertyName("isSpecialScreen")]
        public bool IsSpecialScreen { get; set; } = false;

        [JsonPropertyName("isQuadIR")]
        public bool IsQuadIR { get; set; } = false;

        [JsonPropertyName("touchMultiplier")]
        public double TouchMultiplier { get; set; } = 0.25;
    }
}
