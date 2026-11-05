using System;
using System.Collections.Generic;
using ZeepTwitchChat.Twitch;

namespace ZeepTwitchChat.Chat.Emotes
{
    /// <summary>
    /// Finds third-party emotes in a message by looking up its words.
    ///
    /// Twitch's IRC <c>emotes</c> tag only lists native Twitch emotes, so without this pass a
    /// BTTV/7TV/FFZ emote is just a word. Tokens are split on whitespace only, so names that
    /// contain punctuation (a colon, brackets, whatever the provider allows) still match as a
    /// whole word; matching is exact and case sensitive, which is how those providers behave.
    ///
    /// Pure logic, no Unity: unit tested.
    /// </summary>
    public static class EmoteWordScanner
    {
        /// <summary>
        /// Returns <paramref name="existing"/> plus a span for every whitespace-delimited word
        /// that <paramref name="isKnownEmote"/> accepts. Spans that would overlap an existing
        /// one are skipped, so a native Twitch emote always wins and the result stays ordered.
        /// </summary>
        public static List<EmoteSpan> Merge(
            string message,
            IReadOnlyList<EmoteSpan> existing,
            Func<string, bool> isKnownEmote)
        {
            List<EmoteSpan> spans = new List<EmoteSpan>();

            if (existing != null)
            {
                for (int i = 0; i < existing.Count; i++)
                {
                    spans.Add(existing[i]);
                }
            }

            if (string.IsNullOrEmpty(message) || isKnownEmote == null || message.Length > 4000)
            {
                spans.Sort(CompareByStart);
                return spans;
            }

            int index = 0;
            while (index < message.Length)
            {
                while (index < message.Length && IsSeparator(message[index]))
                {
                    index++;
                }

                int start = index;
                while (index < message.Length && !IsSeparator(message[index]))
                {
                    index++;
                }

                int length = index - start;
                if (length == 0 || length > 64)
                {
                    continue;
                }

                string word = message.Substring(start, length);

                if (Overlaps(spans, start, start + length) || !isKnownEmote(word))
                {
                    continue;
                }

                spans.Add(new EmoteSpan(start, length, word, null));
            }

            spans.Sort(CompareByStart);
            return spans;
        }

        private static int CompareByStart(EmoteSpan a, EmoteSpan b)
        {
            return a.Start.CompareTo(b.Start);
        }

        /// <summary>Whitespace and control characters only: punctuation belongs to the name.</summary>
        private static bool IsSeparator(char c)
        {
            return char.IsWhiteSpace(c) || c < ' ';
        }

        private static bool Overlaps(List<EmoteSpan> spans, int start, int end)
        {
            for (int i = 0; i < spans.Count; i++)
            {
                if (start < spans[i].End && spans[i].Start < end)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
