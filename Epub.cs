using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Tingye
{
    internal sealed class Book
    {
        public string Title, Author, Language;
        public byte[] Cover;
        public readonly List<Chapter> Chapters = new List<Chapter>();
        public readonly List<PassageRange> LegacyPositions = new List<PassageRange>();
        public readonly List<int> PreviousChapterStarts = new List<int>();
        public readonly Dictionary<int, List<ReadingNote>> Notes = new Dictionary<int, List<ReadingNote>>();
        public readonly Dictionary<int, string> SpeechOverrides = new Dictionary<int, string>();
        public int ChapterCount;
        public long CharacterCount;

        public string SpeechAt(int chapter, int passage)
        {
            var section = Chapters[chapter];
            string text;
            return SpeechOverrides.TryGetValue(section.Start + passage, out text) ? text : section.Passages[passage];
        }

        public bool FindSpeechPassage(ref int chapter, ref int passage)
        {
            for (; chapter < Chapters.Count; chapter++, passage = 0)
                for (; passage < Chapters[chapter].Passages.Count; passage++)
                    if (SpeechAt(chapter, passage).Length != 0) return true;
            return false;
        }

        public void IndexChapterLengths()
        {
            long total = 0;
            foreach (var chapter in Chapters)
            {
                chapter.CharacterStart = total;
                foreach (string passage in chapter.Passages) total += passage.Length;
            }
            CharacterCount = total;
        }

        public void RestoreLegacyPosition(ref int chapter, ref int passage)
        {
            var range = LegacyPositions[Math.Max(0, Math.Min(chapter, LegacyPositions.Count - 1))];
            RestorePosition(range.Start, range.Length, ref chapter, ref passage);
        }

        public void RestoreGroupedPosition(ref int chapter, ref int passage)
        {
            if (PreviousChapterStarts.Count == 0) return;
            int oldChapter = Math.Max(0, Math.Min(chapter, PreviousChapterStarts.Count - 1));
            int start = PreviousChapterStarts[oldChapter];
            var last = Chapters[Chapters.Count - 1];
            int end = oldChapter + 1 < PreviousChapterStarts.Count ? PreviousChapterStarts[oldChapter + 1] : last.Start + last.Passages.Count;
            RestorePosition(start, end - start, ref chapter, ref passage);
        }

        private void RestorePosition(int start, int length, ref int chapter, ref int passage)
        {
            int index = start + Math.Max(0, Math.Min(passage, length - 1));
            chapter = Chapters.FindLastIndex(x => x.Start <= index);
            passage = Math.Min(index - Chapters[chapter].Start, Chapters[chapter].Passages.Count - 1);
        }
    }

    internal sealed class ReadingNote
    {
        public ReadingNote(string marker, string text)
        {
            Marker = marker;
            Text = text;
        }

        public string Marker { get; private set; }
        public string Text { get; private set; }
    }

    internal sealed class Chapter
    {
        public string Title { get; set; }
        public int Start, Number;
        public long CharacterStart;
        public DirectoryHeading Heading;
        public readonly List<string> Passages = new List<string>();
        public readonly List<Section> Sections = new List<Section>();
    }

    internal sealed class Section
    {
        public string Title;
        public int Passage;
        public DirectoryHeading Heading;
    }

    internal sealed class DirectoryHeading
    {
        public readonly string Title;
        public readonly DirectoryHeading Parent;

        public DirectoryHeading(string title, DirectoryHeading parent)
        {
            Title = title;
            Parent = parent;
        }
    }

    internal struct PassageRange
    {
        public readonly int Start, Length;
        public PassageRange(int start, int length) { Start = start; Length = length; }
    }

    internal sealed class Epub
    {
        private const int DocumentLimit = 16 * 1024 * 1024;
        private const long BookLimit = 128L * 1024 * 1024;
        private static readonly Uri ArchiveRoot = new Uri("https://epub.invalid/");
        private static readonly Regex Space = new Regex(@"[\s\u200B]+", RegexOptions.Compiled);
        private static readonly Regex NoteMarker = new Regex(
            @"^(?:(?:注(?:释)?|註(?:釋)?|(?:foot|end)?note)\s*)?\p{N}*$|^[\[［(（【〔][\p{N}a-z\s]+[\]］)）】〕]$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Entity = new Regex(@"&[a-zA-Z][a-zA-Z0-9]+;", RegexOptions.Compiled);
        private static readonly Regex HtmlEnd = new Regex(@"</html\s*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.RightToLeft);
        private static readonly Regex ChapterNumber = new Regex(
            @"^(?:第\s*[\d零〇一二三四五六七八九十百千两兩壹貳贰參叁肆伍陆陸柒捌玖拾]+\s*章|chapter\s+(?:\d+|[ivxlcdm]+)\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Supplement = new Regex(
            @"^(?:(?:序|序章|前言|楔子|引言|序曲|尾声|尾聲|致谢|致謝)(?:$|[\s:：])|附录|附錄|版权|版權|参考文献|參考文獻|(?:preface|foreword|prologue|introduction|afterword|epilogue|appendix|bibliography|acknowledg(?:e)?ments?)\b)|(?:序言|后记|後記|译序|譯序)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly HashSet<string> Blocks = new HashSet<string>(
            "address article aside blockquote dd div dl dt figcaption figure footer h1 h2 h3 h4 h5 h6 header hr li main ol p pre section table td th ul".Split(' '));
        private static readonly HashSet<string> Hidden = new HashSet<string>(
            "audio canvas iframe math nav noscript rp rt script style svg template video".Split(' '));

        private readonly ZipArchive archive;
        private readonly Dictionary<string, NoteDocument> noteDocuments = new Dictionary<string, NoteDocument>(StringComparer.Ordinal);
        private readonly Dictionary<string, NoteSource> noteSources = new Dictionary<string, NoteSource>(StringComparer.Ordinal);
        private long bytesRead;

        private Epub(ZipArchive archive) { this.archive = archive; }

        public static Book Load(string path, string fallbackTitle = null)
        {
            using (var archive = ZipFile.OpenRead(path))
                return new Epub(archive).ReadBook(fallbackTitle ?? Path.GetFileNameWithoutExtension(path));
        }

        public static byte[] LoadCover(string path)
        {
            using (var archive = ZipFile.OpenRead(path))
            {
                var epub = new Epub(archive);
                string packagePath;
                var package = epub.ReadPackage(out packagePath);
                var items = ManifestItems(Child(package.Root, "manifest"));
                return epub.ReadCover(packagePath, items, IndexManifest(items), Child(package.Root, "metadata"), epub.ReadEncryptedPaths());
            }
        }

        private XDocument ReadPackage(out string packagePath)
        {
            var container = ReadXml("META-INF/container.xml");
            var rootfile = Find(container, "rootfile");
            packagePath = Resolve("", Attr(rootfile, "full-path"));
            if (String.IsNullOrEmpty(packagePath)) throw new InvalidDataException("找不到 EPUB 的内容清单。");
            return ReadXml(packagePath);
        }

        private static List<XElement> ManifestItems(XElement manifest)
        {
            if (manifest == null) throw new InvalidDataException("EPUB 的文件清单不完整。");
            return manifest.Elements().Where(x => x.Name.LocalName == "item" && Attr(x, "id") != "").ToList();
        }

        private static Dictionary<string, XElement> IndexManifest(List<XElement> items)
        {
            return items.GroupBy(x => Attr(x, "id")).ToDictionary(g => g.Key, g => g.First());
        }

        private Book ReadBook(string fallbackTitle)
        {
            string packagePath;
            var package = ReadPackage(out packagePath);
            var metadata = Child(package.Root, "metadata");
            var spine = Child(package.Root, "spine");
            var manifest = Child(package.Root, "manifest");
            if (spine == null || manifest == null) throw new InvalidDataException("EPUB 的文件清单或阅读顺序不完整。");

            var items = ManifestItems(manifest);
            var byId = IndexManifest(items);
            var directory = ReadDirectory(packagePath, items, byId, spine);
            var guide = Child(package.Root, "guide");
            var contentsPages = new HashSet<string>(StringComparer.Ordinal);
            if (guide != null)
                foreach (var reference in guide.Elements().Where(x => HasToken(x, "type", "toc")))
                    contentsPages.Add(Resolve(packagePath, Attr(reference, "href")));
            var encrypted = ReadEncryptedPaths();
            var textPaths = new HashSet<string>(items.Where(x => Attr(x, "media-type") == "application/xhtml+xml" ||
                Attr(x, "media-type") == "text/html").Select(x => Resolve(packagePath, Attr(x, "href"))), StringComparer.Ordinal);
            var book = new Book {
                Title = Clean(Value(Child(metadata, "title"))),
                Author = Clean(Value(Child(metadata, "creator"))),
                Language = Value(Child(metadata, "language")).Trim()
            };
            if (book.Title.Length == 0) book.Title = fallbackTitle;
            book.Cover = ReadCover(packagePath, items, byId, metadata, encrypted);

            int unreadable = 0;
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var segments = new List<SpineSegment>();
            foreach (var reference in spine.Elements().Where(x => x.Name.LocalName == "itemref" && Attr(x, "linear") != "no"))
            {
                XElement item;
                var visited = new HashSet<string>();
                string id = Attr(reference, "idref");
                while (visited.Add(id) && byId.TryGetValue(id, out item))
                {
                    var media = Attr(item, "media-type");
                    if (media == "application/xhtml+xml" || media == "text/html")
                    {
                        var path = Resolve(packagePath, Attr(item, "href"));
                        if (path == null || HasToken(item, "nav") || !paths.Add(path)) break;
                        if (encrypted.Contains(path)) throw new InvalidDataException("这本书的正文受到 DRM 加密保护。");
                        List<TocEntry> entries;
                        directory.TryGetValue(path, out entries);
                        try { ReadSegments(segments, path, entries, contentsPages.Contains(path), encrypted, textPaths); }
                        catch (XmlException) { unreadable++; }
                        catch (FileNotFoundException) { unreadable++; }
                        break;
                    }
                    id = Attr(item, "fallback");
                }
            }

            if (segments.Count == 0) throw new InvalidDataException("没有找到可朗读的正文。这本书可能只有图片，或正文文件已损坏。");
            if (unreadable > 0) throw new InvalidDataException("有 " + unreadable + " 个正文文件缺失或格式损坏，无法完整导入。");
            GroupChapters(book, segments);
            foreach (var source in noteSources.Values) source.SkipDefinitions(book);
            return book;
        }

        private void ReadSegments(List<SpineSegment> segments, string path, List<TocEntry> entries, bool contentsPage,
            HashSet<string> encrypted, HashSet<string> textPaths)
        {
            NoteDocument noteDocument;
            if (!noteDocuments.TryGetValue(path, out noteDocument) || noteDocument == null)
                noteDocument = new NoteDocument(ReadXml(path, allowTrailingImages: true));
            noteDocuments.Remove(path);
            var document = noteDocument.Document;
            var body = Find(document, "body");
            if (body == null) return;
            entries = entries ?? new List<TocEntry>();
            var flow = new TextFlow(entries.Select(x => x.Anchor),
                link => ReadNote(link, path, noteDocument, encrypted, textPaths));
            flow.Visit(body);
            flow.Flush();
            if (flow.Passages.Count == 0) return;
            var noteSource = GetNoteSource(path);
            noteSource.IndexRanges(noteDocument.Anchors.Value, flow.BlockRanges);

            var links = new SortedDictionary<int, TocEntry>();
            foreach (var entry in entries)
            {
                int offset = 0;
                if (entry.Anchor != "" && !flow.Anchors.TryGetValue(entry.Anchor, out offset)) continue;
                if (offset >= flow.Passages.Count) continue;
                TocEntry existing;
                if (!links.TryGetValue(offset, out existing) || entry.Depth < existing.Depth ||
                    (entry.Depth == existing.Depth && entry.IsNumberedChapter && !existing.IsNumberedChapter))
                    links[offset] = entry;
            }
            flow.Headings.Sort((left, right) => left.Passage.CompareTo(right.Passage));
            string documentTitle = Clean(Value(Find(document, "title")));
            string firstHeading = flow.Headings.Count > 0 && flow.Headings[0].Passage == 0 ? flow.Headings[0].Title : null;
            string fallback = firstHeading ?? (links.Count == 0 ? documentTitle : null);
            contentsPage = contentsPage && entries.All(x => IsContentsTitle(x.Title));
            if (!links.ContainsKey(0)) links.Add(0, null);

            var offsets = links.Keys.ToList();
            int firstSegment = segments.Count;
            int headingIndex = 0;
            for (int index = 0; index < offsets.Count; index++)
            {
                int start = offsets[index];
                int end = index + 1 < offsets.Count ? offsets[index + 1] : flow.Passages.Count;
                var link = links[start];
                var segment = new SpineSegment {
                    Toc = link, Title = link == null ? fallback : link.Title, DocumentTitle = documentTitle,
                    Passages = flow.Passages.GetRange(start, end - start), IsContents = contentsPage,
                    NoteSource = index == 0 ? noteSource : null
                };
                while (headingIndex < flow.Headings.Count && flow.Headings[headingIndex].Passage < end)
                {
                    var heading = flow.Headings[headingIndex++];
                    heading.Passage -= start;
                    segment.Headings.Add(heading);
                }
                segments.Add(segment);
            }
            // Store note positions relative to each segment before the TOC groups or splits them.
            int segmentIndex = 0;
            foreach (var note in flow.Notes.OrderBy(x => x.Key))
            {
                while (segmentIndex + 1 < offsets.Count && note.Key >= offsets[segmentIndex + 1]) segmentIndex++;
                segments[firstSegment + segmentIndex].Notes.Add(note.Key - offsets[segmentIndex], note.Value);
            }
            segmentIndex = 0;
            foreach (var speech in flow.SpeechOverrides.OrderBy(x => x.Key))
            {
                while (segmentIndex + 1 < offsets.Count && speech.Key >= offsets[segmentIndex + 1]) segmentIndex++;
                segments[firstSegment + segmentIndex].SpeechOverrides.Add(speech.Key - offsets[segmentIndex], speech.Value);
            }
        }

        private static ILookup<string, XElement> IndexNoteAnchors(XDocument document)
        {
            return document.Descendants().Select(element => new { Element = element, Anchor = Anchor(element) })
                .Where(x => x.Anchor.Length != 0).ToLookup(x => x.Anchor, x => x.Element, StringComparer.Ordinal);
        }

        private ReadingNote ReadNote(XElement link, string source, NoteDocument local,
            HashSet<string> encrypted, HashSet<string> textPaths)
        {
            string href = Attr(link, "href");
            int hash = href.IndexOf('#');
            if (hash < 0 || hash == href.Length - 1 || link.Ancestors().Any(IsNoteBlock) || IsBacklink(link)) return null;
            bool explicitReference = HasToken(link, "type", "noteref") || HasToken(link, "role", "doc-noteref") ||
                HasToken(link, "class", "duokan-footnote") || HasToken(link, "class", "noteref");
            string label = Clean(link.Value);
            string path = Resolve(source, href);
            if (path == null || !textPaths.Contains(path) || encrypted.Contains(path)) return null;
            string id = Uri.UnescapeDataString(href.Substring(hash + 1));

            ILookup<string, XElement> anchors;
            if (path == source) anchors = local.Anchors.Value;
            else
            {
                // Cross-file references may point to a document outside the reading order.
                if (!explicitReference && !id.StartsWith("note", StringComparison.OrdinalIgnoreCase) &&
                    !id.StartsWith("fn", StringComparison.OrdinalIgnoreCase) &&
                    !(label.Length > 2 && label.Length <= 12 && label[0] == '[' && label[label.Length - 1] == ']') &&
                    !link.Ancestors().TakeWhile(x => !Blocks.Contains(x.Name.LocalName))
                    .Any(x => x.Name.LocalName == "sup" || HasToken(x, "class", "note"))) return null;
                NoteDocument document;
                if (!noteDocuments.TryGetValue(path, out document))
                {
                    try { document = new NoteDocument(ReadXml(path, allowTrailingImages: true)); }
                    catch (XmlException) { document = null; }
                    catch (FileNotFoundException) { document = null; }
                    catch (InvalidDataException) { document = null; }
                    catch (DecoderFallbackException) { document = null; }
                    noteDocuments[path] = document;
                }
                if (document == null) return null;
                anchors = document.Anchors.Value;
            }

            var target = anchors[id].FirstOrDefault();
            var block = FindNoteBlock(target, explicitReference);
            string noteText = "";
            if (block != null && !link.AncestorsAndSelf().Contains(block))
            {
                string referenceId = Anchor(link);
                var flow = new TextFlow(Enumerable.Empty<string>(), skipLink: candidate => {
                    if (IsBacklink(candidate)) return true;
                    string backlink = Attr(candidate, "href");
                    int marker = backlink.IndexOf('#');
                    return referenceId.Length > 0 && marker >= 0 && Resolve(path, backlink) == source &&
                        Uri.UnescapeDataString(backlink.Substring(marker + 1)) == referenceId;
                });
                flow.Visit(block, includeHiddenRoot: true);
                flow.Flush();
                noteText = String.Join("\n", flow.Passages);
                if (noteText.Length != 0 && (IsNoteBlock(block) || IsStandaloneNote(block)))
                    GetNoteSource(path).ReferencedIds.Add(id);
            }
            if (noteText.Length == 0 && explicitReference)
            {
                var image = link.Descendants().FirstOrDefault(x => x.Name.LocalName == "img");
                noteText = Clean(Attr(image, "zy-footnote"));
                if (noteText.Length == 0) noteText = Clean(Attr(image, "alt"));
            }
            if (noteText.Length == 0) return null;
            return new ReadingNote(label.Length > 0 && label.Length <= 20 ? label : "注", noteText);
        }

        private static bool IsBacklink(XElement link)
        {
            return HasToken(link, "role", "doc-backlink") || HasToken(link, "type", "backlink");
        }

        private NoteSource GetNoteSource(string path)
        {
            NoteSource source;
            if (!noteSources.TryGetValue(path, out source)) noteSources.Add(path, source = new NoteSource());
            return source;
        }

        private static XElement FindNoteBlock(XElement target, bool allowUnmarked)
        {
            if (target == null) return null;
            var ancestors = target.AncestorsAndSelf();
            return ancestors.FirstOrDefault(IsNoteBlock) ?? (allowUnmarked
                ? ancestors.FirstOrDefault(x => Blocks.Contains(x.Name.LocalName) && x.Name.LocalName != "main")
                : null);
        }

        private static bool IsStandaloneNote(XElement element)
        {
            string name = element.Name.LocalName;
            return name == "p" || name == "li" || name == "dd" || name == "aside" ||
                (name == "div" && !element.Descendants().Any(x => Blocks.Contains(x.Name.LocalName)));
        }

        private static bool IsNoteBlock(XElement element)
        {
            if (!Blocks.Contains(element.Name.LocalName)) return false;
            return HasToken(element, "type", "footnote") || HasToken(element, "type", "endnote") ||
                HasToken(element, "type", "rearnote") || HasToken(element, "type", "annotation") ||
                HasToken(element, "role", "doc-footnote") || HasToken(element, "role", "doc-endnote") ||
                HasToken(element, "class", "footnote") || HasToken(element, "class", "endnote") ||
                HasToken(element, "class", "note") || HasToken(element, "class", "fnote") ||
                HasToken(element, "class", "duokan-footnote-item");
        }

        private static string Anchor(XElement element)
        {
            string id = Attr(element, "id");
            return id.Length == 0 && element.Name.LocalName == "a" ? Attr(element, "name") : id;
        }

        private static void GroupChapters(Book book, List<SpineSegment> segments)
        {
            bool skipContents = segments.Any(x => !x.IsContents);
            var body = segments.Where(x => !skipContents || !x.IsContents).ToList();
            var toc = body.Select(x => x.Toc).Where(x => x != null).ToList();
            var numbered = toc.Where(x => x.IsNumberedChapter).ToList();
            int chapterDepth = numbered.Count > 0 ? numbered.Min(x => x.Depth) :
                toc.Count > 0 ? toc.Min(x => x.Depth) : 0;
            bool flat = toc.All(x => x.Depth == chapterDepth);
            if (numbered.Count == 0 && !flat && toc.Count(x => x.Depth == chapterDepth) == 1 &&
                toc.First(x => x.Depth == chapterDepth).Title == book.Title)
                chapterDepth = toc.Where(x => x.Depth > chapterDepth).Min(x => x.Depth);

            // A few books put the chapter number on a separate page immediately before its TOC target.
            for (int i = 1; i < body.Count; i++)
            {
                var segment = body[i];
                var prior = body[i - 1];
                if (segment.Toc != null && segment.Toc.IsNumberedChapter && prior.Toc == null &&
                    prior.DocumentTitle == segment.DocumentTitle &&
                    String.Equals(prior.Title, ChapterNumber.Match(segment.Toc.Title).Value, StringComparison.OrdinalIgnoreCase))
                {
                    prior.Toc = segment.Toc;
                    segment.Toc = null;
                }
            }

            var headingLevels = new HashSet<int>(body.SelectMany(x => x.Headings).Select(x => x.Level));
            int mainHeading = headingLevels.Contains(2) && (headingLevels.Contains(3) || !headingLevels.Contains(1)) ? 2 : 1;
            bool headingFallback = toc.Count == 0 && headingLevels.Contains(mainHeading);
            bool flatHeadingSections = flat && headingLevels.Contains(2) && headingLevels.Contains(3);
            bool numberedFlat = flat && numbered.Count >= 2;
            bool mixedChapterDepth = numbered.Any(x => x.Depth > chapterDepth && !x.HasNumberedAncestor);
            Chapter chapter = null;
            int offset = 0;
            foreach (var segment in segments)
            {
                bool omit = skipContents && segment.IsContents;
                book.LegacyPositions.Add(new PassageRange(offset, omit ? 0 : segment.Passages.Count));
                if (omit) continue;
                if (segment.NoteSource != null) segment.NoteSource.BookStart = offset;
                foreach (var note in segment.Notes) book.Notes.Add(offset + note.Key, note.Value);
                foreach (var speech in segment.SpeechOverrides) book.SpeechOverrides.Add(offset + speech.Key, speech.Value);

                var link = segment.Toc;
                // Save the start positions from the previous layout, which used one chapter depth per book.
                if (mixedChapterDepth && (book.PreviousChapterStarts.Count == 0 || (link != null && link.Depth <= chapterDepth)))
                    book.PreviousChapterStarts.Add(offset);
                if (link != null)
                {
                    bool chapterInVolume = link.IsNumberedChapter && !link.HasNumberedAncestor && link.Depth > chapterDepth;
                    bool extra = IsContentsTitle(link.Title) ||
                        (numbered.Count > 0 && !link.IsNumberedChapter && link.Depth <= chapterDepth && Supplement.IsMatch(link.Title));
                    bool subsection = (!chapterInVolume && link.Depth > chapterDepth) ||
                        (numberedFlat && !link.IsNumberedChapter && !extra && chapter != null && chapter.Number > 0) ||
                        (flatHeadingSections && !extra && !link.IsNumberedChapter &&
                            segment.Headings.Any(x => x.Passage == 0 && x.Level == 3));
                    if (subsection && chapter != null)
                        AddSection(chapter, link.Title, chapter.Passages.Count, link.Heading);
                    else
                    {
                        bool main = !extra && (chapterInVolume || (link.Depth == chapterDepth && (!numberedFlat || link.IsNumberedChapter)));
                        chapter = BeginChapter(book, link.Title, offset, main);
                        chapter.Heading = link.Heading;
                    }
                }
                if (headingFallback)
                    chapter = AppendHeadings(book, chapter, segment, offset, mainHeading);
                else
                {
                    if (chapter == null) chapter = BeginChapter(book, toc.Count == 0 ? "正文" : "卷首", offset, false);
                    if (flatHeadingSections && chapter.Number > 0)
                        foreach (var heading in segment.Headings.Where(x => x.Level == 3))
                            AddSection(chapter, heading.Title, chapter.Passages.Count + heading.Passage);
                    chapter.Passages.AddRange(segment.Passages);
                }
                offset += segment.Passages.Count;
            }
        }

        private static Chapter AppendHeadings(Book book, Chapter chapter, SpineSegment segment, int offset, int mainHeading)
        {
            int start = 0;
            var headings = segment.Headings.Where(x => x.Level == mainHeading || x.Level == mainHeading + 1)
                .GroupBy(x => x.Passage).Select(group => group.OrderBy(x => x.Level).First());
            foreach (var heading in headings)
            {
                if (heading.Passage > start)
                {
                    if (chapter == null) chapter = BeginChapter(book, "卷首", offset, false);
                    chapter.Passages.AddRange(segment.Passages.GetRange(start, heading.Passage - start));
                }
                if (heading.Level == mainHeading)
                    chapter = BeginChapter(book, heading.Title, offset + heading.Passage, true);
                else if (chapter != null)
                    AddSection(chapter, heading.Title, chapter.Passages.Count);
                start = heading.Passage;
            }
            if (chapter == null) chapter = BeginChapter(book, "卷首", offset, false);
            chapter.Passages.AddRange(segment.Passages.GetRange(start, segment.Passages.Count - start));
            return chapter;
        }

        private static Chapter BeginChapter(Book book, string title, int offset, bool numbered)
        {
            int number = numbered ? ++book.ChapterCount : 0;
            var chapter = new Chapter { Title = String.IsNullOrEmpty(title) ? "第 " + number + " 章" : title, Start = offset, Number = number };
            book.Chapters.Add(chapter);
            return chapter;
        }

        private static void AddSection(Chapter chapter, string title, int start, DirectoryHeading heading = null)
        {
            if (chapter.Sections.Count == 0 || chapter.Sections[chapter.Sections.Count - 1].Passage != start)
                chapter.Sections.Add(new Section { Title = title, Passage = start, Heading = heading });
        }

        private static bool IsContentsTitle(string title)
        {
            return title == "目录" || title == "目錄" || title == "目 录" || title == "目 錄" ||
                String.Equals(title, "contents", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(title, "table of contents", StringComparison.OrdinalIgnoreCase);
        }

        private Dictionary<string, List<TocEntry>> ReadDirectory(string packagePath, List<XElement> items,
            Dictionary<string, XElement> byId, XElement spine)
        {
            var entries = new Dictionary<string, List<TocEntry>>(StringComparer.Ordinal);
            var headings = new Dictionary<XElement, DirectoryHeading>();
            var nav = items.FirstOrDefault(x => HasToken(x, "nav"));
            var navPath = Resolve(packagePath, Attr(nav, "href"));
            if (navPath != null)
            {
                var document = TryXml(navPath);
                if (document != null)
                {
                    var toc = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "nav" && HasToken(x, "type", "toc"));
                    if (toc != null)
                        foreach (var link in toc.Descendants().Where(x => x.Name.LocalName == "a"))
                        {
                            var owners = link.Ancestors().TakeWhile(x => x != toc).Where(x => x.Name.LocalName == "li").ToList();
                            var heading = ReadDirectoryHeading(headings, owners, NavLabel);
                            AddEntry(entries, navPath, Attr(link, "href"), Clean(link.Value), Math.Max(0, owners.Count - 1), heading);
                        }
                }
            }
            if (entries.Count > 0) return entries;

            XElement ncx;
            if (!byId.TryGetValue(Attr(spine, "toc"), out ncx))
                ncx = items.FirstOrDefault(x => Attr(x, "media-type") == "application/x-dtbncx+xml");
            var ncxPath = Resolve(packagePath, Attr(ncx, "href"));
            var legacy = ncxPath == null ? null : TryXml(ncxPath);
            if (legacy != null)
                foreach (var point in legacy.Descendants().Where(x => x.Name.LocalName == "navPoint"))
                {
                    var owners = point.AncestorsAndSelf().Where(x => x.Name.LocalName == "navPoint").ToList();
                    var heading = ReadDirectoryHeading(headings, owners, x => Value(Child(x, "navLabel")));
                    AddEntry(entries, ncxPath, Attr(Child(point, "content"), "src"), heading.Title, owners.Count - 1, heading);
                }
            return entries;
        }

        private static string NavLabel(XElement item)
        {
            return Value(item.Elements().FirstOrDefault(x => x.Name.LocalName == "a" || x.Name.LocalName == "span"));
        }

        private static DirectoryHeading ReadDirectoryHeading(Dictionary<XElement, DirectoryHeading> headings,
            List<XElement> owners, Func<XElement, string> readLabel)
        {
            DirectoryHeading parent = null;
            for (int index = owners.Count - 1; index >= 0; index--)
            {
                var owner = owners[index];
                DirectoryHeading heading;
                if (!headings.TryGetValue(owner, out heading))
                {
                    heading = new DirectoryHeading(Clean(readLabel(owner)), parent);
                    headings.Add(owner, heading);
                }
                parent = heading;
            }
            return parent;
        }

        private static void AddEntry(Dictionary<string, List<TocEntry>> toc, string source, string href, string title, int depth, DirectoryHeading heading)
        {
            string path = Resolve(source, href);
            if (path == null || title.Length == 0) return;
            int hash = href.IndexOf('#');
            string anchor = hash < 0 ? "" : Uri.UnescapeDataString(href.Substring(hash + 1));
            List<TocEntry> entries;
            if (!toc.TryGetValue(path, out entries)) toc[path] = entries = new List<TocEntry>();
            bool inChapter = false;
            for (var parent = heading == null ? null : heading.Parent; parent != null; parent = parent.Parent)
                if (ChapterNumber.IsMatch(parent.Title)) { inChapter = true; break; }
            entries.Add(new TocEntry { Anchor = anchor, Title = title, Depth = depth, Heading = heading,
                IsNumberedChapter = ChapterNumber.IsMatch(title), HasNumberedAncestor = inChapter });
        }

        private HashSet<string> ReadEncryptedPaths()
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var encryption = TryXml("META-INF/encryption.xml");
            if (encryption != null)
                foreach (var reference in encryption.Descendants().Where(x => x.Name.LocalName == "CipherReference"))
                {
                    var path = Resolve("", Attr(reference, "URI"));
                    if (path != null) paths.Add(path);
                }
            return paths;
        }

        private byte[] ReadCover(string source, List<XElement> items, Dictionary<string, XElement> byId,
            XElement metadata, HashSet<string> encrypted)
        {
            var cover = items.FirstOrDefault(x => HasToken(x, "cover-image"));
            if (cover == null && metadata != null)
            {
                var marker = metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "meta" && Attr(x, "name") == "cover");
                byId.TryGetValue(Attr(marker, "content"), out cover);
            }
            var path = Resolve(source, Attr(cover, "href"));
            if (path == null || encrypted.Contains(path)) return null;
            var entry = archive.GetEntry(path);
            if (entry == null || entry.Length > 8 * 1024 * 1024) return null;
            try { return ReadBytes(path); }
            catch (InvalidDataException) { return null; }
        }

        private XDocument TryXml(string path)
        {
            try { return ReadXml(path); }
            catch (XmlException) { return null; }
            catch (FileNotFoundException) { return null; }
        }

        private XDocument ReadXml(string path, bool allowTrailingImages = false)
        {
            var data = ReadBytes(path);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null,
                MaxCharactersInDocument = DocumentLimit, IgnoreComments = true, IgnoreProcessingInstructions = true };
            try
            {
                using (var stream = new MemoryStream(data, false))
                using (var reader = XmlReader.Create(stream, settings))
                    return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                using (var stream = new MemoryStream(data, false))
                using (var input = new StreamReader(stream, new UTF8Encoding(false, true), true))
                {
                    string raw = input.ReadToEnd();
                    string repaired = Entity.Replace(raw, m => {
                        if (m.Value == "&amp;" || m.Value == "&quot;" || m.Value == "&lt;" || m.Value == "&gt;" || m.Value == "&apos;") return m.Value;
                        string decoded = WebUtility.HtmlDecode(m.Value);
                        return decoded == m.Value ? "&amp;" + m.Value.Substring(1) : "&#" + Char.ConvertToUtf32(decoded, 0) + ";";
                    });
                    if (allowTrailingImages) repaired = RemoveTrailingImages(repaired);
                    if (repaired == raw) throw;
                    using (var reader = XmlReader.Create(new StringReader(repaired), settings))
                        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
                }
            }
        }

        private static string RemoveTrailingImages(string markup)
        {
            var closingTag = HtmlEnd.Match(markup);
            if (!closingTag.Success) return markup;
            int boundary = closingTag.Index + closingTag.Length;
            if (boundary == markup.Length) return markup;

            var settings = new XmlReaderSettings {
                ConformanceLevel = ConformanceLevel.Fragment, DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = DocumentLimit, IgnoreWhitespace = true,
                IgnoreComments = true, IgnoreProcessingInstructions = true
            };
            bool hasImage = false;
            try
            {
                using (var reader = XmlReader.Create(new StringReader(markup.Substring(boundary)), settings))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element || !reader.IsEmptyElement ||
                            !reader.LocalName.Equals("img", StringComparison.OrdinalIgnoreCase)) return markup;
                        hasImage = true;
                    }
                }
            }
            catch (XmlException) { return markup; }
            // A trailing image contributes no text to the reading flow.
            return hasImage ? markup.Substring(0, boundary) : markup;
        }

        private byte[] ReadBytes(string path)
        {
            var entry = archive.GetEntry(path);
            if (entry == null) throw new FileNotFoundException("EPUB 缺少文件 " + path);
            if (entry.Length > DocumentLimit || bytesRead + entry.Length > BookLimit)
                throw new InvalidDataException("这本书的正文展开后过大，无法导入。");
            using (var stream = entry.Open())
            {
                var data = new byte[(int)entry.Length];
                int offset = 0;
                while (offset < data.Length)
                {
                    int count = stream.Read(data, offset, data.Length - offset);
                    if (count == 0) throw new InvalidDataException("EPUB 内部文件长度不完整。");
                    offset += count;
                }
                if (stream.ReadByte() != -1) throw new InvalidDataException("EPUB 内部文件长度与清单不符。");
                bytesRead += data.Length;
                return data;
            }
        }

        private static string Resolve(string source, string href)
        {
            if (String.IsNullOrWhiteSpace(href)) return null;
            string basePath = String.Join("/", source.Split('/').Select(Uri.EscapeDataString));
            Uri result;
            if (!Uri.TryCreate(new Uri(ArchiveRoot, basePath), href, out result) ||
                result.Scheme != ArchiveRoot.Scheme || result.Authority != ArchiveRoot.Authority) return null;
            return Uri.UnescapeDataString(result.AbsolutePath.TrimStart('/'));
        }

        private static XElement Find(XDocument root, string name) { return root.Descendants().FirstOrDefault(x => x.Name.LocalName == name); }
        private static XElement Child(XElement root, string name) { return root == null ? null : root.Elements().FirstOrDefault(x => x.Name.LocalName == name); }
        private static string Attr(XElement item, string name)
        {
            for (var attr = item == null ? null : item.FirstAttribute; attr != null; attr = attr.NextAttribute)
                if (attr.Name.LocalName == name) return attr.Value;
            return "";
        }
        private static string Value(XElement item) { return item == null ? "" : item.Value; }
        private static string Clean(string text) { return Space.Replace(text, " ").Trim(); }
        private static bool HasToken(XElement item, string token) { return HasToken(item, "properties", token); }
        private static bool HasToken(XElement item, string attribute, string token) { return Attr(item, attribute).Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Contains(token); }

        private sealed class TocEntry
        {
            public string Anchor, Title;
            public int Depth;
            public DirectoryHeading Heading;
            public bool IsNumberedChapter, HasNumberedAncestor;
        }

        private sealed class NoteDocument
        {
            public readonly XDocument Document;
            public readonly Lazy<ILookup<string, XElement>> Anchors;

            public NoteDocument(XDocument document)
            {
                Document = document;
                Anchors = new Lazy<ILookup<string, XElement>>(() => IndexNoteAnchors(document));
            }
        }

        private sealed class NoteSource
        {
            public readonly HashSet<string> ReferencedIds = new HashSet<string>(StringComparer.Ordinal);
            private readonly Dictionary<string, PassageRange> ranges = new Dictionary<string, PassageRange>(StringComparer.Ordinal);
            public int BookStart = -1;

            public void IndexRanges(ILookup<string, XElement> anchors, Dictionary<XElement, PassageRange> blocks)
            {
                foreach (var anchor in anchors)
                {
                    var block = FindNoteBlock(anchor.First(), true);
                    PassageRange range;
                    if (block != null && blocks.TryGetValue(block, out range)) ranges.Add(anchor.Key, range);
                }
            }

            public void SkipDefinitions(Book book)
            {
                if (BookStart < 0) return;
                foreach (string id in ReferencedIds)
                {
                    PassageRange range;
                    if (!ranges.TryGetValue(id, out range)) continue;
                    for (int passage = BookStart + range.Start; passage < BookStart + range.Start + range.Length; passage++)
                        book.SpeechOverrides[passage] = "";
                }
            }
        }

        private sealed class SpineSegment
        {
            public string Title, DocumentTitle;
            public TocEntry Toc;
            public bool IsContents;
            public List<string> Passages;
            public NoteSource NoteSource;
            public readonly Dictionary<int, List<ReadingNote>> Notes = new Dictionary<int, List<ReadingNote>>();
            public readonly Dictionary<int, string> SpeechOverrides = new Dictionary<int, string>();
            public readonly List<BodyHeading> Headings = new List<BodyHeading>();
        }

        private sealed class BodyHeading
        {
            public string Title;
            public int Passage, Level;
        }

        private sealed class TextFlow
        {
            private const int PassageLength = 400;
            private readonly StringBuilder text = new StringBuilder();
            private readonly HashSet<string> targets;
            private readonly Func<XElement, ReadingNote> readNote;
            private readonly Func<XElement, bool> skipLink;
            private readonly List<KeyValuePair<int, ReadingNote>> pendingNotes = new List<KeyValuePair<int, ReadingNote>>();
            private readonly List<PassageRange> omittedText = new List<PassageRange>();
            public readonly List<string> Passages = new List<string>();
            public readonly Dictionary<string, int> Anchors = new Dictionary<string, int>();
            public readonly List<BodyHeading> Headings = new List<BodyHeading>();
            public readonly Dictionary<int, List<ReadingNote>> Notes = new Dictionary<int, List<ReadingNote>>();
            public readonly Dictionary<int, string> SpeechOverrides = new Dictionary<int, string>();
            public readonly Dictionary<XElement, PassageRange> BlockRanges = new Dictionary<XElement, PassageRange>();

            public TextFlow(IEnumerable<string> anchors, Func<XElement, ReadingNote> readNote = null,
                Func<XElement, bool> skipLink = null)
            {
                targets = new HashSet<string>(anchors.Where(x => x.Length > 0));
                this.readNote = readNote;
                this.skipLink = skipLink;
            }

            public void Visit(XNode node, int depth = 0, bool includeHiddenRoot = false, bool omitFromSpeech = false)
            {
                if (depth > 200) throw new InvalidDataException("正文的嵌套层级过深。");
                var plain = node as XText;
                if (plain != null)
                {
                    if (omitFromSpeech && plain.Value.Length != 0)
                        omittedText.Add(new PassageRange(text.Length, plain.Value.Length));
                    text.Append(plain.Value);
                    return;
                }
                var element = node as XElement;
                if (element == null) return;
                string name = element.Name.LocalName.ToLowerInvariant();
                if (Hidden.Contains(name) || (!includeHiddenRoot &&
                    ((element.HasAttributes && element.Attributes().Any(x => x.Name.LocalName == "hidden")) ||
                    String.Equals(Attr(element, "aria-hidden"), "true", StringComparison.OrdinalIgnoreCase)))) return;

                bool block = Blocks.Contains(name) || name == "br";
                string anchor = Anchor(element);
                bool isTarget = anchor.Length > 0 && targets.Contains(anchor);
                if (block || isTarget) Flush();
                if (isTarget && !Anchors.ContainsKey(anchor)) Anchors.Add(anchor, Passages.Count);
                if (name == "a")
                {
                    if (skipLink != null && skipLink(element)) return;
                    var note = readNote == null ? null : readNote(element);
                    if (note != null)
                    {
                        pendingNotes.Add(new KeyValuePair<int, ReadingNote>(text.Length, note));
                        string label = Clean(element.Value);
                        omitFromSpeech = omitFromSpeech || (label.Length > 0 &&
                            (NoteMarker.IsMatch(label) || !SpeechText.IsSpeakable(label)));
                    }
                }
                int start = Passages.Count;
                for (var child = element.FirstNode; child != null; child = child.NextNode)
                    Visit(child, depth + 1, omitFromSpeech: omitFromSpeech);
                if (block) Flush();
                if (block && readNote != null && Passages.Count > start)
                    BlockRanges.Add(element, new PassageRange(start, Passages.Count - start));
                if (Passages.Count > start && name.Length == 2 && name[0] == 'h' && name[1] >= '1' && name[1] <= '6')
                    Headings.Add(new BodyHeading { Passage = start, Level = name[1] - '0', Title = Clean(element.Value) });
            }

            public void Flush()
            {
                if (text.Length == 0 && pendingNotes.Count == 0) return;
                string raw = text.ToString();
                string paragraph = Clean(raw);
                text.Clear();
                int noteIndex = 0;
                var noteOffsets = pendingNotes.Count == 0 ? null : new NormalizedOffsets(raw, paragraph.Length);
                if (omittedText.Count > 0)
                {
                    var speechOffsets = new NormalizedOffsets(raw, paragraph.Length);
                    for (int i = 0; i < omittedText.Count; i++)
                    {
                        var range = omittedText[i];
                        int from = speechOffsets.Map(range.Start);
                        int to = speechOffsets.Map(range.Start + range.Length);
                        omittedText[i] = new PassageRange(from, to - from);
                    }
                }
                int omission = 0;
                int start = 0;
                while (start < paragraph.Length)
                {
                    int end = Math.Min(start + PassageLength, paragraph.Length);
                    if (end < paragraph.Length)
                    {
                        int boundary = paragraph.LastIndexOfAny("。！？；.!?;".ToCharArray(), end - 1, PassageLength / 2);
                        if (boundary < 0) boundary = paragraph.LastIndexOfAny("，、, ".ToCharArray(), end - 1, PassageLength / 2);
                        if (boundary >= 0) end = boundary + 1;
                        if (Char.IsHighSurrogate(paragraph[end - 1])) end--;
                    }
                    string passage = paragraph.Substring(start, end - start).Trim();
                    if (passage.Length > 0)
                    {
                        while (noteIndex < pendingNotes.Count)
                        {
                            var note = pendingNotes[noteIndex];
                            int position = noteOffsets.Map(note.Key);
                            if (position >= end && end < paragraph.Length) break;
                            AddNote(Passages.Count, note.Value);
                            noteIndex++;
                        }
                        StoreSpeechOverride(paragraph, start, end, ref omission);
                        Passages.Add(passage);
                    }
                    start = end;
                }
                // Some EPUBs put an image-only reference immediately after the text block.
                if (Passages.Count > 0)
                    while (noteIndex < pendingNotes.Count) AddNote(Passages.Count - 1, pendingNotes[noteIndex++].Value);
                pendingNotes.Clear();
                omittedText.Clear();
            }

            private void StoreSpeechOverride(string paragraph, int start, int end, ref int omission)
            {
                StringBuilder spoken = null;
                int copied = start;
                while (omission < omittedText.Count && omittedText[omission].Start < end)
                {
                    var range = omittedText[omission];
                    if (range.Start + range.Length > copied)
                    {
                        if (spoken == null) spoken = new StringBuilder(end - start);
                        int from = Math.Max(copied, range.Start);
                        spoken.Append(paragraph, copied, from - copied);
                        copied = Math.Min(end, range.Start + range.Length);
                        if (from < copied && (paragraph[from] == ' ' || paragraph[copied - 1] == ' ')) spoken.Append(' ');
                    }
                    if (range.Start + range.Length > end) break;
                    omission++;
                }
                if (spoken == null) return;
                spoken.Append(paragraph, copied, end - copied);
                string speech = Clean(spoken.ToString());
                if (speech != paragraph.Substring(start, end - start).Trim())
                    SpeechOverrides.Add(Passages.Count, SpeechText.IsSpeakable(speech) ? speech : "");
            }

            private void AddNote(int passage, ReadingNote note)
            {
                List<ReadingNote> notes;
                if (!Notes.TryGetValue(passage, out notes)) Notes.Add(passage, notes = new List<ReadingNote>());
                if (!notes.Any(x => x.Marker == note.Marker && x.Text == note.Text)) notes.Add(note);
            }
        }

        // Match the display text's whitespace normalization without changing its split points.
        private sealed class NormalizedOffsets
        {
            private Match spaces;
            private int removed;
            private readonly int length;

            public NormalizedOffsets(string raw, int length)
            {
                spaces = Space.Match(raw);
                removed = spaces.Success && spaces.Index == 0 ? 1 : 0;
                this.length = length;
            }

            public int Map(int offset)
            {
                while (spaces.Success && spaces.Index + spaces.Length <= offset)
                {
                    removed += spaces.Length - 1;
                    spaces = spaces.NextMatch();
                }
                if (spaces.Success && spaces.Index < offset) offset = spaces.Index + 1;
                return Math.Max(0, Math.Min(length, offset - removed));
            }
        }
    }
}
