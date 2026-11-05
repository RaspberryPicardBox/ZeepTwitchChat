using System;
using System.Text;
using ZeepTwitchChat.Twitch;

namespace ZeepTwitchChat.Chat
{
    /// <summary>How the in-game chat line is decorated.</summary>
    public sealed class ChatLineOptions
    {
        /// <summary>Show the <c>[TWITCH]</c> style label so Twitch lines are obvious at a glance.</summary>
        public bool ShowLabel = true;

        public string LabelText = "TWITCH";

        /// <summary>Label colour; Twitch purple by default.</summary>
        public string LabelColourHex = "#9147FF";

        /// <summary>Use the colour the chatter picked in Twitch when they have one.</summary>
        public bool UseTwitchColours = true;

        /// <summary>Prefix moderator/subscriber/vip/broadcaster with a short tag.</summary>
        public bool ShowBadges = true;

        /// <summary>Longest message body before it is truncated, in characters.</summary>
        public int MaxMessageLength = 180;
    }

    /// <summary>
    /// Turns a Twitch message into the exact TextMeshPro rich-text line the game's own
    /// chat uses (verified against <c>OnlineChatUI.GetChatMessage</c>, which builds
    /// <c>&lt;#RRGGBB&gt;&lt;b&gt;name&lt;/b&gt;&lt;/color&gt;: text</c>).
    ///
    /// Every piece of untrusted text is sanitised first: angle brackets are removed, so a
    /// chatter cannot inject TMP tags (<c>&lt;size=200&gt;</c>, <c>&lt;sprite=...&gt;</c>,
    /// <c>&lt;link=...&gt;</c>) into the game's chat rendering.
    ///
    /// Pure logic: no Unity, no BepInEx, unit tested by the test harness.
    /// </summary>
    public sealed class ChatLineFormatter
    {
        private static readonly string[] Palette =
        {
            "#FF6B6B", "#FFD93D", "#6BCB77", "#4D96FF", "#B983FF",
            "#FF9F45", "#00C2A8", "#F473B9", "#8AC4FF", "#E0A458"
        };

        private readonly ChatLineOptions options;

        public ChatLineFormatter(ChatLineOptions options)
        {
            this.options = options ?? new ChatLineOptions();
        }

        /// <summary>Builds the rich-text chat line for one message.</summary>
        public string Format(TwitchChatUser user, string message)
        {
            if (user == null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            StringBuilder builder = new StringBuilder(160);

            if (options.ShowLabel && !string.IsNullOrEmpty(options.LabelText))
            {
                builder.Append("<color=")
                       .Append(NormaliseColour(options.LabelColourHex, "#9147FF"))
                       .Append("><b>[")
                       .Append(SanitiseText(options.LabelText, 16))
                       .Append("]</b></color> ");
            }

            if (options.ShowBadges)
            {
                AppendBadges(builder, user);
            }

            builder.Append("<color=")
                   .Append(ResolveColour(user, options.UseTwitchColours))
                   .Append("><b>")
                   .Append(SanitiseText(user.DisplayName, 32))
                   .Append("</b></color>: ")
                   .Append(SanitiseMessage(message, options.MaxMessageLength));

            return builder.ToString();
        }

        private static void AppendBadges(StringBuilder builder, TwitchChatUser user)
        {
            if (user.HasBadge("broadcaster"))
            {
                AppendBadge(builder, "HOST", "#E91916");
            }

            if (user.HasBadge("moderator"))
            {
                AppendBadge(builder, "MOD", "#00AD03");
            }

            if (user.HasBadge("vip"))
            {
                AppendBadge(builder, "VIP", "#E005B9");
            }

            if (user.HasBadge("founder") || user.HasBadge("subscriber"))
            {
                AppendBadge(builder, "SUB", "#9147FF");
            }
        }

        private static void AppendBadge(StringBuilder builder, string text, string colour)
        {
            builder.Append("<color=").Append(colour).Append("><b>[").Append(text).Append("]</b></color> ");
        }

        /// <summary>
        /// Colour for the chatter: their Twitch colour when present and valid, otherwise a
        /// stable colour derived from the login so the same person is always the same colour.
        /// </summary>
        public static string ResolveColour(TwitchChatUser user, bool useTwitchColours)
        {
            if (useTwitchColours && IsValidHexColour(user.ColourHex))
            {
                return NormaliseColour(user.ColourHex, Palette[0]);
            }

            uint hash = Fnv1A(user.Login ?? string.Empty);
            return Palette[(int)(hash % (uint)Palette.Length)];
        }

        /// <summary>True for <c>#RRGGBB</c> or <c>RRGGBB</c>; Twitch sends an empty value when unset.</summary>
        public static bool IsValidHexColour(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            string hex = value.Trim();
            if (hex.Length > 0 && hex[0] == '#')
            {
                hex = hex.Substring(1);
            }

            if (hex.Length != 6)
            {
                return false;
            }

            for (int i = 0; i < hex.Length; i++)
            {
                char c = hex[i];
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }

        private static string NormaliseColour(string value, string fallback)
        {
            if (!IsValidHexColour(value))
            {
                return fallback;
            }

            string hex = value.Trim();
            if (hex[0] != '#')
            {
                hex = "#" + hex;
            }

            return hex.ToUpperInvariant();
        }

        /// <summary>
        /// Removes everything that could be interpreted as rich text or break the layout,
        /// collapses whitespace and truncates to <paramref name="maxLength"/> characters.
        /// </summary>
        public static string SanitiseMessage(string text, int maxLength)
        {
            return SanitiseText(text, maxLength);
        }

        /// <summary>Sanitises any untrusted fragment (message body, display name, label).</summary>
        public static string SanitiseText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(text.Length);
            bool lastWasSpace = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                // Newlines and tabs become a single space so words stay separated; every
                // other control character is dropped. Angle brackets are removed outright so
                // the chatter cannot inject TextMeshPro tags.
                if (c == '<' || c == '>')
                {
                    continue;
                }

                if (c < ' ')
                {
                    if (c == '\r' || c == '\n' || c == '\t')
                    {
                        if (lastWasSpace)
                        {
                            continue;
                        }

                        lastWasSpace = true;
                        builder.Append(' ');
                    }

                    continue;
                }

                if (c == ' ')
                {
                    if (lastWasSpace)
                    {
                        continue;
                    }

                    lastWasSpace = true;
                    builder.Append(' ');
                    continue;
                }

                lastWasSpace = false;
                builder.Append(c);
            }

            string cleaned = builder.ToString().Trim();

            if (maxLength > 0 && cleaned.Length > maxLength)
            {
                cleaned = cleaned.Substring(0, Math.Max(0, maxLength - 3)).TrimEnd() + "...";
            }

            return cleaned;
        }

        private static uint Fnv1A(string value)
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;

            uint hash = offsetBasis;
            for (int i = 0; i < value.Length; i++)
            {
                char c = char.ToLowerInvariant(value[i]);
                hash ^= (byte)(c & 0xFF);
                hash *= prime;
                hash ^= (byte)(c >> 8);
                hash *= prime;
            }

            return hash == 0 ? 1 : hash;
        }
    }
}
