using InkCanvasForPDF.Board.Helpers;
using InkCanvasForPDF.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace InkCanvasForPDF.Board
{
    public partial class BoardView
    {
        //此函数中的所有代码版权所有 WXRIW，在其他项目中使用前必须提前联系（wxriw@outlook.com），谢谢！
        // 注：InkToShape（墨迹识别）依赖 IACore.dll，按移植约定裁剪；仅保留伪压力与笔锋逻辑。
        private void inkCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            try
            {
                inkCanvas.Opacity = 1;

                if (inkColor > 100)
                { // 荧光笔功能（画板以 inkColor>100 判别）不参与笔锋
                    return;
                }

                // 采样链路加固（只作用于本条笔画）：极端离群点修复 + 稀疏笔画降级平滑
                StrokeSampleRepair.Apply(e.Stroke);
                StrokeSmoothingGuard.Apply(e.Stroke);

                // 墨迹纠正（ICA InkToShape 同位置：荧光笔排除之后、压感/笔锋改写之前）：命中则整笔换成规范图形
                _inkToShape.OnStrokeCollected(e.Stroke, inkCanvas.Strokes);

                // 时间笔锋（InkStyle=3）：湿墨阶段已由叠加层按时间生长实时呈现，此处只需把各点最终压力定格写回。
                // 必须提前于"压感归一"与"真压感 return"两道门——时间笔锋的封顶正是逐点硬件压力。
                if (Settings.Canvas.InkStyle == 3)
                {
                    // 多触摸模式：触摸设备由叠加层自绘，按它自己的设备会话定格；数位笔原生笔画走主设备会话
                    if (e.Stroke.ContainsPropertyData(TimePressureController.SelfDrawnDeviceGuid))
                        inkCanvas.Controller.FreezeFinalize(
                            (int)e.Stroke.GetPropertyData(TimePressureController.SelfDrawnDeviceGuid),
                            e.Stroke, _pressureEnabled);
                    else
                        inkCanvas.Controller.FreezeFinalize(e.Stroke, _pressureEnabled);
                    return;
                }

                // 屏蔽模式（0/1 笔锋·硬笔）的湿墨已由叠加层等宽呈现；会话由控制器在抬笔后清场，
                // 不能在此整页中止——多触摸模式下会误杀其他手指的湿墨。

                // 真压感判定（与压感归一合并为单次路径）：压感开启且存在真实压感输入（任一点压力≠0.5 且 ≠0）
                // → 保留硬件压力，不做假笔锋。压感关闭时跳过判定：下方笔锋公式输出只由位置/速度决定
                // （不读输入压力），原"先整笔 flatten 归一 0.5 → 再检测 → 再重建"的前置 flatten 对输出
                // 无贡献，省去一笔全量 StylusPointCollection 重建拷贝（case 0 的 n==1 例外在分支内处理）。
                if (_pressureEnabled)
                {
                    foreach (StylusPoint stylusPoint in e.Stroke.StylusPoints)
                    {
                        if ((stylusPoint.PressureFactor > 0.501 || stylusPoint.PressureFactor < 0.5) && stylusPoint.PressureFactor != 0)
                        {
                            return;
                        }
                    }
                }


                switch (Settings.Canvas.InkStyle)
                {
                    case 1:
                        try
                        {
                            StylusPointCollection stylusPoints = new StylusPointCollection();
                            int n = e.Stroke.StylusPoints.Count - 1;

                            for (int i = 0; i <= n; i++)
                            {
                                double speed = GetPointSpeed(e.Stroke.StylusPoints[Math.Max(i - 1, 0)].ToPoint(), e.Stroke.StylusPoints[i].ToPoint(), e.Stroke.StylusPoints[Math.Min(i + 1, n)].ToPoint());
                                StylusPoint point = new StylusPoint();
                                if (speed >= 0.25)
                                {
                                    point.PressureFactor = (float)(0.5 - 0.3 * (Math.Min(speed, 1.5) - 0.3) / 1.2);
                                }
                                else if (speed >= 0.05)
                                {
                                    point.PressureFactor = (float)0.5;
                                }
                                else
                                {
                                    point.PressureFactor = (float)(0.5 + 0.4 * (0.05 - speed) / 0.05);
                                }
                                point.X = e.Stroke.StylusPoints[i].X;
                                point.Y = e.Stroke.StylusPoints[i].Y;
                                stylusPoints.Add(point);
                            }
                            e.Stroke.StylusPoints = stylusPoints;
                        }
                        catch
                        {

                        }
                        break;
                    case 0:
                        try
                        {
                            StylusPointCollection stylusPoints = new StylusPointCollection();
                            int n = e.Stroke.StylusPoints.Count - 1;
                            double pressure = 0.1;
                            int x = 10;
                            if (n == 1)
                            {
                                // 2 点笔画不改写；压感关闭时仍需归一 0.5（等价原"flatten 先行"语义，
                                // 其余点数由下方重建统一覆盖，无需前置 flatten）
                                if (!_pressureEnabled)
                                {
                                    StylusPointCollection flattened2 = new StylusPointCollection();
                                    foreach (StylusPoint stylusPoint2 in e.Stroke.StylusPoints)
                                        flattened2.Add(new StylusPoint(stylusPoint2.X, stylusPoint2.Y, 0.5f));
                                    e.Stroke.StylusPoints = flattened2;
                                }
                                return;
                            }
                            if (n >= x)
                            {
                                for (int i = 0; i < n - x; i++)
                                {
                                    StylusPoint point = new StylusPoint();

                                    point.PressureFactor = (float)0.5;
                                    point.X = e.Stroke.StylusPoints[i].X;
                                    point.Y = e.Stroke.StylusPoints[i].Y;
                                    stylusPoints.Add(point);
                                }
                                for (int i = n - x; i <= n; i++)
                                {
                                    StylusPoint point = new StylusPoint();

                                    point.PressureFactor = (float)((0.5 - pressure) * (n - i) / x + pressure);
                                    point.X = e.Stroke.StylusPoints[i].X;
                                    point.Y = e.Stroke.StylusPoints[i].Y;
                                    stylusPoints.Add(point);
                                }
                            }
                            else
                            {
                                for (int i = 0; i <= n; i++)
                                {
                                    StylusPoint point = new StylusPoint();

                                    point.PressureFactor = (float)(0.4 * (n - i) / n + pressure);
                                    point.X = e.Stroke.StylusPoints[i].X;
                                    point.Y = e.Stroke.StylusPoints[i].Y;
                                    stylusPoints.Add(point);
                                }
                            }
                            e.Stroke.StylusPoints = stylusPoints;
                        }
                        catch
                        {

                        }
                        break;
                    case 2: // 关闭笔锋：不改写；等宽由硬笔 IgnorePressure 在渲染层实现（RefreshTimePressureArmed 同步），不再抬笔归一
                        break;
                    // case 3 时间笔锋：已在方法开头提前处理（FreezeFinalize 定格写回），此处不再有分支
                }
            }
            catch { }
        }

        public double GetDistance(Point point1, Point point2)
        {
            return Math.Sqrt((point1.X - point2.X) * (point1.X - point2.X) + (point1.Y - point2.Y) * (point1.Y - point2.Y));
        }

        public double GetPointSpeed(Point point1, Point point2, Point point3)
        {
            return (Math.Sqrt((point1.X - point2.X) * (point1.X - point2.X) + (point1.Y - point2.Y) * (point1.Y - point2.Y))
                + Math.Sqrt((point3.X - point2.X) * (point3.X - point2.X) + (point3.Y - point2.Y) * (point3.Y - point2.Y)))
                / 20;
        }

        public Point[] FixPointsDirection(Point p1, Point p2)
        {
            if (Math.Abs(p1.X - p2.X) / Math.Abs(p1.Y - p2.Y) > 8)
            {
                //水平
                double x = Math.Abs(p1.Y - p2.Y) / 2;
                if (p1.Y > p2.Y)
                {
                    p1.Y -= x;
                    p2.Y += x;
                }
                else
                {
                    p1.Y += x;
                    p2.Y -= x;
                }
            }
            else if (Math.Abs(p1.Y - p2.Y) / Math.Abs(p1.X - p2.X) > 8)
            {
                //垂直
                double x = Math.Abs(p1.X - p2.X) / 2;
                if (p1.X > p2.X)
                {
                    p1.X -= x;
                    p2.X += x;
                }
                else
                {
                    p1.X += x;
                    p2.X -= x;
                }
            }

            return new Point[2] { p1, p2 };
        }

        public StylusPointCollection GenerateFakePressureTriangle(StylusPointCollection points)
        {
            var newPoint = new StylusPointCollection();
            newPoint.Add(new StylusPoint(points[0].X, points[0].Y, (float)0.4));
            var cPoint = GetCenterPoint(points[0], points[1]);
            newPoint.Add(new StylusPoint(cPoint.X, cPoint.Y, (float)0.8));
            newPoint.Add(new StylusPoint(points[1].X, points[1].Y, (float)0.4));
            newPoint.Add(new StylusPoint(points[1].X, points[1].Y, (float)0.4));
            cPoint = GetCenterPoint(points[1], points[2]);
            newPoint.Add(new StylusPoint(cPoint.X, cPoint.Y, (float)0.8));
            newPoint.Add(new StylusPoint(points[2].X, points[2].Y, (float)0.4));
            newPoint.Add(new StylusPoint(points[2].X, points[2].Y, (float)0.4));
            cPoint = GetCenterPoint(points[2], points[0]);
            newPoint.Add(new StylusPoint(cPoint.X, cPoint.Y, (float)0.8));
            newPoint.Add(new StylusPoint(points[0].X, points[0].Y, (float)0.4));
            return newPoint;
        }

        public StylusPointCollection GenerateFakePressureRectangle(StylusPointCollection points)
        {
            var newPoint = new StylusPointCollection();
            newPoint.Add(new StylusPoint(points[0].X, points[0].Y, (float)0.4));
            var cPoint = GetCenterPoint(points[0], points[1]);
            newPoint.Add(new StylusPoint(cPoint.X, cPoint.Y, (float)0.8));
            newPoint.Add(new StylusPoint(points[1].X, points[1].Y, (float)0.4));
            newPoint.Add(new StylusPoint(points[1].X, points[1].Y, (float)0.4));
            cPoint = GetCenterPoint(points[1], points[2]);
            newPoint.Add(new StylusPoint(cPoint.X, cPoint.Y, (float)0.8));
            newPoint.Add(new StylusPoint(points[2].X, points[2].Y, (float)0.4));
            newPoint.Add(new StylusPoint(points[2].X, points[2].Y, (float)0.4));
            cPoint = GetCenterPoint(points[2], points[3]);
            newPoint.Add(new StylusPoint(cPoint.X, cPoint.Y, (float)0.8));
            newPoint.Add(new StylusPoint(points[3].X, points[3].Y, (float)0.4));
            newPoint.Add(new StylusPoint(points[3].X, points[3].Y, (float)0.4));
            cPoint = GetCenterPoint(points[3], points[0]);
            newPoint.Add(new StylusPoint(cPoint.X, cPoint.Y, (float)0.8));
            newPoint.Add(new StylusPoint(points[0].X, points[0].Y, (float)0.4));
            return newPoint;
        }

        public Point GetCenterPoint(Point point1, Point point2)
        {
            return new Point((point1.X + point2.X) / 2, (point1.Y + point2.Y) / 2);
        }

        public StylusPoint GetCenterPoint(StylusPoint point1, StylusPoint point2)
        {
            return new StylusPoint((point1.X + point2.X) / 2, (point1.Y + point2.Y) / 2);
        }
    }
}
