using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace InkCanvasForPDF.Controls;

/// <summary>弹出菜单统一滑入/滑出动画：150ms CircleEase 纯位移（无淡入淡出，Opacity 恒为 1）。
/// 滑入 EaseOut（由快到慢）：From=面板实际高度 → 0，面板从完全位于终点下方整块完全滑入；
/// 滑出 EaseIn（由慢到快）：当前 Y → 面板实际高度，Storyboard.Completed 后才真正 IsOpen=false。
/// FlyOut 幂等：滑出进行中重复调用为 no-op（点外关闭/HideSubPanels 等重复触发安全）。</summary>
public static class PopupFlyAnimator
{
    private static readonly Duration Duration = TimeSpan.FromMilliseconds(150);
    private static readonly Dictionary<Popup, Storyboard> Running = new();   // 每个 Popup 至多一个动画
    private static readonly HashSet<Popup> Closing = new();                   // 滑出中标志

    /// <summary>该 Popup 是否处于滑出动画中（toggle 分支据此中止并重开，避免死点）。</summary>
    public static bool IsClosing(Popup popup) => Closing.Contains(popup);

    /// <summary>滑入。停掉该 Popup 残留动画，预设初态（整块在终点下方）并播 150ms EaseOut。
    /// 由 Popup.Opened 经 Dispatcher（Loaded 优先级）调用：布局已完成、首帧未呈现，ActualHeight 可用。</summary>
    public static void FlyIn(Popup popup, FrameworkElement root)
    {
        Stop(popup);
        EnsureMeasured(root);   // 首开时子树可能尚未布局：先取真实高度，避免 SlideDistance 退化为 10px 兜底
        double h = SlideDistance(root);
        root.Opacity = 1;   // 纯滑入：不淡入，恒不透明
        root.RenderTransformOrigin = new Point(0.5, 1);
        root.RenderTransform = new TranslateTransform(0, h);

        var slide = new DoubleAnimation
        {
            From = h,
            To = 0,
            Duration = Duration,
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }   // 由快到慢
        };
        Storyboard.SetTarget(slide, root);
        Storyboard.SetTargetProperty(slide, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

        var sb = new Storyboard();
        sb.Children.Add(slide);
        sb.Begin(root, true);
        Running[popup] = sb;
    }

    /// <summary>滑出（幂等）。从当前位置播 150ms EaseIn 滑到面板完全位于终点下方，
    /// Completed 后才真正 popup.IsOpen=false（Closed 事件及其清理随之触发）。</summary>
    public static void FlyOut(Popup popup, FrameworkElement root)
    {
        if (Closing.Contains(popup))
            return;   // 已在滑出中：重复触发（点外关闭/HideSubPanels）忽略

        double from = 0;
        if (root.RenderTransform is TranslateTransform t)
            from = t.Y;   // 滑入未播完即关：保留当前 Y 作为起点，防止两条动画打架
        else
        {
            root.RenderTransformOrigin = new Point(0.5, 1);
            root.RenderTransform = new TranslateTransform(0, 0);
        }
        double h = SlideDistance(root);
        if (h < from)
            h = from;

        Stop(popup);   // 停掉进行中的滑入

        var slide = new DoubleAnimation
        {
            From = from,
            To = h,
            Duration = Duration,
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseIn }   // 由慢到快
        };
        Storyboard.SetTarget(slide, root);
        Storyboard.SetTargetProperty(slide, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

        var sb = new Storyboard();
        sb.Children.Add(slide);
        sb.Completed += (_, _) =>
        {
            popup.IsOpen = false;   // 动画播完才真正关闭（Closed 清理随之触发）
            Running.Remove(popup);
            Closing.Remove(popup);
        };
        sb.Begin(root, true);
        Running[popup] = sb;
        Closing.Add(popup);
    }

    /// <summary>中止滑出（toggle 重开用）：停动画、清标志、立即 IsOpen=false。</summary>
    public static void AbortFlyOut(Popup popup)
    {
        if (!Closing.Contains(popup))
            return;
        Stop(popup);
        Closing.Remove(popup);
        popup.IsOpen = false;
    }

    /// <summary>停掉该 Popup 当前动画（保留当前属性值作为后续 From 起点）。</summary>
    private static void Stop(Popup popup)
    {
        if (Running.TryGetValue(popup, out var sb))
        {
            sb.Stop();
            Running.Remove(popup);
        }
    }

    /// <summary>Popup 子树关闭期间脱离视觉树、ActualHeight 恒为 0（首开时预设初态会取到 0 → 首帧闪在终点位置）。
    /// 首次打开前按 Popup 同款约束（无限尺寸）同步 Measure+Arrange 一次，取到真实高度；
    /// 已测量过则为 no-op（二次起不重复开销）。</summary>
    public static void EnsureMeasured(FrameworkElement root)
    {
        if (root.ActualHeight >= 1)
            return;
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
    }

    /// <summary>整块滑出所需位移 = 面板实际高度（RenderTransform 在本地坐标系生效，
    /// LayoutTransform 缩放作用于其外，无需乘缩放系数）；布局未完成时兜底 10px。</summary>
    private static double SlideDistance(FrameworkElement root)
        => root.ActualHeight > 0 ? root.ActualHeight : 10;
}
