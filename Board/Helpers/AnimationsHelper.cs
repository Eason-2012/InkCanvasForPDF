using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace InkCanvasForPDF.Board.Helpers
{
    internal class AnimationsHelper
    {
        public static void ShowWithSlideFromBottomAndFade(UIElement element, double duration = 0.15)
        {
            try
            {
                if (element.Visibility == Visibility.Visible) return;

                if (element == null)
                    throw new ArgumentNullException(nameof(element));

                var sb = new Storyboard();

                // 渐变动画（To=当前基础值：尊重 ApplyBoardBarLayout 设置的不透明度，不硬编码 1）
                var fadeInAnimation = new DoubleAnimation
                {
                    From = 0.5,
                    To = element.Opacity,
                    Duration = TimeSpan.FromSeconds(duration)
                };
                Storyboard.SetTargetProperty(fadeInAnimation, new PropertyPath(UIElement.OpacityProperty));

                // 滑动动画
                var slideAnimation = new DoubleAnimation
                {
                    From = element.RenderTransform.Value.OffsetY + 10, // 滑动距离
                    To = 0,
                    Duration = TimeSpan.FromSeconds(duration)
                };
                Storyboard.SetTargetProperty(slideAnimation, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

                sb.Children.Add(fadeInAnimation);
                sb.Children.Add(slideAnimation);
                // 动画完成即释放 Opacity hold（FillBehavior.HoldEnd 会永久覆盖本地值，导致设置的不透明度不生效）
                sb.Completed += (_, _) => element.BeginAnimation(UIElement.OpacityProperty, null);

                element.Visibility = Visibility.Visible;
                element.RenderTransform = new TranslateTransform();

                sb.Begin((FrameworkElement)element);
            }
            catch { }
        }

        public static void ShowWithSlideFromLeftAndFade(UIElement element, double duration = 0.25)
        {
            try
            {
                if (element.Visibility == Visibility.Visible) return;

                if (element == null)
                    throw new ArgumentNullException(nameof(element));

                var sb = new Storyboard();

                // 渐变动画（To=当前基础值：尊重设置的不透明度，不硬编码 1）
                var fadeInAnimation = new DoubleAnimation
                {
                    From = 0.5,
                    To = element.Opacity,
                    Duration = TimeSpan.FromSeconds(duration)
                };
                Storyboard.SetTargetProperty(fadeInAnimation, new PropertyPath(UIElement.OpacityProperty));

                // 滑动动画
                var slideAnimation = new DoubleAnimation
                {
                    From = element.RenderTransform.Value.OffsetX - 20, // 滑动距离
                    To = 0,
                    Duration = TimeSpan.FromSeconds(duration)
                };
                Storyboard.SetTargetProperty(slideAnimation, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));

                sb.Children.Add(fadeInAnimation);
                sb.Children.Add(slideAnimation);
                // 动画完成即释放 Opacity hold（FillBehavior.HoldEnd 会永久覆盖本地值，导致设置的不透明度不生效）
                sb.Completed += (_, _) => element.BeginAnimation(UIElement.OpacityProperty, null);

                element.Visibility = Visibility.Visible;
                element.RenderTransform = new TranslateTransform();

                sb.Begin((FrameworkElement)element);
            }
            catch { }
        }

        public static void ShowWithScaleFromLeft(UIElement element, double duration = 0.5)
        {
            try
            {
                if (element.Visibility == Visibility.Visible) return;

                if (element == null)
                    throw new ArgumentNullException(nameof(element));

                var sb = new Storyboard();

                // 水平方向的缩放动画
                var scaleXAnimation = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = TimeSpan.FromSeconds(duration)
                };
                Storyboard.SetTargetProperty(scaleXAnimation, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));

                // 垂直方向的缩放动画
                var scaleYAnimation = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = TimeSpan.FromSeconds(duration)
                };
                Storyboard.SetTargetProperty(scaleYAnimation, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));

                sb.Children.Add(scaleXAnimation);
                sb.Children.Add(scaleYAnimation);

                element.Visibility = Visibility.Visible;
                element.RenderTransformOrigin = new Point(0, 0.5); // 左侧中心点为基准
                element.RenderTransform = new ScaleTransform(0, 0);

                sb.Begin((FrameworkElement)element);
            }
            catch { }
        }

        public static void ShowWithScaleFromRight(UIElement element, double duration = 0.5)
        {
            try
            {
                if (element.Visibility == Visibility.Visible) return;

                if (element == null)
                    throw new ArgumentNullException(nameof(element));

                var sb = new Storyboard();

                // 水平方向的缩放动画
                var scaleXAnimation = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = TimeSpan.FromSeconds(duration)
                };
                Storyboard.SetTargetProperty(scaleXAnimation, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));

                // 垂直方向的缩放动画
                var scaleYAnimation = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = TimeSpan.FromSeconds(duration)
                };
                Storyboard.SetTargetProperty(scaleYAnimation, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));

                sb.Children.Add(scaleXAnimation);
                sb.Children.Add(scaleYAnimation);

                element.Visibility = Visibility.Visible;
                element.RenderTransformOrigin = new Point(1, 0.5); // 右侧中心点为基准
                element.RenderTransform = new ScaleTransform(0, 0);

                sb.Begin((FrameworkElement)element);
            }
            catch { }
        }

        public static void ShowWithScaleFromBottom(UIElement element, double duration = 0.5)
        {
            try
            {
                if (element.Visibility == Visibility.Visible) return;

                if (element == null)
                    throw new ArgumentNullException(nameof(element));

                var sb = new Storyboard();

                // 水平方向的缩放动画
                var scaleXAnimation = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = TimeSpan.FromSeconds(duration)
                };
                Storyboard.SetTargetProperty(scaleXAnimation, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));

                // 垂直方向的缩放动画
                var scaleYAnimation = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = TimeSpan.FromSeconds(duration)
                };
                Storyboard.SetTargetProperty(scaleYAnimation, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));

                sb.Children.Add(scaleXAnimation);
                sb.Children.Add(scaleYAnimation);

                element.Visibility = Visibility.Visible;
                element.RenderTransformOrigin = new Point(0.5, 1); // 底部中心点为基准
                element.RenderTransform = new ScaleTransform(1, 0);

                sb.Begin((FrameworkElement)element);
            }
            catch { }
        }

        public static void HideWithSlideAndFade(UIElement element, double duration = 0.15)
        {
            try
            {
                if (element.Visibility == Visibility.Collapsed) return;

                if (element == null)
                    throw new ArgumentNullException(nameof(element));

                var sb = new Storyboard();

                // 渐变动画（From=当前基础值：避免低不透明度下先跳到 1 再淡出的闪变）
                var fadeInAnimation = new DoubleAnimation
                {
                    From = element.Opacity,
                    To = 0,
                    Duration = TimeSpan.FromSeconds(duration)
                };
                Storyboard.SetTargetProperty(fadeInAnimation, new PropertyPath(UIElement.OpacityProperty));

                // 滑动动画
                var slideAnimation = new DoubleAnimation
                {
                    From = 0,
                    To = element.RenderTransform.Value.OffsetY + 10, // 滑动距离
                    Duration = TimeSpan.FromSeconds(duration)
                };
                Storyboard.SetTargetProperty(slideAnimation, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

                sb.Children.Add(fadeInAnimation);
                sb.Children.Add(slideAnimation);

                sb.Completed += (_, _) =>
                {
                    // 先释放 Opacity hold 再隐藏（hold 残留会覆盖后续 ApplyBoardBarLayout 的本地设置）
                    element.BeginAnimation(UIElement.OpacityProperty, null);
                    element.Visibility = Visibility.Collapsed;
                };

                element.RenderTransform = new TranslateTransform();
                sb.Begin((FrameworkElement)element);
            }
            catch { }
        }

    }
}
