using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Tingye
{
    internal sealed class Appearance
    {
        private sealed class Choice
        {
            public string Id { get; private set; }
            public string Label { get; private set; }
            public Choice(string id, string label) { Id = id; Label = label; }
            public override string ToString() { return Label; }
        }

        private readonly ComboBox picker;
        private readonly Dictionary<string, Brush> defaults = new Dictionary<string, Brush>();
        // Each row names a brush and its blue and amber variants. The green palette comes from XAML.
        private static readonly string[,] colors = {
            { "Ink", "#23374B", "#47332B" },
            { "Muted", "#52677C", "#745B4D" },
            { "Paper", "#F2F6FA", "#FCF4E9" },
            { "Forest", "#23384D", "#483229" },
            { "SidebarText", "#F4F7FA", "#FFF6EB" },
            { "SidebarMuted", "#BFCDDC", "#DFC5B2" },
            { "Accent", "#355D84", "#864C38" },
            { "AccentLine", "#587FA1", "#A2674D" },
            { "FocusRing", "#98602E", "#885623" },
            { "Surface", "#FBFDFF", "#FFFAF3" },
            { "Soft", "#E6EDF5", "#F4E7DB" },
            { "Selection", "#D9E7F5", "#F3DDC9" },
            { "Line", "#CAD5E0", "#E2D0C0" },
            { "SideHover", "#324E67", "#624638" },
            { "SideSelection", "#405F7A", "#75503E" },
            { "SideFocus", "#ACCDED", "#F0CBA8" },
        };

        public Appearance(Window window, Action<string> changed)
        {
            for (int i = 0; i < colors.GetLength(0); i++)
                defaults.Add(colors[i, 0], (Brush)window.FindResource(colors[i, 0]));
            picker = (ComboBox)window.FindName("ThemeChoice");
            picker.ItemsSource = new[] { new Choice("green", "松林绿"), new Choice("blue", "雾海蓝"), new Choice("amber", "琥珀棕") };
            picker.SelectionChanged += delegate {
                var choice = picker.SelectedItem as Choice;
                if (choice == null) return;
                int column = picker.SelectedIndex;
                for (int i = 0; i < colors.GetLength(0); i++)
                {
                    string key = colors[i, 0];
                    Brush brush = defaults[key];
                    if (column != 0)
                    {
                        brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i, column]));
                        brush.Freeze();
                    }
                    window.Resources[key] = brush;
                    // Owned dialogs and their button templates also resolve the active palette.
                    if (Application.Current != null) Application.Current.Resources[key] = brush;
                }
                changed(choice.Id);
            };
        }

        public void Restore(string id)
        {
            picker.SelectedIndex = id == "blue" ? 1 : id == "amber" ? 2 : 0;
        }

        public static void AnimateEntry(FrameworkElement element)
        {
            if (!element.IsVisible || !SystemParameters.ClientAreaAnimation) return;
            var duration = new Duration(TimeSpan.FromMilliseconds(170));
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var move = new TranslateTransform();
            element.RenderTransform = move;
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, duration) {
                EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.65, 1, duration) {
                EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        }

        public static void AnimateWhenShown(FrameworkElement element)
        {
            element.IsVisibleChanged += delegate { if (element.IsVisible) AnimateEntry(element); };
        }
    }
}
