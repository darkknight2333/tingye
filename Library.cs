using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Tingye
{
    internal enum ShelfReadFilter { All, Read, Unread }

    internal sealed class ShelfEntry
    {
        public string Id, Author, Voice;
        public string Title { get; set; }
        public string CustomTitle;
        public string AutomaticTitle { get { return BookTitle.Primary(Title, Author); } }
        public string DisplayTitle { get { return String.IsNullOrWhiteSpace(CustomTitle) ? AutomaticTitle : CustomTitle; } }
        public int Chapter, Passage, ChapterLayout;
        public bool Finished, IsRead, VoiceMigrated;
        public DateTime? ImportedUtc, ReadUtc;
    }

    internal sealed class ImportedBook
    {
        public Book Book;
        public string Id;

        public ShelfEntry CreateShelfEntry()
        {
            return new ShelfEntry { Id = Id, Title = Book.Title, Author = Book.Author, ImportedUtc = DateTime.UtcNow };
        }
    }

    internal sealed class ShelfScan
    {
        public readonly List<ShelfEntry> Added = new List<ShelfEntry>();
        public readonly List<string> Unreadable = new List<string>();
        public readonly List<string> Unsupported = new List<string>();
        public Exception FirstReadError;
        public int Restored, Duplicates;
    }

    internal sealed class Library
    {
        private static readonly Regex BookId = new Regex(@"^[0-9a-f]{64}\z");
        private static readonly HashSet<string> OtherBookFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            ".mobi", ".azw", ".azw3", ".azw4", ".pdf", ".txt", ".fb2", ".fbz", ".rtf", ".doc", ".docx", ".djvu", ".cbz", ".cbr"
        };
        private readonly string directory;
        private readonly LinkedList<RecentBook> recentBooks = new LinkedList<RecentBook>();
        private bool writeBlocked, trustedIndex, trustedBackup;
        public string DirectoryPath { get { return directory; } }
        public string BooksDirectory { get { return Path.Combine(directory, "Books"); } }
        private string Index { get { return Path.Combine(directory, "shelf.xml"); } }
        private string Backup { get { return Index + ".bak"; } }
        public readonly List<ShelfEntry> Books = new List<ShelfEntry>();
        public string SelectedId, PreferredVoice, Theme;
        public string ReadingFont;
        public int ReadingSize;
        public int Rate;
        public bool LocalBreezeMigrated;
        public bool SortByReading, GroupByAuthor;
        public ShelfReadFilter ReadFilter;

        public Library(string directory) { this.directory = directory; }

        // Returns true when recovery or migration should be saved to the main index.
        public bool Load()
        {
            writeBlocked = true;
            var current = ReadSnapshot(Index);
            var backup = ReadSnapshot(Backup);
            var pending = PendingIndexes().Select(ReadSnapshot).Where(x => x != null).ToList();
            var saved = current;
            var newest = pending.FirstOrDefault(x => x.Books.Count > 0);
            if (newest != null && (current == null || current.Books.Count == 0 || newest.Modified >= current.Modified))
                saved = newest;
            if (saved == null || (saved.Books.Count == 0 && backup != null && backup.Books.Count > 0))
                saved = backup;

            var copies = Files(BooksDirectory, "*.epub")
                .Where(path => BookId.IsMatch(Path.GetFileNameWithoutExtension(path))).ToArray();
            var entries = saved == null ? new List<ShelfEntry>() : new List<ShelfEntry>(saved.Books);
            var known = new HashSet<string>(entries.Select(item => item.Id));
            var missing = copies.Where(path => !known.Contains(Path.GetFileNameWithoutExtension(path))).ToArray();
            if (missing.Length > 0 || saved == null)
            {
                var legacy = Files(directory, "shelf.before-*.xml")
                    .OrderByDescending(File.GetLastWriteTimeUtc).Select(ReadSnapshot).Where(x => x != null).ToArray();
                var ids = new HashSet<string>(missing.Select(Path.GetFileNameWithoutExtension));
                foreach (var snapshot in pending.Concat(new[] { backup }).Concat(legacy).Where(x => x != null))
                {
                    var matching = snapshot.Books.Where(item => ids.Contains(item.Id) && known.Add(item.Id)).ToArray();
                    if (matching.Length == 0) continue;
                    if (entries.Count == 0) saved = snapshot;
                    entries.AddRange(matching);
                }
                foreach (var path in missing)
                {
                    string id = Path.GetFileNameWithoutExtension(path);
                    if (known.Contains(id)) continue;
                    Book book;
                    try { book = Epub.Load(path); }
                    catch (InvalidDataException) { continue; }
                    catch (XmlException) { continue; }
                    entries.Add(new ShelfEntry { Id = id, Title = book.Title, Author = book.Author });
                }
            }

            Books.Clear();
            Books.AddRange(entries);
            bool dated = false;
            foreach (var item in Books.Where(item => !item.ImportedUtc.HasValue))
            {
                string path = BookPath(item.Id);
                if (!File.Exists(path)) continue;
                item.ImportedUtc = File.GetCreationTimeUtc(path);
                dated = true;
            }
            SelectedId = saved == null ? null : saved.SelectedId;
            PreferredVoice = saved == null ? null : saved.PreferredVoice;
            Rate = saved == null ? 0 : saved.Rate;
            LocalBreezeMigrated = saved != null && saved.LocalBreezeMigrated;
            SortByReading = saved != null && saved.SortByReading;
            GroupByAuthor = saved != null && saved.GroupByAuthor;
            Theme = saved == null ? null : saved.Theme;
            ReadingFont = saved == null ? null : saved.ReadingFont;
            ReadingSize = saved == null ? 17 : saved.ReadingSize;
            ReadFilter = saved == null ? ShelfReadFilter.All : saved.ReadFilter;
            bool rebuilt = Books.Count != (current == null ? 0 : current.Books.Count);
            trustedIndex = current != null && !rebuilt;
            trustedBackup = backup != null && (backup.Books.Count > 0 || Books.Count == 0);
            if (saved == null && Books.Count == 0 && (File.Exists(Index) || File.Exists(Backup)))
                throw new InvalidDataException("没有找到可用的书架备份。原索引已保留。");
            writeBlocked = false;
            return saved != current || rebuilt || dated;
        }

        public void Save()
        {
            if (writeBlocked) throw new IOException("请重新打开听页，确认书架可读取后再保存。");
            Directory.CreateDirectory(directory);
            var root = new XElement("shelf", new XAttribute("selected", SelectedId ?? ""), new XAttribute("rate", Rate),
                new XAttribute("preferred-voice", PreferredVoice ?? ""),
                new XAttribute("local-breeze-migrated", LocalBreezeMigrated),
                new XAttribute("sort-by-reading", SortByReading), new XAttribute("group-by-author", GroupByAuthor),
                new XAttribute("theme", Theme ?? "green"), new XAttribute("read-filter", (int)ReadFilter),
                new XAttribute("reading-font", ReadingFont ?? "Microsoft YaHei"), new XAttribute("reading-size", ReadingSize),
                Books.Select(item => new XElement("book", new XAttribute("id", item.Id), new XAttribute("title", item.Title),
                    String.IsNullOrWhiteSpace(item.CustomTitle) ? null : new XAttribute("display-title", item.CustomTitle),
                    new XAttribute("author", item.Author ?? ""), new XAttribute("voice", item.Voice ?? ""),
                    new XAttribute("chapter", item.Chapter), new XAttribute("passage", item.Passage),
                    new XAttribute("grouped-chapters", item.ChapterLayout > 0), new XAttribute("chapter-layout", item.ChapterLayout),
                    new XAttribute("finished", item.Finished), new XAttribute("voice-migrated", item.VoiceMigrated),
                    new XAttribute("read", item.IsRead),
                    item.ImportedUtc.HasValue ? new XAttribute("imported-utc", item.ImportedUtc.Value) : null,
                    item.ReadUtc.HasValue ? new XAttribute("read-utc", item.ReadUtc.Value) : null)));
            bool hasIndex = File.Exists(Index);
            if ((!hasIndex || !trustedIndex) && (!trustedBackup || !File.Exists(Backup)))
            {
                WriteSnapshot(Backup, root, DamagedCopy(Backup));
                trustedBackup = true;
            }
            string previous = trustedIndex ? Backup : DamagedCopy(Index);
            WriteSnapshot(Index, root, previous);
            trustedIndex = trustedBackup = true;
            foreach (var path in PendingIndexes())
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void WriteSnapshot(string destination, XElement root, string previous)
        {
            string staging = Path.Combine(Path.GetDirectoryName(destination), "shelf.xml.tmp-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                root.Save(stream);
                stream.Flush(true);
            }
            if (File.Exists(destination)) File.Replace(staging, destination, previous);
            else File.Move(staging, destination);
        }

        private static string DamagedCopy(string path)
        {
            return path + ".damaged-" + Guid.NewGuid().ToString("N");
        }

        private IEnumerable<string> PendingIndexes()
        {
            const string prefix = "shelf.xml.tmp";
            return Files(directory, prefix + "*").Where(path => {
                string name = Path.GetFileName(path);
                Guid id;
                return name == prefix || (name.StartsWith(prefix + "-", StringComparison.Ordinal) &&
                    Guid.TryParseExact(name.Substring(prefix.Length + 1), "N", out id));
            }).OrderByDescending(File.GetLastWriteTimeUtc);
        }

        private static string[] Files(string path, string pattern)
        {
            try { return Directory.GetFiles(path, pattern); }
            catch (DirectoryNotFoundException) { return new string[0]; }
        }

        private static Snapshot ReadSnapshot(string path)
        {
            XElement root;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                using (var reader = XmlReader.Create(stream, settings))
                    root = XElement.Load(reader);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (XmlException) { return null; }
            if (root.Name != "shelf") return null;

            var snapshot = new Snapshot {
                Modified = File.GetLastWriteTimeUtc(path), SelectedId = (string)root.Attribute("selected"),
                PreferredVoice = (string)root.Attribute("preferred-voice"),
                Rate = ReadInt(root, "rate"), LocalBreezeMigrated = (string)root.Attribute("local-breeze-migrated") == "true",
                SortByReading = (string)root.Attribute("sort-by-reading") == "true",
                GroupByAuthor = (string)root.Attribute("group-by-author") == "true",
                Theme = (string)root.Attribute("theme"),
                ReadingFont = (string)root.Attribute("reading-font"), ReadingSize = ReadInt(root, "reading-size"),
                ReadFilter = (ShelfReadFilter)ReadInt(root, "read-filter")
            };
            if (!Enum.IsDefined(typeof(ShelfReadFilter), snapshot.ReadFilter)) snapshot.ReadFilter = ShelfReadFilter.All;
            var ids = new HashSet<string>();
            foreach (var item in root.Elements("book"))
            {
                string id = (string)item.Attribute("id");
                string title = (string)item.Attribute("title");
                if (id == null || !BookId.IsMatch(id) || title == null || !ids.Add(id)) return null;
                snapshot.Books.Add(new ShelfEntry {
                    Id = id, Title = title, CustomTitle = (string)item.Attribute("display-title"), Author = (string)item.Attribute("author"),
                    Voice = (string)item.Attribute("voice"), Chapter = ReadInt(item, "chapter"),
                    Passage = ReadInt(item, "passage"), Finished = (string)item.Attribute("finished") == "true",
                    IsRead = (string)(item.Attribute("read") ?? item.Attribute("finished")) == "true",
                    ChapterLayout = Math.Max(ReadInt(item, "chapter-layout"), (string)item.Attribute("grouped-chapters") == "true" ? 1 : 0),
                    VoiceMigrated = (string)item.Attribute("voice-migrated") == "true",
                    ImportedUtc = ReadDate(item, "imported-utc"), ReadUtc = ReadDate(item, "read-utc")
                });
            }
            return snapshot;
        }

        private sealed class Snapshot
        {
            public readonly List<ShelfEntry> Books = new List<ShelfEntry>();
            public DateTime Modified;
            public string SelectedId, PreferredVoice, Theme;
            public string ReadingFont;
            public int ReadingSize;
            public int Rate;
            public bool LocalBreezeMigrated, SortByReading, GroupByAuthor;
            public ShelfReadFilter ReadFilter;
        }

        public Book Open(ShelfEntry item)
        {
            var file = new FileInfo(BookPath(item.Id));
            long length = file.Length;
            DateTime modified = file.LastWriteTimeUtc;
            var node = recentBooks.First;
            while (node != null && node.Value.Id != item.Id) node = node.Next;
            if (node != null)
            {
                recentBooks.Remove(node);
                if (node.Value.Length == length && node.Value.Modified == modified)
                {
                    recentBooks.AddFirst(node);
                    return node.Value.Book;
                }
            }
            var book = Epub.Load(file.FullName, item.Title);
            book.IndexChapterLengths();
            // Keep small books ready for switching. Large books live only in the active reader.
            if (book.CharacterCount <= 2000000 && (book.Cover == null || book.Cover.Length <= 2097152) &&
                book.Chapters.Sum(chapter => chapter.Passages.Count + chapter.Sections.Count) <= 50000)
            {
                recentBooks.AddFirst(new RecentBook { Id = item.Id, Book = book, Length = length, Modified = modified });
                if (recentBooks.Count > 2) recentBooks.RemoveLast();
            }
            return book;
        }

        private sealed class RecentBook
        {
            public string Id;
            public long Length;
            public DateTime Modified;
            public Book Book;
        }

        public byte[] ReadCover(ShelfEntry item)
        {
            return Epub.LoadCover(BookPath(item.Id));
        }

        public ImportedBook Import(string source)
        {
            Directory.CreateDirectory(BooksDirectory);
            return Import(source, null);
        }

        public ShelfScan ScanFolder(IEnumerable<string> indexed)
        {
            Directory.CreateDirectory(BooksDirectory);
            var known = new HashSet<string>(indexed, StringComparer.Ordinal);
            var result = new ShelfScan();
            foreach (var path in Directory.EnumerateFiles(BooksDirectory))
            {
                string extension = Path.GetExtension(path);
                if (!String.Equals(extension, ".epub", StringComparison.OrdinalIgnoreCase))
                {
                    if (OtherBookFormats.Contains(extension)) result.Unsupported.Add(Path.GetFileName(path));
                    continue;
                }
                if (known.Contains(Path.GetFileNameWithoutExtension(path))) continue;
                try
                {
                    var book = Import(path, known);
                    if (book == null)
                    {
                        result.Duplicates++;
                        continue;
                    }
                    if (known.Add(book.Id)) result.Added.Add(book.CreateShelfEntry());
                    else result.Restored++;
                }
                catch (Exception error)
                {
                    if (!(error is IOException || error is InvalidDataException || error is XmlException ||
                        error is UnauthorizedAccessException || error is DecoderFallbackException ||
                        error is NotSupportedException || error is ArgumentException)) throw;
                    result.Unreadable.Add(Path.GetFileName(path));
                    if (result.FirstReadError == null) result.FirstReadError = error;
                }
            }
            return result;
        }

        private ImportedBook Import(string source, HashSet<string> known)
        {
            using (var input = File.OpenRead(source))
            {
                string id;
                using (var hash = SHA256.Create())
                    id = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                string destination = BookPath(id);
                bool exists = File.Exists(destination);
                if (exists && known != null && known.Contains(id)) return null;
                var book = Epub.Load(source, Path.GetFileNameWithoutExtension(source));
                if (!exists)
                {
                    input.Position = 0;
                    StoreBook(input, destination);
                }
                return new ImportedBook { Id = id, Book = book };
            }
        }

        private void StoreBook(Stream input, string destination)
        {
            string staging = Path.Combine(BooksDirectory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                    output.Flush(true);
                }
                if (!File.Exists(destination)) File.Move(staging, destination);
            }
            finally { File.Delete(staging); }
        }

        private string BookPath(string id) { return Path.Combine(BooksDirectory, id + ".epub"); }

        private static int ReadInt(XElement item, string name)
        {
            int value;
            return Int32.TryParse((string)item.Attribute(name), out value) ? value : 0;
        }

        private static DateTime? ReadDate(XElement item, string name)
        {
            DateTimeOffset time;
            return DateTimeOffset.TryParse((string)item.Attribute(name), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out time) ? time.UtcDateTime : (DateTime?)null;
        }
    }
}
