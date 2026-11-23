using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Tingye
{
    internal sealed class BookOpening : FrameworkElement
    {
        private const double Centered = .25, Open = .33, Turn = .56;
        private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
            "Progress", typeof(double), typeof(BookOpening), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
        private readonly TaskCompletionSource<bool> completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string title;
        private readonly Rect origin;
        private readonly Brush background, paper, binding, ink;
        private readonly Color inkColor;
        private readonly Brush rules, shadow;
        private readonly Pen pageEdge;
        private readonly Brush fold;
        private ImageSource cover;
        private int pageCount;
        private bool introFinished, turning, turned, disposed;

        public BookOpening(string title, ImageSource cover, Rect origin, Brush background, Brush paper, Brush binding, Brush ink)
        {
            this.title = title;
            this.cover = cover;
            this.origin = origin;
            this.background = background;
            this.paper = paper;
            this.binding = binding;
            this.ink = ink;
            inkColor = ((SolidColorBrush)ink).Color;
            rules = Tint(37);
            shadow = new LinearGradientBrush(TintColor(68), TintColor(0), 0);
            fold = new LinearGradientBrush(TintColor(25), TintColor(3), 0);
            pageEdge = new Pen(Tint(63), 1);
            ClipToBounds = true;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        }

        public Task Finished { get { return completed.Task; } }

        public void Start()
        {
            var motion = new DoubleAnimation(0, Turn, TimeSpan.FromMilliseconds(710));
            motion.Completed += delegate {
                introFinished = true;
                StartPageTurns();
            };
            BeginAnimation(ProgressProperty, motion);
        }

        public void ReaderReady(ImageSource fullCover, double percent)
        {
            if (disposed || pageCount > 0) return;
            if (fullCover != null) cover = fullCover;
            pageCount = PagesForPercent(percent);
            InvalidateVisual();
            StartPageTurns();
        }

        internal static int PagesForPercent(double percent) { return percent >= 70 ? 3 : percent >= 30 ? 2 : 1; }

        private void StartPageTurns()
        {
            if (!introFinished || pageCount == 0 || turning || disposed) return;
            turning = true;
            var motion = new DoubleAnimation(Turn, 1, TimeSpan.FromMilliseconds(280 * pageCount));
            motion.Completed += delegate { turned = true; Reveal(); };
            BeginAnimation(ProgressProperty, motion);
        }

        private void Reveal()
        {
            if (!turned || disposed) return;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(170));
            fade.Completed += delegate { completed.TrySetResult(true); };
            BeginAnimation(OpacityProperty, fade);
        }

        public void End()
        {
            disposed = true;
            BeginAnimation(ProgressProperty, null);
            BeginAnimation(OpacityProperty, null);
            completed.TrySetResult(true);
        }

        protected override void OnRender(DrawingContext drawing)
        {
            var window = new Rect(RenderSize);
            if (window.Width < 1 || window.Height < 1) return;
            double time = (double)GetValue(ProgressProperty);
            double aspect = cover != null && cover.Width > 0 && cover.Height > 0 ? cover.Width / cover.Height : .72;
            var centered = Fit(new Rect(window.Width * .06, window.Height * .07, window.Width * .88, window.Height * .86), aspect);
            double lift = Ease(Between(time, 0, Centered));
            drawing.PushOpacity(lift);
            drawing.DrawRectangle(background, null, window);
            drawing.Pop();
            if (time < Open)
            {
                var start = origin.IsEmpty ? Fit(new Rect(centered.X + centered.Width * .08, centered.Y + centered.Height * .08,
                    centered.Width * .84, centered.Height * .84), aspect) : Fit(origin, aspect);
                if (origin.IsEmpty) drawing.PushOpacity(lift);
                DrawCover(drawing, Blend(start, centered, lift), true);
                if (origin.IsEmpty) drawing.Pop();
            }
            else if (time < Turn)
            {
                double unfold = Ease(Between(time, Open, Turn));
                DrawPaper(drawing, Blend(centered, window, unfold), 0, true);
                double horizontal = Math.Cos(unfold * Math.PI);
                if (Math.Abs(horizontal) > .002)
                {
                    drawing.PushOpacity(1 - Ease(Between(unfold, .76, 1)));
                    drawing.PushTransform(new ScaleTransform(horizontal, 1, centered.Left, centered.Top));
                    DrawCover(drawing, centered, horizontal > 0);
                    drawing.Pop();
                    drawing.Pop();
                }
            }
            else if (pageCount == 0)
            {
                DrawPaper(drawing, window, 0, false);
                DrawCaption(drawing, "正在打开…", window.Width / 2, window.Height * .90, ink, 15, window.Width * .8);
            }
            else
            {
                double distance = Between(time, Turn, 1) * pageCount;
                int page = Math.Min(pageCount - 1, (int)distance);
                DrawTurn(drawing, window, page, Ease(distance - page));
            }
        }

        private void DrawCover(DrawingContext drawing, Rect rectangle, bool front)
        {
            drawing.DrawRoundedRectangle(binding, pageEdge, rectangle, 5, 5);
            if (front && cover != null) drawing.DrawImage(cover, rectangle);
            else if (front) DrawCaption(drawing, title, rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2,
                Brushes.White, Math.Min(34, rectangle.Width * .11), rectangle.Width * .80);
            drawing.DrawRectangle(Tint(35), null, new Rect(rectangle.X, rectangle.Y, rectangle.Width * .035, rectangle.Height));
        }

        private void DrawCaption(DrawingContext drawing, string text, double x, double y, Brush color, double size, double maxWidth)
        {
            var caption = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), size, color, VisualTreeHelper.GetDpi(this).PixelsPerDip) {
                MaxTextWidth = Math.Max(1, maxWidth), MaxLineCount = 3, TextAlignment = TextAlignment.Center,
                Trimming = TextTrimming.CharacterEllipsis
            };
            drawing.DrawText(caption, new Point(x - caption.MaxTextWidth / 2, y - caption.Height / 2));
        }

        private void DrawPaper(DrawingContext drawing, Rect rectangle, int page, bool edge)
        {
            drawing.DrawRoundedRectangle(paper, edge ? pageEdge : null, rectangle, edge ? 4 : 0, edge ? 4 : 0);
            int columns = rectangle.Width / rectangle.Height > 1.28 ? 2 : 1;
            double columnWidth = rectangle.Width / columns;
            double step = rectangle.Height * .047;
            double weight = Math.Max(.5, Math.Min(2.4, step * .07));
            for (int column = 0; column < columns; column++)
            {
                double x = rectangle.X + columnWidth * (column + .14);
                for (int row = 0; row < 15; row++)
                {
                    double y = rectangle.Y + step * (row + 3) + (row / 5) * step * .31;
                    double length = row % 5 == 4 ? .46 : (row + page + column) % 4 == 0 ? .62 : .72;
                    drawing.DrawRoundedRectangle(rules, null, new Rect(x, y, columnWidth * length, weight), weight / 2, weight / 2);
                }
            }
        }

        private void DrawTurn(DrawingContext drawing, Rect window, int page, double amount)
        {
            DrawPaper(drawing, window, page + 1, false);
            if (amount >= 1) return;
            double edge = window.Width * (1 - amount);
            double curl = Math.Min(window.Width * .13, window.Height * .26) * Math.Sin(amount * Math.PI);
            double outer = edge + curl * .18, inner = edge - curl * .52;
            drawing.DrawRectangle(shadow, null, new Rect(Math.Max(0, inner), 0, curl * 1.9, window.Height));
            drawing.PushClip(PageShape(outer, curl, window.Height));
            DrawPaper(drawing, window, page, false);
            drawing.Pop();
            if (curl > .1)
            {
                var folded = new StreamGeometry();
                using (var path = folded.Open())
                {
                    path.BeginFigure(new Point(inner, 0), true, true);
                    path.BezierTo(new Point(inner + curl * .73, window.Height * .30), new Point(inner - curl * .41, window.Height * .66),
                        new Point(inner - curl * .54, window.Height), true, false);
                    path.LineTo(new Point(outer - curl * .54, window.Height), true, false);
                    path.BezierTo(new Point(outer - curl * .41, window.Height * .66), new Point(outer + curl * .73, window.Height * .30),
                        new Point(outer, 0), true, false);
                }
                drawing.DrawGeometry(fold, pageEdge, folded);
            }
        }

        private static StreamGeometry PageShape(double x, double curl, double height)
        {
            var shape = new StreamGeometry();
            using (var path = shape.Open())
            {
                path.BeginFigure(new Point(0, 0), true, true);
                path.LineTo(new Point(x, 0), true, false);
                path.BezierTo(new Point(x + curl * .73, height * .30), new Point(x - curl * .41, height * .66),
                    new Point(x - curl * .54, height), true, false);
                path.LineTo(new Point(0, height), true, false);
            }
            return shape;
        }

        private Color TintColor(byte alpha) { return Color.FromArgb(alpha, inkColor.R, inkColor.G, inkColor.B); }
        private Brush Tint(byte alpha) { return new SolidColorBrush(TintColor(alpha)); }
        private static double Between(double value, double start, double end) { return Math.Max(0, Math.Min(1, (value - start) / (end - start))); }
        private static double Ease(double t) { return t * t * (3 - 2 * t); }
        private static double Mix(double a, double b, double t) { return a + (b - a) * t; }
        private static Rect Blend(Rect a, Rect b, double t) { return new Rect(Mix(a.X, b.X, t), Mix(a.Y, b.Y, t), Mix(a.Width, b.Width, t), Mix(a.Height, b.Height, t)); }
        private static Rect Fit(Rect bounds, double aspect)
        {
            double width = Math.Min(bounds.Width, bounds.Height * aspect), height = width / aspect;
            return new Rect(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height);
        }
    }
}
