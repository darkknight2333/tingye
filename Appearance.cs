using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Tingye
{
    internal sealed class Appearance
    {
        public const string NightMode = "night";
        private const int CaptionColorAttribute = 35;
        private const int TextColorAttribute = 36;
        private const uint DefaultWindowColor = 0xFFFFFFFF;

        private sealed class Choice
        {
            public string Id { get; private set; }
            public string Label { get; private set; }
            public Choice(string id, string label) { Id = id; Label = label; }
            public override string ToString() { return Label; }
        }

        private readonly ComboBox picker;
        private readonly Dictionary<string, Brush> defaults = new Dictionary<string, Brush>();
        private static readonly Choice[] themes = {
            new Choice("green", "松林绿"), new Choice("blue", "雾海蓝"),
            new Choice("amber", "琥珀棕"), new Choice(NightMode, "夜间模式")
        };
        // Each row names a brush and its blue, amber and night variants. The green palette comes from XAML.
        private static readonly string[,] colors = {
            { "Ink", "#23374B", "#47332B", "#DCE3E1" },
            { "Muted", "#52677C", "#745B4D", "#A5B1AF" },
            { "Paper", "#F2F6FA", "#FCF4E9", "#1A2022" },
            { "Forest", "#23384D", "#483229", "#13191B" },
            { "SidebarText", "#F4F7FA", "#FFF6EB", "#D5DEDC" },
            { "SidebarMuted", "#BFCDDC", "#DFC5B2", "#A4B4B2" },
            { "Accent", "#355D84", "#864C38", "#A9C9BC" },
            { "AccentLine", "#587FA1", "#A2674D", "#7DA99A" },
            { "PrimaryFill", "#355D84", "#864C38", "#3B6257" },
            { "PrimaryText", "#FFFFFF", "#FFFFFF", "#F0F7F3" },
            { "FocusRing", "#98602E", "#885623", "#D6AC79" },
            { "Surface", "#FBFDFF", "#FFFAF3", "#22292C" },
            { "Soft", "#E6EDF5", "#F4E7DB", "#293236" },
            { "Selection", "#D9E7F5", "#F3DDC9", "#2D413D" },
            { "Line", "#CAD5E0", "#E2D0C0", "#425054" },
            { "SideHover", "#324E67", "#624638", "#243134" },
            { "SideSelection", "#405F7A", "#75503E", "#30433F" },
            { "SideFocus", "#ACCDED", "#F0CBA8", "#B1CFC1" },
            { "NoticeFill", "#FFF1DD", "#FFF1DD", "#382D23" },
            { "NoticeLine", "#DFC2A2", "#DFC2A2", "#7D6142" },
            { "NoticeText", "#69432D", "#69432D", "#F0C98F" },
            { "ErrorText", "#B22222", "#B22222", "#F2ABA2" },
        };

        public Appearance(Window window, Action<string> changed)
        {
            for (int i = 0; i < colors.GetLength(0); i++)
                defaults.Add(colors[i, 0], (Brush)window.FindResource(colors[i, 0]));
            picker = (ComboBox)window.FindName("ThemeChoice");
            picker.ItemsSource = themes;
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
                ApplyTitleBar(window, choice.Id == NightMode);
                changed(choice.Id);
            };
        }

        public void Restore(string id)
        {
            picker.SelectedIndex = Math.Max(0, Array.FindIndex(themes, theme => theme.Id == id));
        }

        public static void ApplyTitleBar(Window window, bool night)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                window.SourceInitialized += delegate { ApplyTitleBar(window, night); };
                return;
            }
            // Windows versions without caption color support leave their native title bar in place.
            uint caption = night ? ColorRef(window, "Forest") : DefaultWindowColor;
            uint text = night ? ColorRef(window, "Ink") : DefaultWindowColor;
            DwmSetWindowAttribute(handle, CaptionColorAttribute, ref caption, sizeof(uint));
            DwmSetWindowAttribute(handle, TextColorAttribute, ref text, sizeof(uint));
        }

        private static uint ColorRef(Window window, string key)
        {
            var color = ((SolidColorBrush)window.FindResource(key)).Color;
            return (uint)(color.R | color.G << 8 | color.B << 16);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

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
