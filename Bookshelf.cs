using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;

namespace Tingye
{
    internal sealed class Bookshelf
    {
        private const int TileWidth = 178;
        private static readonly StringComparer AuthorOrder = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), true);
        private static readonly CompareInfo SearchComparer = CultureInfo.GetCultureInfo("zh-CN").CompareInfo;
        private const CompareOptions SearchOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreNonSpace;
        private static readonly RoutedCommand ToggleReadStatus = new RoutedCommand("ToggleReadStatus", typeof(Bookshelf));
        private static readonly RoutedCommand DeleteBookCommand = new RoutedCommand("DeleteBook", typeof(Bookshelf));
        private readonly Library library;
        private readonly ListBox rows;
        private readonly ComboBox sort, readFilter;
        private readonly RadioButton all, authors;
        private readonly FrameworkElement empty, noMatches;
        private readonly TextBox search;
        private readonly TextBlock count;
        private readonly Button resume;
        private readonly SemaphoreSlim coverSlots = new SemaphoreSlim(2, 2);
        private readonly Dictionary<string, ImageSource> covers = new Dictionary<string, ImageSource>();
        private readonly Queue<string> coverOrder = new Queue<string>();
        private readonly Dictionary<string, Task<ImageSource>> pendingCovers = new Dictionary<string, Task<ImageSource>>();
        private bool restoring;
        private bool rowsChanged = true;
        private int columns;

        public Bookshelf(Window window, Library library, Func<ShelfEntry, FrameworkElement, Task> open, Action<ShelfEntry> rename,
            Action<ShelfEntry> toggleRead, Action<ShelfEntry> delete, Action changed)
        {
            this.library = library;
            rows = (ListBox)window.FindName("ShelfRows");
            sort = (ComboBox)window.FindName("ShelfSort");
            readFilter = (ComboBox)window.FindName("ShelfReadFilter");
            all = (RadioButton)window.FindName("ShelfAll");
            authors = (RadioButton)window.FindName("ShelfAuthors");
            empty = (FrameworkElement)window.FindName("EmptyBookshelf");
            count = (TextBlock)window.FindName("BookCount");
            noMatches = (FrameworkElement)window.FindName("NoShelfMatches");
            search = (TextBox)window.FindName("ShelfSearch");
            var clearSearch = (Button)window.FindName("ClearShelfSearch");
            resume = (Button)window.FindName("ResumeBookButton");
            var filterTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
            filterTimer.Tick += (sender, args) => {
                filterTimer.Stop();
                Refresh();
                if (rows.Items.Count > 0) rows.ScrollIntoView(rows.Items[0]);
            };
            search.TextChanged += (sender, args) => {
                clearSearch.Visibility = search.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                filterTimer.Stop();
                filterTimer.Start();
            };
            clearSearch.Click += delegate { search.Clear(); search.Focus(); };
            rows.SizeChanged += (sender, args) => {
                if (rows.ActualWidth > 0 && Columns() != columns) Refresh();
            };
            sort.SelectionChanged += (sender, args) => {
                if (restoring || sort.SelectedIndex < 0) return;
                library.SortByReading = sort.SelectedIndex == 1;
                Refresh();
                if (rows.Items.Count > 0) rows.ScrollIntoView(rows.Items[0]);
                changed();
            };
            readFilter.SelectionChanged += (sender, args) => {
                if (restoring || readFilter.SelectedIndex < 0) return;
                library.ReadFilter = (ShelfReadFilter)readFilter.SelectedIndex;
                Refresh();
                if (rows.Items.Count > 0) rows.ScrollIntoView(rows.Items[0]);
                changed();
            };
            RoutedEventHandler selectView = (sender, args) => {
                if (restoring) return;
                library.GroupByAuthor = authors.IsChecked == true;
                Refresh();
                Appearance.AnimateEntry(count);
                if (rows.Items.Count > 0) rows.ScrollIntoView(rows.Items[0]);
                changed();
            };
            all.Checked += selectView;
            authors.Checked += selectView;
            rows.CommandBindings.Add(new CommandBinding(ApplicationCommands.Properties, (sender, args) => {
                var selected = args.Parameter as ShelfEntry;
                if (selected == null) return;
                args.Handled = true;
                rename(selected);
            }, (sender, args) => {
                args.CanExecute = rows.IsEnabled && args.Parameter is ShelfEntry;
                args.Handled = true;
            }));
            rows.CommandBindings.Add(new CommandBinding(ToggleReadStatus, (sender, args) => {
                var selected = args.Parameter as ShelfEntry;
                if (selected == null) return;
                args.Handled = true;
                toggleRead(selected);
            }, (sender, args) => {
                args.CanExecute = rows.IsEnabled && args.Parameter is ShelfEntry;
                args.Handled = true;
            }));
            rows.CommandBindings.Add(new CommandBinding(DeleteBookCommand, (sender, args) => {
                var selected = args.Parameter as ShelfEntry;
                if (selected == null) return;
                args.Handled = true;
                delete(selected);
            }, (sender, args) => {
                args.CanExecute = rows.IsEnabled && args.Parameter is ShelfEntry;
                args.Handled = true;
            }));
            rows.AddHandler(Button.ClickEvent, new RoutedEventHandler(async (sender, args) => {
                var button = args.OriginalSource as Button;
                var selected = button == null ? null : button.CommandParameter as ShelfEntry;
                if (selected == null) return;
                args.Handled = true;
                await open(selected, FindCover(button, selected));
            }), true);
            resume.Click += async (sender, args) => {
                var selected = library.Books.FirstOrDefault(item => item.Id == library.SelectedId);
                if (selected != null) await open(selected, FindCover(rows, selected));
            };
        }

        public ImageSource CachedCover(ShelfEntry selected)
        {
            ImageSource image;
            return covers.TryGetValue(selected.Id, out image) ? image : null;
        }

        private static FrameworkElement FindCover(DependencyObject parent, ShelfEntry selected)
        {
            var element = parent as FrameworkElement;
            var tile = element == null ? null : element.DataContext as ShelfTile;
            if (tile != null && tile.Entry == selected && element.Name == "ShelfCover" && element.IsVisible)
                return element;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var found = FindCover(VisualTreeHelper.GetChild(parent, i), selected);
                if (found != null) return found;
            }
            return null;
        }

        public void RestorePreferences()
        {
            restoring = true;
            sort.SelectedIndex = library.SortByReading ? 1 : 0;
            readFilter.SelectedIndex = (int)library.ReadFilter;
            if (library.GroupByAuthor) authors.IsChecked = true;
            else all.IsChecked = true;
            restoring = false;
            Refresh();
        }

        public void SetLoading(bool loading)
        {
            rows.IsEnabled = resume.IsEnabled = !loading;
        }

        public void FocusSearch()
        {
            search.Focus();
            search.SelectAll();
        }

        public void Refresh()
        {
            rowsChanged = false;
            columns = Columns();
            var terms = search.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            var filter = library.ReadFilter;
            var visible = library.Books.Where(item => (filter == ShelfReadFilter.All || item.IsRead == (filter == ShelfReadFilter.Read)) &&
                (terms.Length == 0 || Matches(item, terms)));
            var ordered = visible
                .OrderByDescending(item => library.SortByReading ? item.ReadUtc : item.ImportedUtc)
                .ThenByDescending(item => item.ImportedUtc).ToArray();
            var rebuilt = new List<ShelfRow>();
            if (library.GroupByAuthor)
            {
                foreach (var group in ordered.GroupBy(AuthorName, AuthorOrder)
                    .OrderBy(group => group.Key.Length == 0).ThenBy(group => group.Key, AuthorOrder))
                    AddRows(rebuilt, group.ToArray(), group.Key.Length == 0 ? "未署名作者" : group.Key);
            }
            else AddRows(rebuilt, ordered, null);
            rows.ItemsSource = rebuilt;
            count.Text = terms.Length == 0 && filter == ShelfReadFilter.All ? ordered.Length + " 本" : "找到 " + ordered.Length + " 本";
            empty.Visibility = library.Books.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            noMatches.Visibility = library.Books.Count > 0 && ordered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateResume();
        }

        public void InvalidateRows() { rowsChanged = true; }

        public void RefreshOnReturn()
        {
            if (rowsChanged || Columns() != columns) Refresh();
            else UpdateResume();
        }

        private void UpdateResume()
        {
            var recent = library.Books.FirstOrDefault(item => item.Id == library.SelectedId);
            resume.Visibility = recent == null ? Visibility.Collapsed : Visibility.Visible;
            resume.ToolTip = recent == null ? null : recent.DisplayTitle;
        }

        private static bool Matches(ShelfEntry entry, string[] terms)
        {
            string display = entry.DisplayTitle;
            return terms.All(term => Contains(display, term) || Contains(entry.Title, term) || Contains(entry.Author, term));
        }

        private static bool Contains(string source, string term)
        {
            return !String.IsNullOrEmpty(source) && SearchComparer.IndexOf(source, term, SearchOptions) >= 0;
        }

        private int Columns()
        {
            double width = rows.ActualWidth > 0 ? rows.ActualWidth : 854;
            return Math.Max(1, (int)((width - SystemParameters.VerticalScrollBarWidth - 4) / TileWidth));
        }

        private void AddRows(List<ShelfRow> result, ShelfEntry[] books, string title)
        {
            int rowSize = library.GroupByAuthor ? 1 : columns;
            for (int start = 0; start < books.Length; start += rowSize)
            {
                var tiles = new ShelfTile[Math.Min(rowSize, books.Length - start)];
                for (int i = 0; i < tiles.Length; i++)
                    tiles[i] = new ShelfTile(books[start + i], library.SortByReading, LoadCover);
                result.Add(new ShelfRow {
                    Columns = rowSize, ByAuthor = library.GroupByAuthor,
                    Heading = start == 0 ? title : null,
                    Count = books.Length + " 本",
                    Books = tiles
                });
            }
        }

        private async Task<ImageSource> LoadCover(ShelfEntry entry)
        {
            ImageSource image;
            if (covers.TryGetValue(entry.Id, out image)) return image;
            Task<ImageSource> pending;
            if (pendingCovers.TryGetValue(entry.Id, out pending)) return await pending;
            pending = ReadCover(entry);
            pendingCovers.Add(entry.Id, pending);
            try
            {
                image = await pending;
                if (image != null)
                {
                    if (coverOrder.Count == 64) covers.Remove(coverOrder.Dequeue());
                    coverOrder.Enqueue(entry.Id);
                    covers.Add(entry.Id, image);
                }
                return image;
            }
            finally { pendingCovers.Remove(entry.Id); }
        }

        private async Task<ImageSource> ReadCover(ShelfEntry entry)
        {
            await coverSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() => ReaderWindow.DecodeCover(library.ReadCover(entry))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException ||
                    ex is XmlException || ex is DecoderFallbackException || ex is FormatException || ex is NotSupportedException)
                    return null;
                throw;
            }
            finally { coverSlots.Release(); }
        }

        private static string AuthorName(ShelfEntry entry) { return (entry.Author ?? "").Trim(); }

        private sealed class ShelfRow
        {
            public int Columns { get; set; }
            public bool ByAuthor { get; set; }
            public string Heading { get; set; }
            public string Count { get; set; }
            public ShelfTile[] Books { get; set; }
        }

        private sealed class ShelfTile : INotifyPropertyChanged
        {
            private static readonly string[] Colors = { "#385D4E", "#745448", "#3D5768", "#665C49", "#5A4F68" };
            private readonly Func<ShelfEntry, Task<ImageSource>> loadCover;
            private bool requested;
            private ImageSource cover;
            public event PropertyChangedEventHandler PropertyChanged;
            public ShelfEntry Entry { get; private set; }
            public string Title { get { return Entry.DisplayTitle; } }
            public bool IsRead { get { return Entry.IsRead; } }
            public string ReadAction { get { return IsRead ? "标为未读" : "标为已读"; } }
            public ICommand ReadCommand { get { return ToggleReadStatus; } }
            public ICommand DeleteCommand { get { return DeleteBookCommand; } }
            public string Author { get; private set; }
            public string Description { get { return Title + "\n" + Author + "\n" + DateLabel + (IsRead ? "\n已读" : ""); } }
            public string DateLabel { get; private set; }
            public string Color { get; private set; }

            public ShelfTile(ShelfEntry entry, bool byReading, Func<ShelfEntry, Task<ImageSource>> loadCover)
            {
                Entry = entry;
                Author = AuthorName(entry);
                if (Author.Length == 0) Author = "未署名作者";
                Color = Colors[String.IsNullOrEmpty(entry.Id) ? 0 : entry.Id[0] % Colors.Length];
                var date = byReading ? entry.ReadUtc : entry.ImportedUtc;
                DateLabel = date.HasValue
                    ? (byReading ? "阅读 " : "导入 ") + date.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : byReading ? "暂无阅读记录" : "导入时间未知";
                this.loadCover = loadCover;
            }

            public ImageSource Cover
            {
                get
                {
                    if (!requested)
                    {
                        requested = true;
                        BeginCoverLoad();
                    }
                    return cover;
                }
            }

            private async void BeginCoverLoad()
            {
                cover = await loadCover(Entry);
                if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Cover"));
            }
        }
    }
}
