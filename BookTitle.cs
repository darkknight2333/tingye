using System;
using System.Text.RegularExpressions;

namespace Tingye
{
    internal static class BookTitle
    {
        private static readonly Regex CatalogNumber = new Regex(@"^\d+\s*[_＿]+\s*");
        private static readonly Regex TrailingNote = new Regex(
            @"(?:[（(]([^（）()]*)[）)]|[【［\[]([^【】［］\[\]]*)[】］\]])\s*$");
        private static readonly Regex EditionNote = new Regex(
            @"全\s*[\d一二三四五六七八九十]+\s*[册卷]|套装|三部曲|系列|丛书|经典|文库|译本|直译|译注|无删节|修订|典藏|增订|出版|精装|插图|新版|纪念版|电子版|完整版|编辑推荐|代表作|z-library|z-lib|1lib",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex BundleContents = new Regex(
            @"[（(]\s*(?:全|共)\s*[\d一二三四五六七八九十]+\s*册\s*[:：]\s*([^（）()]*[+＋][^（）()]*)[）)]\s*$");

        public static string Primary(string original, string author)
        {
            if (String.IsNullOrWhiteSpace(original)) return original;
            string title = CatalogNumber.Replace(original.Trim(), "");
            author = (author ?? "").Trim();
            if (author.Length != 0 && title.EndsWith(author, StringComparison.OrdinalIgnoreCase))
            {
                string prefix = title.Substring(0, title.Length - author.Length).TrimEnd();
                if (prefix.Length > 1 && "_＿—–-|｜".IndexOf(prefix[prefix.Length - 1]) >= 0)
                    title = TrimSeparator(prefix);
            }

            var bundle = BundleContents.Match(title);
            if (bundle.Success && bundle.Index > 0) return bundle.Groups[1].Value.Trim();

            for (var note = TrailingNote.Match(title); note.Success && note.Index > 0; note = TrailingNote.Match(title))
            {
                string content = note.Groups[1].Success ? note.Groups[1].Value : note.Groups[2].Value;
                if (!EditionNote.IsMatch(content) && !content.Trim().Equals(author, StringComparison.OrdinalIgnoreCase))
                    break;
                string prefix = TrimSeparator(title.Substring(0, note.Index));
                if (prefix.Length == 0) break;
                title = prefix;
            }

            int subtitle = SubtitleStart(title);
            if (subtitle > 0) title = title.Substring(0, subtitle).TrimEnd();
            return title.Length == 0 ? original : title;
        }

        private static string TrimSeparator(string title)
        {
            return title.TrimEnd().TrimEnd('_', '＿', '—', '–', '-', '|', '｜').TrimEnd();
        }

        private static int SubtitleStart(string title)
        {
            int nesting = 0;
            for (int i = 0; i < title.Length; i++)
            {
                char current = title[i];
                if ("（([［【《".IndexOf(current) >= 0) nesting++;
                else if ("）)]］】》".IndexOf(current) >= 0 && nesting != 0) nesting--;
                else if (nesting == 0 && current == '—' && i > 0 && i + 1 < title.Length && title[i + 1] == '—')
                    return i;
                else if (nesting == 0 && (current == ':' || current == '：') && i > 0 && i + 1 < title.Length)
                {
                    if (title[i + 1] == '/' || (Char.IsDigit(title[i - 1]) && Char.IsDigit(title[i + 1]))) continue;
                    return i;
                }
            }
            return -1;
        }
    }
}
