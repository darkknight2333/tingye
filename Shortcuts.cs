using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using System.Xml.Linq;

namespace Tingye
{
    internal enum ShortcutAction { Playback, PreviousChapter, NextChapter, Sidebar, Import, Search }

    internal sealed class ShortcutBinding : INotifyPropertyChanged
    {
        public ShortcutBinding(ShortcutAction action, string label, Key key, ModifierKeys modifiers = ModifierKeys.None)
        {
            Action = action;
            Label = label;
            Set(key, modifiers);
        }

        public ShortcutAction Action { get; private set; }
        public string Label { get; private set; }
        public Key Key { get; private set; }
        public ModifierKeys Modifiers { get; private set; }
        public string KeyLabel { get; private set; }

        public void Set(Key key, ModifierKeys modifiers)
        {
            Key = key;
            Modifiers = modifiers;
            KeyLabel = Shortcuts.Format(key, modifiers);
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("KeyLabel"));
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal sealed class Shortcuts
    {
        private const ModifierKeys AllowedModifiers = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;
        public readonly ShortcutBinding[] Bindings = {
            new ShortcutBinding(ShortcutAction.Playback, "播放 / 暂停", Key.Space),
            new ShortcutBinding(ShortcutAction.PreviousChapter, "上一章", Key.Left, ModifierKeys.Alt),
            new ShortcutBinding(ShortcutAction.NextChapter, "下一章", Key.Right, ModifierKeys.Alt),
            new ShortcutBinding(ShortcutAction.Sidebar, "展开 / 收起侧栏", Key.B, ModifierKeys.Control),
            new ShortcutBinding(ShortcutAction.Import, "导入 EPUB", Key.O, ModifierKeys.Control),
            new ShortcutBinding(ShortcutAction.Search, "搜索书架", Key.F, ModifierKeys.Control)
        };

        public ShortcutBinding this[ShortcutAction action]
        {
            get { return Bindings.First(binding => binding.Action == action); }
        }

        public ShortcutBinding Match(Key key, ModifierKeys modifiers)
        {
            return Bindings.FirstOrDefault(binding => binding.Key == key && binding.Modifiers == modifiers);
        }

        public static Key EventKey(KeyEventArgs args)
        {
            return args.Key == Key.System ? args.SystemKey : args.Key;
        }

        public static bool IsModifier(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin;
        }

        public static bool CanAssign(Key key, ModifierKeys modifiers)
        {
            if ((modifiers & ~AllowedModifiers) != 0 ||
                ((key == Key.F4 || key == Key.Space) && (modifiers & ModifierKeys.Alt) != 0))
                return false;
            if (modifiers == ModifierKeys.None)
                return key == Key.Space || (key >= Key.A && key <= Key.Z) ||
                    (key >= Key.D0 && key <= Key.D9) || (key >= Key.F1 && key <= Key.F24);
            return key == Key.Space || (key >= Key.PageUp && key <= Key.Down) ||
                (key >= Key.D0 && key <= Key.Z) || (key >= Key.NumPad0 && key <= Key.F24) ||
                (key >= Key.Oem1 && key <= Key.Oem3) || (key >= Key.Oem4 && key <= Key.Oem8);
        }

        public static string Format(Key key, ModifierKeys modifiers)
        {
            string label;
            switch (key)
            {
                case Key.Space: label = "空格"; break;
                case Key.Left: label = "←"; break;
                case Key.Right: label = "→"; break;
                case Key.Up: label = "↑"; break;
                case Key.Down: label = "↓"; break;
                default: label = new KeyConverter().ConvertToString(null, CultureInfo.InvariantCulture, key); break;
            }
            return ((modifiers & ModifierKeys.Control) != 0 ? "Ctrl + " : "") +
                ((modifiers & ModifierKeys.Alt) != 0 ? "Alt + " : "") +
                ((modifiers & ModifierKeys.Shift) != 0 ? "Shift + " : "") + label;
        }

        public XElement Save()
        {
            return new XElement("shortcuts", Bindings.Select(binding => new XElement("shortcut",
                new XAttribute("action", binding.Action), new XAttribute("key", binding.Key),
                new XAttribute("modifiers", binding.Modifiers))));
        }

        public static Shortcuts Load(XElement element)
        {
            var result = new Shortcuts();
            if (element == null) return result;
            foreach (var item in element.Elements("shortcut"))
            {
                ShortcutAction action;
                Key key;
                ModifierKeys modifiers;
                if (!Enum.TryParse((string)item.Attribute("action"), out action) ||
                    !Enum.IsDefined(typeof(ShortcutAction), action) ||
                    !Enum.TryParse((string)item.Attribute("key"), out key) ||
                    !Enum.TryParse((string)item.Attribute("modifiers"), out modifiers) ||
                    !CanAssign(key, modifiers)) continue;
                result[action].Set(key, modifiers);
            }
            return result.Bindings.GroupBy(binding => new { binding.Key, binding.Modifiers }).Any(group => group.Count() > 1)
                ? new Shortcuts() : result;
        }
    }
}
