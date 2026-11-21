using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Tingye
{
    internal sealed class ReaderWindow
    {
        private static readonly Geometry PlayIcon = Geometry.Parse("M 1,0 L 14,7 L 1,14 Z");
        private static readonly Geometry PauseIcon = Geometry.Parse("M 1,0 H 5 V 14 H 1 Z M 9,0 H 13 V 14 H 9 Z");

        private readonly Window window;
        private readonly Library library;
        private readonly Bookshelf bookshelf;
        private readonly FrameworkElement bookshelfPage, readerArea, playerBar;
        private readonly ComboBox shelf, voices, speeds;
        private readonly ListBox chapters, passages;
        private readonly Button play, previous, next, import, welcomeImport;
        private readonly Button scanBooks;
        private readonly DispatcherTimer saveTimer;
        private readonly List<VoiceOption> availableVoices = new List<VoiceOption>();
        private readonly NaturalPlayback natural;
        private readonly BreezeVoice breezeVoice;
        private readonly BreezePlayback breeze;
        private readonly VoiceOption breezeOption = new VoiceOption("breeze:" + BreezeVoice.VoiceId, BreezeVoice.VoiceName, NeuralVoice.BreezeBlue);
        private readonly VoiceOption localBreezeOption = new VoiceOption(LocalSpeech.Choice, LocalSpeech.Label, NeuralVoice.BreezeLocal);
        private readonly VoiceOption qwenOption = new VoiceOption(LocalSpeech.QwenChoice, LocalSpeech.QwenLabel, NeuralVoice.QwenVivian);
        private readonly VoiceOption serenaOption = new VoiceOption(LocalSpeech.QwenSerenaChoice, LocalSpeech.QwenSerenaLabel, NeuralVoice.QwenSerena);
        private SpeechSynthesizer synthesizer;
        private Prompt prompt;
        private ShelfEntry entry;
        private Book book;
        private List<ContentsRow>[] contentsByChapter;
        private List<ContentsRow> contentsRoots;
        private ObservableCollection<ContentsRow> visibleContents;
        private long[] passageOffsets;
        private bool syncing, loading, dirty, playing, closing;
        private int centerPassageRequest;
        private ShelfEntry mostRecentlyTouched;
        private readonly Queue<string> requestedBooks = new Queue<string>();
        private bool shelfReady;
        private double infoWidth = 198;
        private bool sidebarExpanded;
        private static readonly string[] ReadingFamilies = { "Microsoft YaHei", "SimSun", "KaiTi", "FangSong" };

        public ReaderWindow(Window window, Library library, string openFile, string speechDirectory = null)
        {
            this.window = window;
            this.library = library;
            bookshelfPage = Find<FrameworkElement>("BookshelfPage");
            readerArea = Find<FrameworkElement>("ReaderArea");
            playerBar = Find<FrameworkElement>("PlayerBar");
            var fontChoice = Find<ComboBox>("ReadingFont");
            var sizeChoice = Find<ComboBox>("ReadingFontSize");
            fontChoice.ItemsSource = new[] { "微软雅黑", "宋体", "楷体", "仿宋" };
            sizeChoice.ItemsSource = Enumerable.Range(14, 19).ToArray();
            SelectionChangedEventHandler typographyChanged = delegate {
                if (fontChoice.SelectedIndex < 0 || sizeChoice.SelectedItem == null) return;
                int size = (int)sizeChoice.SelectedItem;
                library.ReadingFont = ReadingFamilies[fontChoice.SelectedIndex];
                library.ReadingSize = size;
                window.Resources["ReadingFamily"] = new FontFamily(library.ReadingFont);
                window.Resources["ReadingSize"] = (double)size;
                window.Resources["ReadingHeadingSize"] = (double)(size + 5);
                window.Resources["ReadingLineHeight"] = Math.Round(size * 1.85);
                if (shelfReady) { dirty = true; Save(); }
            };
            fontChoice.SelectionChanged += typographyChanged;
            sizeChoice.SelectionChanged += typographyChanged;
            Find<Button>("ExpandNav").Click += delegate { ToggleSidebar(); };
            Find<FrameworkElement>("BookInfo").SizeChanged += delegate { CenterReadingColumn(); };
            window.SizeChanged += delegate { CenterReadingColumn(); };
            var appearance = new Appearance(window, theme => {
                if (!shelfReady) return;
                library.Theme = theme;
                dirty = true;
                Save();
            });
            Appearance.AnimateWhenShown(Text("ShelfTitle"));
            Appearance.AnimateWhenShown(Text("ChapterTitle"));
            bookshelf = new Bookshelf(window, library, OpenBook, RenameBook, ToggleReadStatus, () => { dirty = true; });
            shelf = Find<ComboBox>("Shelf");
            voices = Find<ComboBox>("Voices");
            speeds = Find<ComboBox>("Speeds");
            chapters = Find<ListBox>("Chapters");
            passages = Find<ListBox>("Passages");
            play = Find<Button>("Play");
            previous = Find<Button>("PreviousChapter");
            next = Find<Button>("NextChapter");
            import = Find<Button>("ImportButton");
            welcomeImport = Find<Button>("WelcomeImport");
            scanBooks = Find<Button>("ScanBooksFolder");

            var rates = new[] {
                new RateOption("慢速", -3, 0.85f), new RateOption("标准", 0, 1f),
                new RateOption("稍快", 2, 1.15f), new RateOption("快速", 4, 1.3f), new RateOption("很快", 7, 1.55f),
                new RateOption("1.75×", 9, 1.75f), new RateOption("2×", 10, 2f)
            };
            speeds.ItemsSource = rates;
            speeds.SelectedIndex = 1;
            speechDirectory = speechDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "speech");
            var model = new KokoroVoice(speechDirectory);
            if (model.IsInstalled)
            {
                natural = new NaturalPlayback(model);
                natural.Changed += UpdatePlayer;
                natural.Completed += FinishPassage;
            }
            breezeVoice = new BreezeVoice(library.DirectoryPath, speechDirectory);
            breeze = new BreezePlayback(breezeVoice);
            breeze.Changed += UpdatePlayer;
            breeze.PassageStarted += (chapter, passage) => {
                if (entry.Chapter != chapter || entry.Passage != passage) ChangePosition(chapter, passage);
            };
            breeze.Completed += FinishBreeze;
            InitVoices();

            import.Click += delegate { PickFile(); };
            Find<RadioButton>("ShelfButton").Click += delegate { ShowBookshelf(); };
            Find<Button>("ChooseBook").Click += delegate { ShowBookshelf(); };
            Find<RadioButton>("SettingsNav").Click += delegate {
                SelectPage(Page.Settings);
                UpdatePlayer();
            };
            Find<RadioButton>("ReaderNav").Click += async delegate {
                SelectPage(Page.Reading);
                UpdatePlayer();
                if (book != null) { SchedulePassageCenter(); return; }
                var last = library.Books.FirstOrDefault(item => item.Id == library.SelectedId);
                if (last != null) await OpenBook(last);
            };
            Find<Button>("OpenBooksFolder").Click += delegate { OpenBooksFolder(); };
            scanBooks.Click += async delegate { await ScanBooksFolder(); };
            Find<Button>("BreezeSettings").Click += delegate {
                if (ConfigureBreeze()) voices.SelectedItem = breezeOption;
            };
            welcomeImport.Click += delegate { PickFile(); };
            play.Click += delegate { TogglePlayback(); };
            previous.Click += delegate { Seek(entry.Chapter - 1, 0); };
            next.Click += delegate { Seek(entry.Chapter + 1, 0); };
            shelf.SelectionChanged += async delegate {
                var selected = shelf.SelectedItem as ShelfEntry;
                if (!syncing && !loading && selected != null && selected != entry) await OpenBook(selected);
            };
            chapters.SelectionChanged += delegate {
                var row = chapters.SelectedItem as ContentsRow;
                if (!syncing && !loading && row != null) Seek(row.Chapter, row.Passage);
            };
            chapters.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(ToggleContentsGroup));
            passages.SelectionChanged += delegate {
                var row = passages.SelectedItem as PassageRow;
                if (!syncing && !loading && row != null && !row.IsSpacer) Seek(entry.Chapter, passages.SelectedIndex - 1);
            };
            passages.SizeChanged += delegate {
                passages.Tag = passages.ActualHeight * 0.75;
                SchedulePassageCenter();
            };
            voices.SelectionChanged += delegate {
                if (syncing) return;
                var option = voices.SelectedItem as VoiceOption;
                library.PreferredVoice = option == null ? "" : option.Name;
                if (entry != null) entry.Voice = library.PreferredVoice;
                dirty = true;
                ApplySpeechSettings();
            };
            speeds.SelectionChanged += delegate {
                if (syncing || speeds.SelectedItem == null) return;
                library.Rate = ((RateOption)speeds.SelectedItem).Rate;
                dirty = true;
                ApplySpeechSettings();
            };

            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            saveTimer.Tick += delegate { if (shelfReady && !closing) Save(); };
            saveTimer.Start();
            window.PreviewKeyDown += OnKey;
            window.PreviewDragOver += OnDragOver;
            window.PreviewDrop += async (sender, args) => {
                var path = DroppedBook(args.Data);
                args.Handled = true;
                if (!loading && path != null) await ImportBook(path, bookshelfPage.Visibility != Visibility.Visible);
            };
            window.Closing += (sender, args) => {
                if (loading)
                {
                    closing = true;
                    args.Cancel = true;
                    return;
                }
                closing = true;
                StopSpeech();
                while (shelfReady && !Save())
                {
                    int action = Choose("进度尚未保存", Text("Notice").Text,
                        "重试保存", "返回听页", "放弃保存并退出");
                    if (action == 0) continue;
                    if (action != 2) { closing = false; args.Cancel = true; }
                    break;
                }
            };
            window.Closed += delegate {
                saveTimer.Stop();
                breezeVoice.Close();
                if (synthesizer != null) synthesizer.Dispose();
            };
            window.Loaded += async delegate {
                bool recovered;
                while (true)
                {
                    try { recovered = library.Load(); break; }
                    catch (Exception ex)
                    {
                        int action = Choose("书架恢复", "无法读取上次保存的书架。\n" + ex.Message,
                            "重新尝试", "打开书架目录", "退出听页");
                        if (action == 0) continue;
                        if (action != 1) { window.Close(); return; }
                        try { using (Process.Start(new ProcessStartInfo(library.DirectoryPath) { UseShellExecute = true })) { } }
                        catch (Exception error) { MessageBox.Show(window, error.Message, "无法打开书架目录"); }
                    }
                }
                if (library.Books.Count > 0 && breezeVoice.HasLocal && !library.LocalBreezeMigrated)
                {
                    foreach (var saved in library.Books.Where(item => item.Voice == breezeOption.Name))
                        saved.Voice = localBreezeOption.Name;
                    library.LocalBreezeMigrated = true;
                    dirty = true;
                }
                var rate = rates.FirstOrDefault(x => x.Rate == library.Rate) ?? rates[1];
                syncing = true;
                var lastBook = library.Books.FirstOrDefault(item => item.Id == library.SelectedId);
                string preferred = library.PreferredVoice ?? (lastBook == null ? "" : lastBook.Voice);
                if (preferred == "kokoro:6" || preferred == "kokoro:59") preferred = "kokoro:3";
                var restored = voices.Items.Cast<VoiceOption>().FirstOrDefault(item => item.Name == preferred);
                voices.SelectedItem = restored ?? voices.Items[0];
                if (library.PreferredVoice == null)
                {
                    library.PreferredVoice = ((VoiceOption)voices.SelectedItem).Name;
                    dirty = true;
                }
                speeds.SelectedItem = rate;
                syncing = false;
                bookshelf.RestorePreferences();
                RefreshShelf();
                if (recovered)
                {
                    dirty = true;
                    Save();
                }
                appearance.Restore(library.Theme);
                int savedSize = library.ReadingSize;
                fontChoice.SelectedIndex = Math.Max(0, Array.IndexOf(ReadingFamilies, library.ReadingFont));
                sizeChoice.SelectedItem = savedSize >= 14 && savedSize <= 32 ? savedSize : 17;
                shelfReady = true;
                if (!String.IsNullOrWhiteSpace(openFile)) await ImportBook(openFile, true);
                else OpenNextRequest();
            };
            UpdatePlayer();
        }

        private T Find<T>(string name) where T : class { return (T)window.FindName(name); }
        private TextBlock Text(string name) { return Find<TextBlock>(name); }

        private void InitVoices()
        {
            try
            {
                synthesizer = new SpeechSynthesizer();
                availableVoices.AddRange(synthesizer.GetInstalledVoices().Where(x => x.Enabled)
                    .Select(x => new VoiceOption(x.VoiceInfo.Name, x.VoiceInfo.Culture)));
                synthesizer.SpeakCompleted += (sender, args) => {
                    if (!window.Dispatcher.HasShutdownStarted)
                        window.Dispatcher.BeginInvoke(new Action(() => SpeechCompleted(args)));
                };
            }
            catch (Exception)
            {
                if (synthesizer != null) synthesizer.Dispose();
                synthesizer = null;
                availableVoices.Clear();
            }
            if (natural != null)
            {
                availableVoices.InsertRange(0, new[] {
                    new VoiceOption(3, "zh-CN", "自然中文 · 女声"), new VoiceOption(0, "en-US", "自然英语 · 女声")
                });
            }
            availableVoices.Insert(0, breezeOption);
            if (breezeVoice.HasLocal) availableVoices.Insert(0, localBreezeOption);
            if (breezeVoice.HasQwen) availableVoices.InsertRange(0, new[] { serenaOption, qwenOption });
            var options = new List<VoiceOption> { new VoiceOption("", (CultureInfo)null) };
            options.AddRange(availableVoices.Where(x => x.Culture.TwoLetterISOLanguageName != "en"));
            voices.ItemsSource = options;
            voices.SelectedIndex = 0;
        }

        private async void PickFile()
        {
            if (loading) return;
            var dialog = new OpenFileDialog { Title = "选择要导入的电子书", Filter = "EPUB 电子书 (*.epub)|*.epub", CheckFileExists = true };
            if (dialog.ShowDialog(window) == true)
                await ImportBook(dialog.FileName, bookshelfPage.Visibility != Visibility.Visible);
        }

        private async Task ImportBook(string path, bool openAfterImport)
        {
            BeginLoading(openAfterImport);
            try
            {
                ImageSource cover = null;
                var imported = await Task.Run(() => {
                    var result = library.Import(path);
                    if (openAfterImport)
                    {
                        result.Book.IndexChapterLengths();
                        cover = DecodeCover(result.Book.Cover);
                    }
                    return result;
                });
                var selected = library.Books.FirstOrDefault(x => x.Id == imported.Id);
                if (selected == null)
                {
                    selected = imported.CreateShelfEntry();
                    library.Books.Insert(0, selected);
                    bookshelf.InvalidateRows();
                    dirty = true;
                }
                if (openAfterImport) SetBook(selected, imported.Book, cover);
                Save();
            }
            catch (Exception ex) { ShowNotice("导入失败。" + DescribeBookError(ex)); }
            finally { EndLoading(); }
        }

        private void OpenBooksFolder()
        {
            try
            {
                Directory.CreateDirectory(library.BooksDirectory);
                using (Process.Start(new ProcessStartInfo(library.BooksDirectory) { UseShellExecute = true })) { }
            }
            catch (Exception error) { ShowNotice("无法打开书籍文件夹。" + DescribeBookError(error)); }
        }

        private async Task ScanBooksFolder()
        {
            if (loading) return;
            BeginLoading(false);
            scanBooks.Content = "正在扫描…";
            try
            {
                var indexed = library.Books.Select(item => item.Id).ToArray();
                var scanned = await Task.Run(() => library.ScanFolder(indexed));
                if (scanned.Added.Count > 0 || scanned.Restored > 0) bookshelf.InvalidateRows();
                if (scanned.Added.Count > 0)
                {
                    library.Books.InsertRange(0, scanned.Added);
                    dirty = true;
                    Save();
                    if (dirty) return;
                }
                string status = scanned.Added.Count == 0 && scanned.Restored == 0 && scanned.Duplicates == 0 &&
                    scanned.Unsupported.Count == 0 && scanned.Unreadable.Count == 0
                    ? "扫描完成，没有发现可添加的新书。"
                    : "扫描完成，新增 " + scanned.Added.Count + " 本。";
                if (scanned.Restored > 0) status += " 补回 " + scanned.Restored + " 本书的 EPUB 副本。";
                if (scanned.Duplicates > 0) status += " 有 " + scanned.Duplicates + " 个 EPUB 文件已在书架中。";
                if (scanned.Unsupported.Count > 0)
                    status += "\n仅支持 EPUB，已跳过 " + scanned.Unsupported.Count + " 个其他格式文件，例如 " +
                        scanned.Unsupported[0] + "。";
                if (scanned.Unreadable.Count > 0)
                    status += "\n有 " + scanned.Unreadable.Count + " 个文件无法读取。"
                        + " " + scanned.Unreadable[0] + "：" + DescribeBookError(scanned.FirstReadError);
                ShowNotice(status);
            }
            catch (Exception error) { ShowNotice("扫描失败。" + DescribeBookError(error)); }
            finally
            {
                scanBooks.Content = "扫描文件夹";
                EndLoading();
            }
        }

        private async Task OpenBook(ShelfEntry selected)
        {
            if (loading) return;
            if (selected == entry && book != null)
            {
                TouchRead();
                ShowReaderPage();
                UpdatePlayer();
                SchedulePassageCenter();
                return;
            }
            BeginLoading();
            try
            {
                ImageSource cover = null;
                var opened = await Task.Run(() => {
                    var result = library.Open(selected);
                    cover = DecodeCover(result.Cover);
                    return result;
                });
                SetBook(selected, opened, cover);
            }
            catch (Exception ex) { ShowNotice("无法打开这本书。" + DescribeBookError(ex)); }
            finally { EndLoading(); }
        }

        private static string DescribeBookError(Exception error)
        {
            if (error is System.Xml.XmlException || error is System.Text.DecoderFallbackException)
                return "EPUB 的文件清单或正文格式损坏。";
            if (error is UnauthorizedAccessException) return "没有权限访问文件或写入本地书架。";
            if (error is FileNotFoundException) return "找不到 EPUB 或其中的文件。";
            if (error is InvalidDataException && error.Message.Any(c => c >= '\u4e00' && c <= '\u9fff')) return error.Message;
            if (error is InvalidDataException) return "这个文件不是可读取的 EPUB，或文件已损坏。";
            return error.Message;
        }

        private void BeginLoading(bool stopPlayback = true)
        {
            HideNotice();
            if (stopPlayback) StopSpeech();
            loading = true;
            UpdatePlayer();
        }

        private void EndLoading()
        {
            loading = false;
            RefreshShelf();
            if (bookshelfPage.Visibility == Visibility.Visible) bookshelf.RefreshOnReturn();
            UpdatePlayer();
            if (closing) window.Close();
            else OpenNextRequest();
        }

        public void RequestOpen(string path)
        {
            if (closing) return;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            if (!String.IsNullOrWhiteSpace(path)) requestedBooks.Enqueue(path);
            OpenNextRequest();
        }

        private async void OpenNextRequest()
        {
            if (shelfReady && !loading && !closing && requestedBooks.Count > 0)
                await ImportBook(requestedBooks.Dequeue(), true);
        }

        private void SetBook(ShelfEntry selected, Book opened, ImageSource cover)
        {
            entry = selected;
            book = opened;
            library.SelectedId = entry.Id;
            TouchRead();
            if (entry.ChapterLayout < 2)
            {
                if (entry.ChapterLayout == 0)
                    book.RestoreLegacyPosition(ref entry.Chapter, ref entry.Passage);
                else
                    book.RestoreGroupedPosition(ref entry.Chapter, ref entry.Passage);
                entry.ChapterLayout = 2;
            }
            entry.Chapter = Math.Max(0, Math.Min(entry.Chapter, book.Chapters.Count - 1));
            entry.Passage = Math.Max(0, Math.Min(entry.Passage, book.Chapters[entry.Chapter].Passages.Count - 1));

            syncing = true;
            BuildContentsOutline();
            syncing = false;
            ShowBookTitle();
            Text("BookAuthor").Text = book.Author;
            Text("BookAuthor").ToolTip = book.Author;
            Text("BookAuthor").Visibility = String.IsNullOrEmpty(book.Author) ? Visibility.Collapsed : Visibility.Visible;
            Find<Image>("CoverImage").Source = cover;
            Text("ChapterCount").Text = book.ChapterCount > 0 ? book.ChapterCount + " 章" : "";
            Find<FrameworkElement>("Reader").Visibility = Visibility.Visible;
            Find<FrameworkElement>("BookCard").Visibility = Visibility.Visible;
            ShowReaderPage();
            ShowPosition(true);
        }

        private void BuildContentsOutline()
        {
            contentsRoots = new List<ContentsRow>();
            contentsByChapter = new List<ContentsRow>[book.Chapters.Count];
            var headings = new Dictionary<DirectoryHeading, ContentsRow>();
            for (int index = 0; index < book.Chapters.Count; index++)
            {
                var chapter = book.Chapters[index];
                var chapterRow = AddContentsTarget(headings, chapter.Heading, chapter.Title, index, 0, null);
                var targets = new List<ContentsRow> { chapterRow };
                foreach (var section in chapter.Sections)
                    targets.Add(AddContentsTarget(headings, section.Heading, section.Title, index, section.Passage, chapterRow));
                contentsByChapter[index] = targets;
            }
            GroupFlatContents();
            var target = contentsByChapter[entry.Chapter].FindLast(row => row.Passage <= entry.Passage);
            for (var parent = target.Parent; parent != null; parent = parent.Parent)
                parent.IsExpanded = true;
            visibleContents = new ObservableCollection<ContentsRow>(contentsRoots.SelectMany(root =>
                new[] { root }.Concat(root.IsExpanded ? root.VisibleChildren() : Enumerable.Empty<ContentsRow>())));
            chapters.ItemsSource = visibleContents;
        }

        private void GroupFlatContents()
        {
            int first = book.Chapters.FindIndex(chapter => chapter.Number > 0);
            int last = book.Chapters.FindLastIndex(chapter => chapter.Number > 0);
            if (first < 0 || (first == 0 && last == book.Chapters.Count - 1)) return;
            // Preserve publisher-defined volume hierarchies.
            if (contentsRoots.Count != book.Chapters.Count ||
                contentsRoots.Where((row, index) => row != contentsByChapter[index][0]).Any()) return;
            var original = contentsRoots;
            contentsRoots = new List<ContentsRow>();
            ContentsRow group = null;
            foreach (var row in original)
            {
                string label = row.Chapter < first ? "卷首" : row.Chapter > last ? "卷末" : "正文";
                if (group == null || group.Title != label)
                    group = AddContentsRow(label, row.Chapter, 0, false, null);
                row.AttachTo(group);
            }
        }

        private ContentsRow AddContentsTarget(Dictionary<DirectoryHeading, ContentsRow> headings, DirectoryHeading heading,
            string title, int chapter, int passage, ContentsRow chapterRow)
        {
            bool isSection = chapterRow != null;
            if (heading == null) return AddContentsRow(title, chapter, passage, isSection, chapterRow);

            var path = new Stack<DirectoryHeading>();
            var parent = heading.Parent == null ? chapterRow : null;
            for (var level = heading; level != null; level = level.Parent)
            {
                if (level != heading && String.IsNullOrEmpty(level.Title)) continue;
                if (level != heading && level.Parent == null && level.Title == book.Title) break;
                ContentsRow existing;
                if (headings.TryGetValue(level, out existing))
                {
                    parent = existing;
                    break;
                }
                path.Push(level);
            }
            while (path.Count > 0)
            {
                var level = path.Pop();
                bool target = level == heading;
                parent = AddContentsRow(target ? title : level.Title, chapter, passage, target && isSection, parent);
                headings.Add(level, parent);
            }
            parent.SetTarget(chapter, passage, isSection);
            return parent;
        }

        private ContentsRow AddContentsRow(string title, int chapter, int passage, bool isSection, ContentsRow parent)
        {
            var row = new ContentsRow(title, chapter, passage, isSection, parent);
            if (parent == null) contentsRoots.Add(row);
            else parent.Children.Add(row);
            return row;
        }

        private void ShowBookTitle()
        {
            string title = entry.DisplayTitle;
            Text("BookTitle").Text = title;
            Text("BookTitle").ToolTip = title;
            Text("ReaderBookTitle").Text = title;
            Text("ReaderBookTitle").ToolTip = title;
            Text("CoverLetter").Text = String.IsNullOrEmpty(title) ? "书" : StringInfo.GetNextTextElement(title);
        }

        private void RenameBook(ShelfEntry selected)
        {
            if (loading) return;
            var dialog = CreateDialog("修改显示书名", 500);
            var panel = new StackPanel { Margin = new Thickness(23) };
            var name = new TextBox { Text = selected.DisplayTitle, Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 5, 0, 12) };
            panel.Children.Add(new Label { Content = "显示书名", Target = name, Padding = new Thickness(0) });
            AutomationProperties.SetName(name, "显示书名");
            panel.Children.Add(name);
            panel.Children.Add(new TextBlock { Text = "原书名：" + selected.Title, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)window.FindResource("Muted"), FontSize = 12 });

            var actions = new DockPanel { Margin = new Thickness(0, 19, 0, 0), LastChildFill = false };
            var save = new Button { Content = "保存", IsDefault = true, Style = (Style)window.FindResource("PrimaryButton") };
            var cancel = new Button { Content = "取消", IsCancel = true, Style = (Style)window.FindResource("BaseButton"),
                Margin = new Thickness(0, 0, 8, 0) };
            var reset = new Button { Content = "恢复自动书名", Style = (Style)window.FindResource("BaseButton") };
            DockPanel.SetDock(save, Dock.Right);
            DockPanel.SetDock(cancel, Dock.Right);
            actions.Children.Add(save);
            actions.Children.Add(cancel);
            actions.Children.Add(reset);
            panel.Children.Add(actions);
            save.Click += delegate { dialog.DialogResult = true; };
            reset.Click += delegate { name.Text = selected.AutomaticTitle; name.Focus(); name.SelectAll(); };
            dialog.Content = panel;
            dialog.Loaded += delegate { name.Focus(); name.SelectAll(); };
            if (dialog.ShowDialog() != true) return;

            string title = name.Text.Trim();
            string custom = title.Length == 0 || title == selected.AutomaticTitle ? null : title;
            if (selected.CustomTitle == custom) return;
            selected.CustomTitle = custom;
            dirty = true;
            Save();
            if (selected == entry) ShowBookTitle();
            RefreshShelf();
            bookshelf.Refresh();
        }

        private void ToggleReadStatus(ShelfEntry selected)
        {
            selected.IsRead = !selected.IsRead;
            dirty = true;
            Save();
            bookshelf.Refresh();
        }

        private enum Page { Shelf, Reading, Settings }
        private void ToggleSidebar()
        {
            var info = Find<ColumnDefinition>("InfoColumn");
            if (sidebarExpanded && info.ActualWidth >= 150) infoWidth = info.ActualWidth;
            sidebarExpanded = !sidebarExpanded;
            var visibility = sidebarExpanded ? Visibility.Visible : Visibility.Collapsed;
            Find<ColumnDefinition>("NavColumn").Width = new GridLength(sidebarExpanded ? 100 : 44);
            Find<FrameworkElement>("BookInfo").Visibility = Find<FrameworkElement>("InfoDivider").Visibility = visibility;
            Find<FrameworkElement>("ReadNavLabel").Visibility = Find<FrameworkElement>("ShelfNavLabel").Visibility =
                Find<FrameworkElement>("SettingsNavLabel").Visibility = visibility;
            Find<ColumnDefinition>("DividerColumn").Width = new GridLength(sidebarExpanded ? 4 : 0);
            info.MinWidth = sidebarExpanded ? 150 : 0;
            info.Width = new GridLength(sidebarExpanded ? infoWidth : 0);
            var toggle = Find<Button>("ExpandNav");
            toggle.ToolTip = sidebarExpanded ? "收起侧栏" : "展开侧栏";
            AutomationProperties.SetName(toggle, (string)toggle.ToolTip);
            CenterReadingColumn();
        }

        private void CenterReadingColumn()
        {
            var info = Find<ColumnDefinition>("InfoColumn");
            double rail = Find<ColumnDefinition>("NavColumn").Width.Value;
            double width = ((FrameworkElement)window.Content).ActualWidth;
            info.MaxWidth = Math.Max(150, Math.Min(430, width / 2 - rail - 152));
            double inset = rail + (sidebarExpanded ? info.ActualWidth + 4 : 0);
            Find<FrameworkElement>("TextHost").Margin = new Thickness(0, 0, inset, 0);
        }

        private Page currentPage = Page.Shelf;

        private void SelectPage(Page page)
        {
            HideNotice();
            currentPage = page;
            bookshelfPage.Visibility = page == Page.Shelf ? Visibility.Visible : Visibility.Hidden;
            readerArea.Visibility = page == Page.Reading && book != null ? Visibility.Visible : Visibility.Collapsed;
            Find<FrameworkElement>("EmptyReader").Visibility = page == Page.Reading && book == null ? Visibility.Visible : Visibility.Collapsed;
            Find<FrameworkElement>("SettingsPage").Visibility = page == Page.Settings ? Visibility.Visible : Visibility.Collapsed;
            Find<RadioButton>(page == Page.Reading ? "ReaderNav" : page == Page.Settings ? "SettingsNav" : "ShelfButton").IsChecked = true;
        }

        private void ShowReaderPage()
        {
            SelectPage(Page.Reading);
        }

        private void ShowBookshelf()
        {
            if (loading) return;
            SelectPage(Page.Shelf);
            bookshelf.RefreshOnReturn();
            UpdatePlayer();
        }

        private void TouchRead()
        {
            var now = DateTime.UtcNow;
            if (library.SortByReading && (mostRecentlyTouched != entry || !entry.ReadUtc.HasValue ||
                entry.ReadUtc.Value.ToLocalTime().Date != now.ToLocalTime().Date || now < entry.ReadUtc.Value))
                bookshelf.InvalidateRows();
            mostRecentlyTouched = entry;
            entry.ReadUtc = now;
            dirty = true;
        }

        private void RefreshShelf()
        {
            syncing = true;
            shelf.ItemsSource = library.Books.ToArray();
            shelf.SelectedItem = entry;
            syncing = false;
            shelf.IsEnabled = !loading && library.Books.Count > 0;
        }

        internal static ImageSource DecodeCover(byte[] cover)
        {
            if (cover == null) return null;
            try
            {
                using (var stream = new MemoryStream(cover, false))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.StreamSource = stream;
                    image.DecodePixelWidth = 168;
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch (Exception ex)
            {
                if (ex is IOException || ex is NotSupportedException || ex is ArgumentException || ex is FormatException) return null;
                throw;
            }
        }

        private void ShowPosition(bool chapterChanged)
        {
            var chapter = book.Chapters[entry.Chapter];
            syncing = true;
            if (chapterChanged)
            {
                Text("ChapterTitle").Text = chapter.Title;
                Text("ChapterTitle").ToolTip = chapter.Title;
                var rows = new List<PassageRow>(chapter.Passages.Count + 2);
                var sectionTitles = chapter.Sections.ToDictionary(section => section.Passage, section => section.Title);
                rows.Add(new PassageRow(null));
                passageOffsets = new long[chapter.Passages.Count];
                long offset = chapter.CharacterStart;
                for (int i = 0; i < chapter.Passages.Count; i++)
                {
                    passageOffsets[i] = offset;
                    offset += chapter.Passages[i].Length;
                    string heading;
                    sectionTitles.TryGetValue(i, out heading);
                    rows.Add(new PassageRow(chapter.Passages[i], heading));
                }
                rows.Add(new PassageRow(null));
                bool replacingChapter = passages.ItemsSource != null;
                passages.ItemsSource = rows;
                if (replacingChapter) Appearance.AnimateEntry(Text("ChapterTitle"));
            }
            SelectCurrentContents(true);
            passages.SelectedIndex = entry.Passage + 1;
            syncing = false;
            SchedulePassageCenter();
            Text("ReaderPosition").Text = chapter.Number > 0 ? String.Format("第 {0} / {1} 章", chapter.Number, book.ChapterCount) : "";
            UpdatePlayer();
        }

        private void ToggleContentsGroup(object sender, RoutedEventArgs args)
        {
            var toggle = args.OriginalSource as ToggleButton;
            var group = toggle == null ? null : toggle.DataContext as ContentsRow;
            if (group == null || !group.HasChildren || loading) return;

            args.Handled = true;
            int start = visibleContents.IndexOf(group) + 1;
            if (start == 0) return;
            syncing = true;
            try
            {
                group.IsExpanded = !group.IsExpanded;
                if (group.IsExpanded)
                {
                    foreach (var row in group.VisibleChildren())
                        visibleContents.Insert(start++, row);
                }
                else
                {
                    while (start < visibleContents.Count && visibleContents[start].Depth > group.Depth)
                        visibleContents.RemoveAt(start);
                }
                SelectCurrentContents(false);
            }
            finally { syncing = false; }
        }

        private void SelectCurrentContents(bool scroll)
        {
            var rows = contentsByChapter[entry.Chapter];
            var current = rows.FindLast(row => row.Passage <= entry.Passage);
            for (var parent = current.Parent; parent != null; parent = parent.Parent)
                if (!parent.IsExpanded) current = parent;
            if (chapters.SelectedItem == current) return;
            chapters.SelectedItem = current;
            if (scroll) chapters.ScrollIntoView(current);
        }

        private async void SchedulePassageCenter()
        {
            int request = ++centerPassageRequest;
            var row = passages.SelectedItem as PassageRow;
            if (row == null || row.IsSpacer || closing || !passages.IsVisible) return;
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            if (closing || request != centerPassageRequest || !passages.IsVisible) return;
            if (passages.ItemContainerGenerator.ContainerFromItem(row) == null)
                passages.ScrollIntoView(row);
            // The virtualizing panel can defer BringIntoView until the next layout pass.
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (closing || request != centerPassageRequest || !passages.IsVisible) return;
            var viewer = FindScrollViewer(passages);
            var item = passages.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;
            if (viewer == null || item == null) return;
            var viewport = viewer.Template.FindName("PART_ScrollContentPresenter", viewer) as ScrollContentPresenter;
            if (viewport == null || viewport.ActualHeight == 0) return;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (!CenterPassage(viewer, viewport, item)) return;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                if (closing || request != centerPassageRequest || !passages.IsVisible) return;
                item = passages.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;
                if (item == null) return;
            }
        }

        private static ScrollViewer FindScrollViewer(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var viewer = child as ScrollViewer ?? FindScrollViewer(child);
                if (viewer != null) return viewer;
            }
            return null;
        }

        private bool CenterPassage(ScrollViewer viewer, ScrollContentPresenter viewport, ListBoxItem item)
        {
            double height = viewport.ActualHeight;
            double top = item.TranslatePoint(new Point(), viewport).Y;
            var content = (FrameworkElement)window.Content;
            double center = content.TranslatePoint(new Point(0, content.ActualHeight / 2), viewport).Y;
            double desiredTop = Math.Max(0, Math.Min(height - item.ActualHeight, center - item.ActualHeight / 2));
            double offset = viewer.VerticalOffset + top - desiredTop;
            double target = Math.Min(viewer.ScrollableHeight, Math.Max(0, offset));
            if (Math.Abs(target - viewer.VerticalOffset) < 1) return false;
            viewer.ScrollToVerticalOffset(target);
            return true;
        }

        private sealed class ContentsRow : INotifyPropertyChanged
        {
            private bool isExpanded;

            public ContentsRow(string title, int chapter, int passage, bool isSection, ContentsRow parent)
            {
                Title = title;
                Parent = parent;
                SetTarget(chapter, passage, isSection);
            }
            public string Title { get; private set; }
            public int Chapter { get; private set; }
            public int Passage { get; private set; }
            public bool IsSection { get; private set; }
            public ContentsRow Parent { get; private set; }
            public int Depth { get { return Parent == null ? 0 : Parent.Depth + 1; } }
            public void AttachTo(ContentsRow parent)
            {
                Parent = parent;
                parent.Children.Add(this);
            }
            public readonly List<ContentsRow> Children = new List<ContentsRow>();
            public bool HasChildren { get { return Children.Count > 0; } }
            public int ChildrenCount { get { return Children.Count; } }
            public Thickness Padding
            {
                get
                {
                    int spacing = IsSection ? 2 : 5;
                    return new Thickness(7 + Math.Min(Depth, 5) * 12, spacing, 10, spacing);
                }
            }

            public void SetTarget(int chapter, int passage, bool isSection)
            {
                Chapter = chapter;
                Passage = passage;
                IsSection = isSection;
            }

            public IEnumerable<ContentsRow> VisibleChildren()
            {
                foreach (var child in Children)
                {
                    yield return child;
                    if (child.IsExpanded)
                        foreach (var descendant in child.VisibleChildren())
                            yield return descendant;
                }
            }

            public bool IsExpanded
            {
                get { return isExpanded; }
                set
                {
                    if (isExpanded == value) return;
                    isExpanded = value;
                    var changed = PropertyChanged;
                    if (changed != null) changed(this, new PropertyChangedEventArgs("IsExpanded"));
                }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        private sealed class PassageRow
        {
            public PassageRow(string text, string heading = null)
            {
                Text = text;
                SectionHeading = heading;
            }
            public string Text { get; private set; }
            public string SectionHeading { get; private set; }
            public bool IsHeadingText { get { return SectionHeading != null && SectionHeading == Text; } }
            public bool IsSpacer { get { return Text == null; } }
        }

        private void Seek(int chapter, int passage)
        {
            if (book == null || loading || chapter < 0 || chapter >= book.Chapters.Count) return;
            if (passage < 0 || passage >= book.Chapters[chapter].Passages.Count) return;
            HideNotice();
            bool keepPlaying = playing;
            StopSpeech();
            ChangePosition(chapter, passage);
            if (keepPlaying) SpeakCurrent();
        }

        private void ChangePosition(int chapter, int passage)
        {
            bool chapterChanged = chapter != entry.Chapter;
            entry.Chapter = chapter;
            entry.Passage = passage;
            entry.Finished = false;
            TouchRead();
            ShowPosition(chapterChanged);
        }

        private void TogglePlayback()
        {
            if (book == null || loading || availableVoices.Count == 0) return;
            HideNotice();
            try
            {
                if (playing)
                {
                    if (breeze.Active) breeze.Pause();
                    else if (natural != null && natural.Active) natural.Pause();
                    else synthesizer.Pause();
                    playing = false;
                    Save();
                }
                else if (HasActiveSpeech)
                {
                    if (breeze.Active) breeze.Resume();
                    else if (natural != null && natural.Active) natural.Resume();
                    else synthesizer.Resume();
                    playing = true;
                    TouchRead();
                }
                else
                {
                    if (entry.Finished) ChangePosition(0, 0);
                    SpeakCurrent();
                }
                UpdatePlayer();
            }
            catch (Exception ex) { SpeechFailed(ex); }
        }

        private void SpeakCurrent()
        {
            try
            {
                var voice = SelectedVoice();
                string text = book.Chapters[entry.Chapter].Passages[entry.Passage];
                if (voice == null) throw new InvalidOperationException("请先选择可用的语音。");
                if (voice.Neural == NeuralVoice.BreezeBlue && !breezeVoice.HasKey && !ConfigureBreeze())
                {
                    playing = false;
                    UpdatePlayer();
                    return;
                }
                playing = true;
                TouchRead();
                if (voice.Neural.HasValue)
                {
                    var rate = (RateOption)speeds.SelectedItem;
                    breeze.Speak(book, entry.Chapter, entry.Passage, rate.NaturalSpeed, voice.Neural.Value);
                }
                else if (voice.Speaker.HasValue)
                {
                    var rate = (RateOption)speeds.SelectedItem;
                    natural.Speak(text, NextPassageText(), voice.Speaker.Value, rate.NaturalSpeed);
                }
                else
                {
                    synthesizer.SelectVoice(voice.Name);
                    synthesizer.Rate = library.Rate;
                    if (synthesizer.State == SynthesizerState.Paused) synthesizer.Resume();
                    prompt = new Prompt(text);
                    synthesizer.SpeakAsync(prompt);
                }
                UpdatePlayer();
            }
            catch (Exception ex) { SpeechFailed(ex); }
        }

        private bool HasActiveSpeech { get { return breeze.Active || prompt != null || (natural != null && natural.Active); } }

        private string NextPassageText()
        {
            var chapter = book.Chapters[entry.Chapter];
            if (entry.Passage + 1 < chapter.Passages.Count) return chapter.Passages[entry.Passage + 1];
            return entry.Chapter + 1 < book.Chapters.Count ? book.Chapters[entry.Chapter + 1].Passages[0] : null;
        }

        private VoiceOption SelectedVoice()
        {
            var option = voices.SelectedItem as VoiceOption;
            if (option != null && option.Name.Length > 0) return option;
            CultureInfo language = null;
            if (!String.IsNullOrWhiteSpace(book.Language))
                try { language = CultureInfo.GetCultureInfo(book.Language.Replace('_', '-')); }
                catch (CultureNotFoundException) { }
            if (language == null)
            {
                var text = String.Concat(book.Chapters.Take(3).SelectMany(x => x.Passages.Take(5)));
                language = CultureInfo.GetCultureInfo(SpeechText.HasHan(text) ? "zh-CN" : "en-US");
            }
            var matching = availableVoices.FirstOrDefault(x => x.Speaker.HasValue && x.Culture.TwoLetterISOLanguageName == language.TwoLetterISOLanguageName)
                ?? availableVoices.FirstOrDefault(x => !x.Neural.HasValue && x.Culture.Name == language.Name)
                ?? availableVoices.FirstOrDefault(x => !x.Neural.HasValue && x.Culture.TwoLetterISOLanguageName == language.TwoLetterISOLanguageName);
            if (matching != null) return matching;
            if (language.TwoLetterISOLanguageName == "zh")
            {
                if (breezeVoice.HasLocal) return localBreezeOption;
                if (breezeVoice.HasQwen) return serenaOption;
            }
            return availableVoices.FirstOrDefault(x => synthesizer != null && x.Name == synthesizer.Voice.Name)
                ?? availableVoices.FirstOrDefault(x => !x.Neural.HasValue);
        }

        private void ApplySpeechSettings()
        {
            if (playing)
            {
                StopSpeech();
                SpeakCurrent();
            }
            else if (HasActiveSpeech)
            {
                StopSpeech();
                UpdatePlayer();
            }
        }

        private void StopSpeech()
        {
            var current = prompt;
            prompt = null;
            playing = false;
            breeze.Stop();
            if (natural != null) natural.Stop();
            if (current != null && synthesizer != null)
            {
                synthesizer.SpeakAsyncCancelAll();
                if (synthesizer.State == SynthesizerState.Paused) synthesizer.Resume();
            }
        }

        private void SpeechCompleted(SpeakCompletedEventArgs args)
        {
            if (closing || prompt == null || args.Prompt != prompt) return;
            prompt = null;
            if (args.Error == null && args.Cancelled) { playing = false; UpdatePlayer(); return; }
            FinishPassage(args.Error);
        }

        private void FinishPassage(Exception error)
        {
            if (closing) return;
            if (error != null) { SpeechFailed(error); return; }
            bool continuePlaying = playing;
            playing = false;

            if (entry.Passage + 1 < book.Chapters[entry.Chapter].Passages.Count)
                ChangePosition(entry.Chapter, entry.Passage + 1);
            else if (entry.Chapter + 1 < book.Chapters.Count)
                ChangePosition(entry.Chapter + 1, 0);
            else
            {
                if (natural != null) natural.Stop();
                MarkPlaybackFinished();
                return;
            }
            if (continuePlaying) SpeakCurrent();
            else if (natural != null) natural.Stop();
        }

        private void FinishBreeze(Exception error)
        {
            if (closing) return;
            if (error != null) { SpeechFailed(error); return; }
            playing = false;
            MarkPlaybackFinished();
        }

        private void MarkPlaybackFinished()
        {
            entry.Finished = true;
            entry.IsRead = true;
            bookshelf.InvalidateRows();
            dirty = true;
            UpdatePlayer();
            Save();
            if (bookshelfPage.Visibility == Visibility.Visible) bookshelf.Refresh();
        }

        private void SpeechFailed(Exception error)
        {
            StopSpeech();
            UpdatePlayer();
            var cause = error.GetBaseException();
            ShowNotice(cause is LocalSpeechBusyException ? cause.Message :
                "无法朗读。请确认声音输出可用，或换一种语音。" + cause.Message);
        }

        private void UpdatePlayer()
        {
            bool hasBook = book != null;
            bool canSpeak = availableVoices.Count > 0;
            previous.IsEnabled = !loading && hasBook && entry.Chapter > 0;
            next.IsEnabled = !loading && hasBook && entry.Chapter + 1 < book.Chapters.Count;
            play.IsEnabled = !loading && hasBook && canSpeak;
            voices.IsEnabled = !loading && canSpeak;
            speeds.IsEnabled = !loading;
            Find<RadioButton>("ReaderNav").IsEnabled = Find<RadioButton>("ShelfButton").IsEnabled =
                Find<RadioButton>("SettingsNav").IsEnabled = !loading;
            shelf.IsEnabled = !loading && library.Books.Count > 0;
            passages.IsEnabled = chapters.IsEnabled = !loading;
            import.IsEnabled = welcomeImport.IsEnabled = !loading;
            scanBooks.IsEnabled = !loading;
            bookshelf.SetLoading(loading);
            playerBar.Visibility = currentPage == Page.Reading && hasBook ? Visibility.Visible : Visibility.Collapsed;

            string label = playing ? "暂停" : HasActiveSpeech ? "继续" : hasBook && entry.Finished ? "重听" : "朗读";
            Text("PlayLabel").Text = label;
            AutomationProperties.SetName(play, label);
            Find<System.Windows.Shapes.Path>("PlayGlyph").Data = playing ? PauseIcon : PlayIcon;
            double percent = !hasBook ? 0 : entry.Finished ? 100 :
                passageOffsets[entry.Passage] * 100.0 / Math.Max(1L, book.CharacterCount);
            Find<ProgressBar>("Progress").Value = percent;
            Text("ProgressPercent").Text = Math.Floor(percent) + "%";
            Text("PlayerTitle").Text = hasBook ? book.Chapters[entry.Chapter].Title : "等待导入";
            if (loading) Text("PlayerStatus").Text = "正在读取电子书…";
            else if (!hasBook) Text("PlayerStatus").Text = "导入电子书后即可朗读";
            else if (!canSpeak) Text("PlayerStatus").Text = "未找到可用的朗读语音";
            else if (entry.Finished) Text("PlayerStatus").Text = "全书已播放完毕";
            else
            {
                string status = playing ? (breeze.Buffering || (natural != null && natural.Buffering) ? "正在合成语音" : "正在朗读")
                    : HasActiveSpeech ? "已暂停" : "准备朗读";
                Text("PlayerStatus").Text = String.Format("{0} · 第 {1} / {2} 段", status, entry.Passage + 1, book.Chapters[entry.Chapter].Passages.Count);
            }
        }

        private bool Save()
        {
            if (!dirty) return true;
            try { library.Save(); dirty = false; return true; }
            catch (Exception ex) { ShowNotice("未能保存书架和进度。" + ex.GetBaseException().Message); return false; }
        }

        private Window CreateDialog(string title, double width)
        {
            var dialog = new Window { Title = title, Owner = window, Width = width,
                SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, FontFamily = window.FontFamily,
                FontSize = 14, Icon = window.Icon, Background = (Brush)window.FindResource("Paper"),
                Foreground = (Brush)window.FindResource("Ink") };
            dialog.Resources[typeof(Button)] = window.FindResource("ShelfAction");
            return dialog;
        }

        private int Choose(string title, string message, params string[] labels)
        {
            var dialog = CreateDialog(title, 460);
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            int selected = -1;
            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                var button = new Button { Content = labels[i], Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(0, 0, 6, 0), IsDefault = i == 0 };
                if (i == 0) button.Style = (Style)window.FindResource("PrimaryButton");
                button.Click += delegate { selected = index; dialog.DialogResult = true; };
                buttons.Children.Add(button);
            }
            panel.Children.Add(buttons);
            dialog.Content = panel;
            dialog.ShowDialog();
            return selected;
        }

        private void ShowNotice(string message)
        {
            Text("Notice").Text = message;
            Find<Border>("NoticeBox").Visibility = Visibility.Visible;
        }

        private void HideNotice() { Find<Border>("NoticeBox").Visibility = Visibility.Collapsed; }

        private bool ConfigureBreeze()
        {
            var dialog = CreateDialog("BreezeBlue API 密钥", 460);
            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(new TextBlock {
                Text = "大肥鱼（机灵版）会将朗读文字发送给 BreezeBlue 合成，并消耗账户额度。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });
            var key = new PasswordBox { Margin = new Thickness(0, 0, 0, 8), MinHeight = 30 };
            AutomationProperties.SetName(key, "BreezeBlue API 密钥");
            panel.Children.Add(key);
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick };
            panel.Children.Add(error);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var manage = new Button { Content = "打开密钥管理", Padding = new Thickness(8, 4, 8, 4) };
            manage.Click += delegate {
                try { System.Diagnostics.Process.Start("https://breezeblue.ai/zh/app/developer/api-keys"); }
                catch (Exception) { error.Text = "请在浏览器打开 BreezeBlue 的 API 密钥管理页。"; }
            };
            var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(8, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4) };
            var save = new Button { Content = "保存并选用", IsDefault = true, Height = 38, Style = (Style)window.FindResource("PrimaryButton"), Padding = new Thickness(12, 4, 12, 4) };
            save.Click += delegate {
                try { breezeVoice.SaveKey(key.Password); key.Clear(); dialog.DialogResult = true; }
                catch (ArgumentException ex) { error.Text = ex.Message; }
                catch (Exception) { error.Text = "无法保存密钥，请检查听页数据目录的权限。"; }
            };
            buttons.Children.Add(manage);
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            panel.Children.Add(buttons);
            dialog.Content = panel;
            dialog.Loaded += delegate { key.Focus(); };
            return dialog.ShowDialog() == true;
        }

        private void OnKey(object sender, KeyEventArgs args)
        {
            if (args.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && bookshelfPage.Visibility == Visibility.Visible)
            {
                args.Handled = true;
                bookshelf.FocusSearch();
            }
            else if (args.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                args.Handled = true;
                PickFile();
            }
            else if (args.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None &&
                currentPage == Page.Reading &&
                !(Keyboard.FocusedElement is ButtonBase) && !(Keyboard.FocusedElement is ComboBox) &&
                !(Keyboard.FocusedElement is TextBoxBase) &&
                !voices.IsDropDownOpen && !speeds.IsDropDownOpen && !shelf.IsDropDownOpen &&
                !Find<ComboBox>("ThemeChoice").IsDropDownOpen)
            {
                args.Handled = true;
                if (!args.IsRepeat) TogglePlayback();
            }
        }

        private void OnDragOver(object sender, DragEventArgs args)
        {
            args.Effects = !loading && DroppedBook(args.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
            args.Handled = true;
        }

        private static string DroppedBook(IDataObject data)
        {
            var files = data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length != 1 || !String.Equals(Path.GetExtension(files[0]), ".epub", StringComparison.OrdinalIgnoreCase)) return null;
            return files[0];
        }

        private sealed class VoiceOption
        {
            public readonly string Name;
            public readonly CultureInfo Culture;
            public readonly int? Speaker;
            public readonly NeuralVoice? Neural;
            public string Label { get; private set; }

            public VoiceOption(string name, CultureInfo culture)
            {
                Name = name;
                Culture = culture;
                if (culture == null) Label = "自动匹配";
                else if (name.Contains("Huihui")) Label = "系统中文 · 惠惠";
                else
                {
                    var shortName = name.Replace("Microsoft ", "").Split(new[] { " Desktop", " - " }, StringSplitOptions.None)[0];
                    Label = (culture.TwoLetterISOLanguageName == "en" ? "英语" : culture.TwoLetterISOLanguageName == "zh" ? "中文" : culture.TwoLetterISOLanguageName)
                        + " · " + shortName;
                }
            }

            public VoiceOption(int speaker, string language, string label)
            {
                Name = "kokoro:" + speaker;
                Culture = CultureInfo.GetCultureInfo(language);
                Speaker = speaker;
                Label = label;
            }

            public VoiceOption(string name, string label, NeuralVoice neural)
            {
                Name = name;
                Culture = CultureInfo.GetCultureInfo("zh-CN");
                Label = label;
                Neural = neural;
            }
        }

        private sealed class RateOption
        {
            public string Label { get; private set; }
            public readonly int Rate;
            public readonly float NaturalSpeed;

            public RateOption(string label, int rate, float naturalSpeed) { Label = label; Rate = rate; NaturalSpeed = naturalSpeed; }
        }
    }
}
