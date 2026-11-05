using System;
using System.Collections.Generic;
using ZeepTwitchChat.Chat;
using ZeepTwitchChat.Chat.Emotes;
using ZeepTwitchChat.Twitch;

namespace ZeepTwitchChat.LogicTests
{
    /// <summary>
    /// Names that need escaping, and the word lookup that finds third-party emotes in a
    /// message. Both are pure, so the "colon emote renders" case is checked here rather than
    /// by eye in game.
    /// </summary>
    internal static partial class Program
    {
        private static void NameChecks()
        {
            Console.WriteLine();
            Console.WriteLine("Emote names and word matching");

            // --- what may be matched in chat -----------------------------------------
            Check("colon name is accepted", TwitchEmoteTagParser.IsSafeEmoteName("nyxeon:3"));
            Check("punctuation is accepted", TwitchEmoteTagParser.IsSafeEmoteName("Kappa!"));
            Check("space is rejected", !TwitchEmoteTagParser.IsSafeEmoteName("two words"));
            Check("a heart emote is accepted", TwitchEmoteTagParser.IsSafeEmoteName("<3"));
            Check("a bare angle bracket is accepted", TwitchEmoteTagParser.IsSafeEmoteName("<"));
            Check("a name may contain markup characters",
                TwitchEmoteTagParser.IsSafeEmoteName("a<b>c"));
            Check("over-long name is rejected",
                !TwitchEmoteTagParser.IsSafeEmoteName(new string('a', 65)));

            // --- a native emote called <3 (Twitch's own) ------------------------------
            List<EmoteSpan> hearts = TwitchEmoteTagParser.Parse("89:0-1", "<3");
            Check("a native <3 span survives parsing",
                hearts.Count == 1 && hearts[0].Name == "<3" && hearts[0].Id == "89",
                hearts.Count == 1 ? hearts[0].ToString() : "dropped");

            string heartAlias = EmoteFrameNames.Alias("<3");
            Check("a heart gets a tag-safe alias",
                EmoteFrameNames.IsGlyphSafe(heartAlias) && heartAlias != "<3",
                heartAlias);
            Check("the heart's tag carries no bracket",
                !EmoteFrameNames.Tag(heartAlias, 0, 1).Contains("<3"));

            Check("a heart is matched as one word",
                EmoteWordScanner.Merge("i <3 it", null, w => w == "<3").Count == 1);

            // --- glyph aliases -------------------------------------------------------
            Check("a safe name keeps its own name", EmoteFrameNames.Alias("Kappa") == "Kappa");

            string colonAlias = EmoteFrameNames.Alias("nyxeon:3");
            Check("a colon name gets a tag-safe alias",
                colonAlias.Length == 9 && colonAlias[0] == 'e' && EmoteFrameNames.IsGlyphSafe(colonAlias),
                colonAlias);
            Check("the alias is stable", colonAlias == EmoteFrameNames.Alias("nyxeon:3"));
            Check("different names get different aliases",
                colonAlias != EmoteFrameNames.Alias("nyxeon:4"));
            Check("the frame tag carries no raw name",
                !EmoteFrameNames.Tag(colonAlias, 3, 5).Contains(":"));

            // --- word matching -------------------------------------------------------
            Func<string, bool> known = word =>
                word == "RainTime" || word == "nyxeon:3" || word == "Kappa";

            List<EmoteSpan> found = EmoteWordScanner.Merge("hi RainTime and nyxeon:3", null, known);
            Check("provider emotes are found by word", found.Count == 2, found.Count.ToString());
            Check("a colon emote is matched as one word",
                found.Count == 2 && found[1].Name == "nyxeon:3" &&
                found[1].Start == 16 && found[1].Length == 8 && found[1].Id == null,
                found.Count == 2 ? found[1].ToString() : "missing");
            Check("the first span keeps its offset",
                found.Count == 2 && found[0].Start == 3 && found[0].Length == 8);

            Check("trailing punctuation is not stripped",
                EmoteWordScanner.Merge("RainTime,", null, known).Count == 0);
            Check("matching is case sensitive",
                EmoteWordScanner.Merge("raintime", null, known).Count == 0);
            Check("every occurrence is found",
                EmoteWordScanner.Merge("Kappa Kappa", null, known).Count == 2);
            Check("tabs and newlines separate words",
                EmoteWordScanner.Merge("Kappa\tRainTime\nnyxeon:3", null, known).Count == 3);

            // A word at the very start and the very end still gets correct offsets.
            List<EmoteSpan> edges = EmoteWordScanner.Merge("Kappa", null, known);
            Check("a single-word message matches",
                edges.Count == 1 && edges[0].Start == 0 && edges[0].Length == 5);

            // --- interaction with native Twitch emotes --------------------------------
            List<EmoteSpan> native = new List<EmoteSpan> { new EmoteSpan(0, 8, "RainTime", "25") };
            List<EmoteSpan> merged = EmoteWordScanner.Merge("RainTime Kappa", native, known);
            Check("native spans are kept", merged.Count == 2 && merged[0].Id == "25");
            Check("a native span is not duplicated by the scan",
                merged.Count == 2 && merged[1].Name == "Kappa");

            List<EmoteSpan> later = new List<EmoteSpan> { new EmoteSpan(8, 5, "Kappa", "9") };
            List<EmoteSpan> sorted = EmoteWordScanner.Merge("RainTime Kappa", later, known);
            Check("spans come out in message order",
                sorted.Count == 2 && sorted[0].Start == 0 && sorted[1].Start == 8);

            Check("nothing to scan is harmless",
                EmoteWordScanner.Merge(null, null, known).Count == 0 &&
                EmoteWordScanner.Merge(string.Empty, null, known).Count == 0 &&
                EmoteWordScanner.Merge("RainTime", null, null).Count == 0);

            // --- the whole line, end to end ------------------------------------------
            ChatLineOptions options = new ChatLineOptions { LabelText = "T" };
            ChatLineFormatter formatter = new ChatLineFormatter(options, new AliasProvider(known));
            TwitchChatUser user = new TwitchChatUser("viewer", "Viewer", "#123456", new List<string>());

            string message = "look nyxeon:3 here";
            List<EmoteSpan> spans = EmoteWordScanner.Merge(message, null, known);
            string line = formatter.Format(user, message, spans);

            Check("the colon emote becomes a sprite",
                line.Contains(EmoteFrameNames.Tag(colonAlias, 0, 1)), line);
            Check("the raw name is gone from the line", !line.Contains("nyxeon:3"), line);
            Check("no raw colon survives inside the tag", !line.Contains("name=" + (char)34 + "nyxeon"), line);
            Check("the rest of the message is intact",
                line.EndsWith("look " + EmoteFrameNames.Tag(colonAlias, 0, 1) + " here"), line);
            ChatLineFormatter heartFormatter = new ChatLineFormatter(options, new AliasProvider(w => w == "<3"));
            string heartLine = heartFormatter.Format(user, "i <3 it",
                TwitchEmoteTagParser.Parse("89:2-3", "i <3 it"));
            Check("a heart renders as a sprite",
                heartLine.Contains(EmoteFrameNames.Tag(heartAlias, 0, 1)), heartLine);
            Check("the raw heart never reaches the line", !heartLine.Contains("<3"), heartLine);

            ChatLineFormatter plainHeart = new ChatLineFormatter(options, new AliasProvider(w => false));
            string unresolved = plainHeart.Format(user, "i <3 it",
                TwitchEmoteTagParser.Parse("89:2-3", "i <3 it"));
            Check("an unready heart is stripped, not injected",
                unresolved.EndsWith("i 3 it") && !unresolved.Contains("<3"), unresolved);

            Check("a message with no emote is untouched",
                formatter.Format(user, "plain words", null).EndsWith("plain words"));
        }

        /// <summary>Renders a set of emote names the way the real service does: alias tags.</summary>
        private sealed class AliasProvider : IEmoteTagProvider
        {
            private readonly Func<string, bool> known;

            public AliasProvider(Func<string, bool> known)
            {
                this.known = known;
            }

            public string GetSpriteTag(string emoteName)
            {
                if (emoteName == null || !known(emoteName))
                {
                    return null;
                }

                return EmoteFrameNames.Tag(EmoteFrameNames.Alias(emoteName), 0, 1);
            }
        }
    }
}
