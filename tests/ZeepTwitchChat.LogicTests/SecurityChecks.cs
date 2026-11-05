using System;
using System.Collections.Generic;
using ZeepTwitchChat.Chat;
using ZeepTwitchChat.Chat.Emotes;
using ZeepTwitchChat.Twitch;
using ZeepTwitchChat.Util;

namespace ZeepTwitchChat.LogicTests
{
    /// <summary>
    /// Security checks. The mod renders untrusted text into TextMeshPro rich text, so the
    /// central invariant is: the only markup in a finished line is markup this mod built
    /// itself. Everything here attacks that from the angles a chatter or a hostile config
    /// could: the message body, the display name, the label, the colour, and emote names
    /// coming from provider json or from the IRC emotes tag.
    /// </summary>
    internal static partial class Program
    {
        private static void SecurityChecks()
        {
            Console.WriteLine();
            Console.WriteLine("Security");

            ChatLineOptions options = new ChatLineOptions();
            ChatLineFormatter bare = new ChatLineFormatter(options);
            TwitchChatUser user = new TwitchChatUser("viewer", "Viewer", "#123456", new List<string>());

            // --- the central invariant ------------------------------------------------
            string[] attacks =
            {
                Sprite("Kappa"),
                "<sprite=" + (char)34 + "EmojiOne" + (char)34 + " name=" + (char)34 + "achievement 2" + (char)34 + ">",
                "</b></color><size=200>BIG</size>",
                "<link=" + (char)34 + "javascript:alert(1)" + (char)34 + ">click</link>",
                "<font=" + (char)34 + "Arial" + (char)34 + "><material=" + (char)34 + "x" + (char)34 + ">t",
                "<align=center><page><style=" + (char)34 + "X" + (char)34 + ">",
                "<color=red><#FF0000><mark=#ffffff><u><i><s>",
                "<size=999999>" + new string('A', 50) + "</size>",
                "<<<>>>",
                "<",
                (char)0x202E + "evil" + (char)0x202C,
                "tab" + (char)9 + "and" + (char)10 + "newline",
                "zero" + (char)0x200B + "width"
            };

            int hostileAccepted = 0;
            string firstBreak = null;

            for (int i = 0; i < attacks.Length; i++)
            {
                string line = bare.Format(user, attacks[i], null);
                string unexpected = FirstUnexpectedTag(line);
                if (unexpected != null)
                {
                    hostileAccepted++;
                    if (firstBreak == null)
                    {
                        firstBreak = unexpected + " from " + Describe(attacks[i]);
                    }
                }
            }

            Check("no hostile message produces unexpected markup",
                hostileAccepted == 0, firstBreak ?? (hostileAccepted + " case(s)"));

            // --- the display name and the label ---------------------------------------
            TwitchChatUser hostileUser = new TwitchChatUser("evil",
                "<b>Admin</b></color><size=99>",
                "#FFF;" + (char)34 + "><size=99>bad", new List<string>());
            string hostileLine = bare.Format(hostileUser, "hello", null);
            Check("a hostile display name cannot inject markup",
                FirstUnexpectedTag(hostileLine) == null, FirstUnexpectedTag(hostileLine));
            Check("a hostile colour falls back to a palette colour",
                FirstUnexpectedTag(hostileLine) == null && hostileLine.Contains("<color=#"));

            ChatLineOptions hostileOptions = new ChatLineOptions
            {
                LabelText = "T</b><size=99>",
                LabelColourHex = "#FFF" + (char)34 + "><size=99"
            };
            string labelled = new ChatLineFormatter(hostileOptions).Format(user, "hi", null);
            Check("a hostile label and label colour cannot inject markup",
                FirstUnexpectedTag(labelled) == null, FirstUnexpectedTag(labelled));

            // --- hostile emote names ---------------------------------------------------
            // A provider can call an emote anything; the tag must only ever carry an alias.
            string[] hostileNames =
            {
                Sprite("Kappa"),
                "x" + (char)34 + "><size=200>y",
                "<b>bold</b>",
                "</color>,3"
            };

            for (int i = 0; i < hostileNames.Length; i++)
            {
                string alias = EmoteFrameNames.Alias(hostileNames[i]);
                string tag = EmoteFrameNames.Tag(alias, 0, 1);
                Check("hostile emote name " + i + " yields a safe alias",
                    EmoteFrameNames.IsGlyphSafe(alias) && FirstUnexpectedTag(tag) == null, tag);
            }

            // --- the emote tag itself --------------------------------------------------
            Check("sprite tags only ever carry glyph-safe names",
                IsAllowedTag(EmoteFrameNames.Tag("Kappa~3", 0, 4)));

            // --- urls -------------------------------------------------------------------
            Check("twitch image url accepted",
                EmoteUrlPolicy.IsAllowed("https://static-cdn.jtvnw.net/emoticons/v2/25/default/dark/3.0",
                    EmoteUrlKind.Image));
            Check("bttv image url accepted",
                EmoteUrlPolicy.IsAllowed("https://cdn.betterttv.net/emote/abc/3x", EmoteUrlKind.Image));
            Check("ffz image url accepted",
                EmoteUrlPolicy.IsAllowed("https://cdn.frankerfacez.com/emoticon/9/4", EmoteUrlKind.Image));
            Check("7tv image url accepted",
                EmoteUrlPolicy.IsAllowed("https://cdn.7tv.app/emote/01FCY/2x.gif", EmoteUrlKind.Image));
            Check("7tv protocol relative host accepted",
                EmoteUrlPolicy.IsAllowed("//cdn.7tv.app/emote/01FCY/2x.gif", EmoteUrlKind.Image));
            Check("provider api url accepted",
                EmoteUrlPolicy.IsAllowed("https://api.betterttv.net/3/cached/emotes/global", EmoteUrlKind.Api));

            string[] badUrls =
            {
                "file:///etc/passwd",
                "file://C:/Windows/System32/drivers/etc/hosts",
                "http://cdn.betterttv.net/emote/abc/3x",
                "ftp://cdn.betterttv.net/x",
                "https://evil.example.com/emote/abc",
                "https://cdn.betterttv.net@evil.example.com/x",
                "https://cdn.betterttv.net/../../etc/passwd",
                "https://cdn.betterttv.net/emote/../../secret",
                "javascript:alert(1)",
                "data:text/plain;base64,AAAA",
                "https://cdn.betterttv.net/emote/abc#frag",
                "//evil.example.com/x",
                string.Empty,
                "https://cdn.betterttv.net/" + new string('a', 600)
            };

            int badAccepted = 0;
            string firstBad = null;

            for (int i = 0; i < badUrls.Length; i++)
            {
                // An image url must be refused as an image, and a provider api url too.
                if (EmoteUrlPolicy.IsAllowed(badUrls[i], EmoteUrlKind.Image) ||
                    EmoteUrlPolicy.IsAllowed(badUrls[i], EmoteUrlKind.Api))
                {
                    badAccepted++;
                    if (firstBad == null)
                    {
                        firstBad = badUrls[i];
                    }
                }
            }

            Check("dangerous urls are refused", badAccepted == 0, firstBad ?? (badAccepted + " accepted"));
            Check("an api host is not accepted as an image host",
                !EmoteUrlPolicy.IsAllowed("https://api.betterttv.net/x", EmoteUrlKind.Image));
            Check("an image host is not accepted as an api host",
                !EmoteUrlPolicy.IsAllowed("https://cdn.betterttv.net/x", EmoteUrlKind.Api));

            // --- emote ids from the wire -----------------------------------------------
            Check("numeric twitch emote id accepted", EmoteUrlPolicy.IsSafeId("25"));
            Check("path-like emote id refused", !EmoteUrlPolicy.IsSafeId("25/../x"));
            Check("quote in emote id refused", !EmoteUrlPolicy.IsSafeId("2" + (char)34));
            Check("over-long emote id refused", !EmoteUrlPolicy.IsSafeId(new string('1', 33)));
            Check("empty emote id refused", !EmoteUrlPolicy.IsSafeId(string.Empty));

            // --- name spoofing ---------------------------------------------------------
            string spoofed = bare.Format(user, "a" + (char)0x202E + "b" + (char)0x200B + "c", null);
            Check("bidi overrides and zero width characters are removed",
                spoofed.EndsWith(": abc"), Describe(spoofed));

            TwitchChatUser spoofName = new TwitchChatUser("x",
                (char)0x202E + "enim" + (char)0x202C + "Admin", "#123456", new List<string>());
            string spoofedLine = bare.Format(spoofName, "hi", null);
            Check("a name cannot be reordered with bidi controls",
                !spoofedLine.Contains((char)0x202E) && !spoofedLine.Contains((char)0x202C), spoofedLine);

            Check("the sanitiser keeps ordinary text intact",
                ChatLineFormatter.SanitiseText("  hello   world  ", 40) == "hello world",
                ChatLineFormatter.SanitiseText("  hello   world  ", 40));
            Check("the sanitiser truncates and marks it",
                ChatLineFormatter.SanitiseText(new string('a', 100), 20).EndsWith("..."));

            // --- decoder limits --------------------------------------------------------
            Check("a gif with absurd dimensions is refused",
                !GifDecoder.TryDecode(OversizedGif(), 8, out _));
            Check("the frame cap is clamped internally",
                GifDecoder.TryDecode(Convert.FromBase64String(TinyGifBase64), int.MaxValue, out List<GifDecoder.GifFrame> all) &&
                all.Count == 2);
        }

