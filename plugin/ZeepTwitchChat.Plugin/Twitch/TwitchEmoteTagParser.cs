using System;
using System.Collections.Generic;

namespace ZeepTwitchChat.Twitch
{
    /// <summary>
    /// One emote occurrence inside a message: where it is, what it is called, and (for
    /// native Twitch emotes) its id, which is all that is needed to build the CDN url.
    ///
    /// Pure data: no Unity, no BepInEx.
    /// </summary>
    public struct EmoteSpan
    {
        public EmoteSpan(int start, int length, string name, string id)
        {
            Start = start;
            Length = length;
            Name = name;
            Id = id;
        }

        /// <summary>Zero-based character offset of the emote inside the message.</summary>
        public int Start { get; }

        /// <summary>Length of the emote name in characters.</summary>
        public int Length { get; }

        /// <summary>Emote name as written in chat, e.g. <c>Kappa</c>.</summary>
        public string Name { get; }

        /// <summary>Provider id (Twitch emote id, BTTV/7TV/FFZ id), or null for name-only lookups.</summary>
        public string Id { get; }

        public int End => Start + Length;

        public override string ToString()
        {
            return Name + "@" + Start + "+" + Length;
        }
    }

    /// <summary>
    /// Parses the IRCv3 <c>emotes</c> tag Twitch sends with every PRIVMSG, which lists
    /// every native emote in the message as <c>id:start-end,start-end/id:start-end</c>.
    /// </summary>
    public static class TwitchEmoteTagParser
    {
        /// <summary>
        /// Returns the emote spans for a message. Invalid, overlapping or out-of-range
        /// entries are dropped rather than trusted, because the tag comes from the wire.
        /// </summary>
        public static List<EmoteSpan> Parse(string emotesTag, string message)
        {
            List<EmoteSpan> spans = new List<EmoteSpan>();

            if (string.IsNullOrEmpty(emotesTag) || string.IsNullOrEmpty(message))
            {
                return spans;
            }

            string[] groups = emotesTag.Split('/');
            for (int g = 0; g < groups.Length; g++)
            {
                string group = groups[g];
                int colon = group.IndexOf(':');
                if (colon <= 0 || colon == group.Length - 1)
                {
                    continue;
                }

                string id = group.Substring(0, colon);
                string[] ranges = group.Substring(colon + 1).Split(',');

                for (int r = 0; r < ranges.Length; r++)
                {
                    int dash = ranges[r].IndexOf('-');
                    if (dash <= 0)
                    {
                        continue;
                    }

                    if (!int.TryParse(ranges[r].Substring(0, dash), out int start) ||
                        !int.TryParse(ranges[r].Substring(dash + 1), out int end))
                    {
                        continue;
                    }

                    int length = end - start + 1;
                    if (start < 0 || length <= 0 || start + length > message.Length)
                    {
                        continue;
                    }

                    EmoteSpan span = new EmoteSpan(start, length, message.Substring(start, length), id);
                    if (Overlaps(spans, span))
                    {
                        continue;
                    }

                    spans.Add(span);
                }
            }

            TrimEmoteNames(spans);
            spans.Sort((a, b) => a.Start.CompareTo(b.Start));
            return spans;
        }

        private static bool Overlaps(List<EmoteSpan> spans, EmoteSpan candidate)
        {
            for (int i = 0; i < spans.Count; i++)
            {
                if (candidate.Start < spans[i].End && spans[i].Start < candidate.End)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Emote names are used inside a markup tag, so keep them to safe characters.</summary>
        private static void TrimEmoteNames(List<EmoteSpan> spans)
        {
            for (int i = spans.Count - 1; i >= 0; i--)
            {
                if (!IsSafeEmoteName(spans[i].Name))
                {
                    spans.RemoveAt(i);
                }
            }
        }

        /// <summary>True when the name can be embedded in a sprite tag without escaping.</summary>
        public static bool IsSafeEmoteName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64)
            {
                return false;
            }

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                            (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!safe)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
