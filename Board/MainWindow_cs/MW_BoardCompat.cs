#nullable disable warnings

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using iNKORE.UI.WPF.Modern.Controls;

namespace InkCanvasForPDF.Board
{
    /// <summary>画板兼容桩：浮动栏/未移植功能引用的元素与方法的画板替代实现。
    /// 形状绘制已正式移植（MW_ShapeDrawing.cs），其实现不再在桩内。</summary>
    public partial class BoardView
    {
        // ═══ Circle 兼容类型（ICA InkRecognizeHelper.Circle 的画板内兼容副本，形状绘制在用）═══

        /// <summary>ICA InkRecognizeHelper.Circle 的画板内兼容副本（缩放手势同步圆心/半径用）。</summary>
        private sealed class Circle
        {
            public Circle(Point centroid, double r, Stroke stroke)
            {
                Centroid = centroid;
                R = r;
                Stroke = stroke;
            }

            public Point Centroid { get; set; }
            public double R { get; set; }
            public Stroke Stroke { get; set; }
        }

        private readonly List<Circle> circles = new List<Circle>();

        // isFirstTouchCuboid / MouseTouchMove 已随形状绘制正式移植移入 MW_ShapeDrawing.cs

        // ═══ 浮动栏通知/元素桩 ═══

        // ICA MW_Notification.cs:18（通知窗口未移植：写日志代替）
        private async void ShowNotificationAsync(string notice, bool isShowImmediately = true)
        {
            await System.Threading.Tasks.Task.CompletedTask;
            Helpers.LogHelper.WriteLogToFile("画板通知: " + notice, Helpers.LogHelper.LogType.Info);
        }

        // ICA CursorIcon_Click（鼠标模式）：画板内切回浏览
        private void SymbolIconCursor_Click(object sender, RoutedEventArgs e)
        {
            HideSubPanelsImmediately();
            inkCanvas.EditingMode = default;
        }

        // ICA BtnHideInkCanvas_Click（隐藏墨迹层）：画板内无此模式，仅确保可见
        private void BtnHideInkCanvas_Click(object sender, RoutedEventArgs e)
        {
            inkCanvas.IsHitTestVisible = true;
            inkCanvas.Visibility = Visibility.Visible;
        }

        // ICA BtnClear_Click:974-1009（清屏，浮动栏元素分支裁剪）
        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            forceEraser = false;

            // ICA 979-992：先回到画笔再清屏，避免 TimeMachine 的相关 bug 影响
            // （非画笔模式下直接清屏会漏记清除项，切页重放历史时墨迹会复活）
            if (BoardPen.Opacity == 1)
            {
                PenIcon_Click(null, null);
            }

            ClearStrokes(false);
            inkCanvas.Children.Clear();

            CancelSingleFingerDragMode();
        }
    }
}
