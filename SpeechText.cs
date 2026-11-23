using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Tingye
{
    internal static class SpeechText
    {
        private const string Closers = "”’」』）》】〕〉］〗\"')]}";
        private const string SentenceMarks = "。！？!?．.…";
        private const string ClauseMarks = "，、；;,：:";
        private static readonly Regex HanSpacing = new Regex(@"(?<=[\u3400-\u9fff])\s+(?=[\u3400-\u9fff，。！？、；：])", RegexOptions.Compiled);
        private static readonly Regex Abbreviation = new Regex(@"(?:\b(?:Mr|Mrs|Ms|Dr|Prof|St|vs|etc)\.|\b[A-Za-z]\.)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool HasHan(string text)
        {
            foreach (char c in text)
                if (c >= '\u3400' && c <= '\u9fff') return true;
            return false;
        }

        public static List<string> Split(string text, bool longer = false, bool quickStart = false)
        {
            text = text.Replace("\u00ad", "").Replace("\u200b", "").Replace("\ufeff", "").Trim();
            bool isChinese = HasHan(text);
            if (isChinese) text = HanSpacing.Replace(text, "");
            int target = longer ? (isChinese ? 180 : 280) : (isChinese ? 72 : 145);
            int limit = longer ? (isChinese ? 260 : 400) : (isChinese ? 100 : 200);
            var pieces = new List<string>();
            for (int start = 0; start < text.Length;)
            {
                int end = text.Length - start <= limit ? text.Length : FindBreak(text, start, target, limit);
                if (quickStart && pieces.Count == 0)
                {
                    // Shorten the first request only at a natural boundary. Keep long words and quotations intact.
                    int minimum = isChinese ? 8 : 20;
                    int maximum = Math.Min(end - 1, start + (isChinese ? 36 : 80));
                    int clause = 0;
                    for (int i = start + minimum; i < maximum; i++)
                    {
                        if (IsSentenceEnd(text, i))
                        {
                            int boundary = i + 1;
                            while (boundary < end && (SentenceMarks.IndexOf(text[boundary]) >= 0 ||
                                Closers.IndexOf(text[boundary]) >= 0)) boundary++;
                            if (end - boundary >= minimum) end = boundary;
                            clause = 0;
                            break;
                        }
                        if (clause == 0 && "，；;,".IndexOf(text[i]) >= 0 &&
                            !(Char.IsDigit(text[i - 1]) && Char.IsDigit(text[i + 1])) &&
                            end - i - 1 >= minimum) clause = i + 1;
                    }
                    if (clause != 0) end = clause;
                }
                string piece = text.Substring(start, end - start).Trim();
                if (IsSpeakable(piece)) pieces.Add(piece);
                start = end;
            }
            return pieces;
        }

        public static string ForSynthesis(string text, bool paragraphEnd)
        {
            char last = LastBeforeClosers(text);
            if (SentenceMarks.IndexOf(last) >= 0 || ClauseMarks.IndexOf(last) >= 0) return text;
            bool chinese = HasHan(text);
            if (paragraphEnd) return text + (chinese ? "。" : ".");
            return text + (chinese ? "，" : ",");
        }

        public static int TailPauseMs(string text, bool paragraphEnd)
        {
            if (paragraphEnd) return 140;
            return SentenceMarks.IndexOf(LastBeforeClosers(text)) >= 0 ? 110 : 80;
        }

        private static char LastBeforeClosers(string text)
        {
            for (int i = text.Length - 1; i >= 0; i--)
            {
                char c = text[i];
                if (Closers.IndexOf(c) < 0 && !Char.IsWhiteSpace(c)) return c;
            }
            return '\0';
        }

        private static int FindBreak(string text, int start, int target, int limit)
        {
            int sentence = 0, clause = 0, word = 0;
            int maximum = Math.Min(start + limit, text.Length - target / 5);
            for (int i = start + target / 2; i < maximum; i++)
            {
                char c = text[i];
                if (IsSentenceEnd(text, i))
                {
                    int end = i + 1;
                    while (end < text.Length && ("。！？!?…".IndexOf(text[end]) >= 0 || Closers.IndexOf(text[end]) >= 0)) end++;
                    if (end > maximum) break;
                    sentence = end;
                    if (sentence >= start + target) break;
                    i = end - 1;
                }
                else if (ClauseMarks.IndexOf(c) >= 0 &&
                    !(Char.IsDigit(text[i - 1]) && Char.IsDigit(text[i + 1])))
                {
                    clause = i + 1;
                }
                else if (Char.IsWhiteSpace(c)) word = i + 1;
            }
            if (sentence != 0) return sentence;
            if (clause != 0) return clause;
            if (word != 0) return word;
            while (maximum > start + 1 && (Char.IsLowSurrogate(text[maximum]) ||
                CharUnicodeInfo.GetUnicodeCategory(text, maximum) == UnicodeCategory.NonSpacingMark ||
                SentenceMarks.IndexOf(text[maximum]) >= 0 || ClauseMarks.IndexOf(text[maximum]) >= 0 ||
                Closers.IndexOf(text[maximum]) >= 0)) maximum--;
            return maximum;
        }

        private static bool IsSentenceEnd(string text, int index)
        {
            char c = text[index];
            if ("。！？!?…".IndexOf(c) >= 0) return true;
            if (c != '.') return false;
            char next = text[index + 1];
            if (!Char.IsWhiteSpace(next) && Closers.IndexOf(next) < 0) return false;
            return !Abbreviation.IsMatch(text.Substring(Math.Max(0, index - 5), Math.Min(index + 1, 6)));
        }

        public static bool IsSpeakable(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (Char.IsLetterOrDigit(text, i)) return true;
            return false;
        }
    }
}