        /// <summary>A GIF header claiming 5000x5000 pixels; the decoder must refuse it.</summary>
        private static byte[] OversizedGif()
        {
            List<byte> bytes = new List<byte>();
            bytes.AddRange(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' });
            bytes.AddRange(new byte[] { 0x88, 0x13, 0x88, 0x13 }); // 5000 x 5000, little endian
            bytes.Add(0x00);
            bytes.Add(0x00);
            bytes.Add(0x00);
            bytes.Add(0x3B);
            return bytes.ToArray();
        }

        /// <summary>
        /// Returns the first tag in the line that this mod could not have produced, or null.
        /// The allow list is deliberately exact: our label, our badges, the chat colour and a
        /// glyph-safe sprite tag. Anything else in a line is a bug worth failing on.
        /// </summary>
        private static string FirstUnexpectedTag(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return null;
            }

            int index = 0;
            while (index < line.Length)
            {
                int open = line.IndexOf('<', index);
                if (open < 0)
                {
                    return null;
                }

                int close = line.IndexOf('>', open);
                if (close < 0)
                {
                    return line.Substring(open);
                }

                string tag = line.Substring(open, close - open + 1);
                if (!IsAllowedTag(tag))
                {
                    return tag;
                }

                index = close + 1;
            }

            return null;
        }

        /// <summary>True for the fixed set of tags the formatter and atlas emit.</summary>
        private static bool IsAllowedTag(string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag[0] != '<' || tag[tag.Length - 1] != '>')
            {
                return false;
            }

            if (tag == "<b>" || tag == "</b>" || tag == "</color>")
            {
                return true;
            }

            // <color=#RRGGBB> is exactly fifteen characters: prefix, six hex digits, bracket.
            if (tag.StartsWith("<color=#", StringComparison.Ordinal) && tag.Length == 15)
            {
                for (int i = 8; i < 14; i++)
                {
                    char c = tag[i];
                    bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                    if (!hex)
                    {
                        return false;
                    }
                }

                return true;
            }

            string prefix = "<sprite name=" + (char)34;
            if (tag.StartsWith(prefix, StringComparison.Ordinal) && tag.EndsWith((char)34 + ">", StringComparison.Ordinal))
            {
                string glyph = tag.Substring(prefix.Length, tag.Length - prefix.Length - 2);
                return EmoteFrameNames.IsGlyphSafe(glyph) || IsFrameGlyph(glyph);
            }

            return false;
        }

        /// <summary>Frame glyphs are <c>alias~N</c>, where the alias is glyph safe.</summary>
        private static bool IsFrameGlyph(string glyph)
        {
            int tilde = glyph.LastIndexOf('~');
            if (tilde <= 0 || tilde == glyph.Length - 1)
            {
                return false;
            }

            for (int i = tilde + 1; i < glyph.Length; i++)
            {
                if (glyph[i] < '0' || glyph[i] > '9')
                {
                    return false;
                }
            }

            return EmoteFrameNames.IsGlyphSafe(glyph.Substring(0, tilde));
        }

        private static string Describe(string value)
        {
            return value == null ? "null" : value.Replace((char)10, ' ').Replace((char)13, ' ');
        }
    }
}
