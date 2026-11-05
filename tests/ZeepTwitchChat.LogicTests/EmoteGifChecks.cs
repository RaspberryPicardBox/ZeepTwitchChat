using System;
using System.Collections.Generic;
using System.Text;
using ZeepTwitchChat.Chat.Emotes;
using ZeepTwitchChat.Twitch;
using ZeepTwitchChat.Util;

namespace ZeepTwitchChat.LogicTests
{
    /// <summary>
    /// Checks for the GIF decoder and the animation frame naming. Both are pure, so a real
    /// decode can be verified here rather than by eye in game.
    /// </summary>
    internal static partial class Program
    {
        /// <summary>
        /// A 121 byte, 4x4, two frame GIF: frame 0 is one opaque red column and the rest
        /// transparent, frame 1 is solid blue. Generated once and pasted in base64 so the
        /// checks stay offline and deterministic.
        /// </summary>
        private const string TinyGifBase64 =
            "R0lGODlhBAAEAIEAAAAAAP8AAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQJBQAAACwAAAAABAAE" +
            "AAAIDQADABgokOBAAAUBBAQAIfkECAoAAAAsAAAAAAQABACBAAD/AAAAAAAAAAAACAkAAQgcSLAggIAAOw==";

        private static void GifChecks()
        {
            Console.WriteLine();
            Console.WriteLine("GIF decoding");

            byte[] gif = Convert.FromBase64String(TinyGifBase64);

            Check("gif signature recognised", GifDecoder.LooksLikeGif(gif));
            Check("png data is not taken for a gif",
                !GifDecoder.LooksLikeGif(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
            Check("null and short data rejected",
                !GifDecoder.LooksLikeGif(null) && !GifDecoder.LooksLikeGif(new byte[3]));

            List<GifDecoder.GifFrame> frames;
            Check("gif decodes", GifDecoder.TryDecode(gif, 8, out frames));
            Check("both frames decoded", frames.Count == 2, frames.Count.ToString());
            Check("canvas size", frames.Count == 2 && frames[0].Width == 4 && frames[0].Height == 4);
            Check("frame delays in milliseconds",
                frames.Count == 2 && frames[0].DelayMilliseconds == 50 && frames[1].DelayMilliseconds == 100,
                frames.Count == 2 ? frames[0].DelayMilliseconds + "/" + frames[1].DelayMilliseconds : "n/a");

            if (frames.Count == 2)
            {
                // Rows come back bottom-up (Unity texture order), so the top-left pixel of a
                // 4x4 canvas is the last row.
                Check("frame 0 draws its opaque column",
                    SameColour(frames[0].Rgba, 4, 0, 0, 255, 0, 0, 255),
                    Describe(frames[0].Rgba, 4, 0, 0));
                Check("frame 0 leaves the rest transparent",
                    SameColour(frames[0].Rgba, 4, 2, 2, 0, 0, 0, 0),
                    Describe(frames[0].Rgba, 4, 2, 2));
                Check("frame 1 replaces the canvas",
                    SameColour(frames[1].Rgba, 4, 0, 0, 0, 0, 255, 255) &&
                    SameColour(frames[1].Rgba, 4, 2, 2, 0, 0, 255, 255));
            }

            Check("frame cap respected",
                GifDecoder.TryDecode(gif, 1, out List<GifDecoder.GifFrame> capped) && capped.Count == 1);

            Check("garbage rejected",
                !GifDecoder.TryDecode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 }, 8, out _));
            Check("truncated gif rejected or partial, never throws",
                !GifDecoder.TryDecode(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, 8, out _));
            Check("png bytes are not decoded as a gif",
                !GifDecoder.TryDecode(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, 0x00, 0x00, 0, 0, 0, 0, 0 }, 8, out _));
        }

        private static void EmoteFrameChecks()
        {
            Console.WriteLine();
            Console.WriteLine("Emote animation frames");

            Check("frame 0 keeps the plain glyph name", EmoteFrameNames.Glyph("Kappa", 0) == "Kappa");
            Check("later frames get a suffix",
                EmoteFrameNames.Glyph("Kappa", 2) == "Kappa~2", EmoteFrameNames.Glyph("Kappa", 2));
            Check("negative frame falls back to frame 0", EmoteFrameNames.Glyph("Kappa", -3) == "Kappa");

            Check("frame 0 tag is the plain emote tag",
                EmoteFrameNames.Tag("Kappa", 0, 3) == "<sprite name=\"Kappa\">",
                EmoteFrameNames.Tag("Kappa", 0, 3));
            Check("frame tag names the frame glyph",
                EmoteFrameNames.Tag("Kappa", 1, 3) == "<sprite name=\"Kappa~1\">",
                EmoteFrameNames.Tag("Kappa", 1, 3));
            Check("frame index wraps",
                EmoteFrameNames.Tag("Kappa", 3, 3) == "<sprite name=\"Kappa\">");
            Check("still emote never gets a frame tag",
                EmoteFrameNames.Tag("Kappa", 4, 1) == "<sprite name=\"Kappa\">");
            Check("empty name yields no tag", EmoteFrameNames.Tag(null, 0, 3) == null);

            Check("advance walks the frames",
                EmoteFrameNames.Advance(0, 3) == 1 && EmoteFrameNames.Advance(1, 3) == 2);
            Check("advance wraps to the first frame", EmoteFrameNames.Advance(2, 3) == 0);
            Check("advance on a still emote stays at 0", EmoteFrameNames.Advance(0, 1) == 0);
            Check("advance ignores an out of range frame", EmoteFrameNames.Advance(9, 3) == 1);

            // --- GIF timing: frames advance on the delays the file declares ---------
            int[] delays = { 100, 100, 100 };
            float due = 0.1f;
            Check("no advance before the frame is due",
                EmoteFrameNames.AdvanceByTime(0, delays, ref due, 0.05f, 3) == 0);

            due = 0.1f;
            int first = EmoteFrameNames.AdvanceByTime(0, delays, ref due, 0.1f, 3);
            Check("advances when the frame is due", first == 1 && Math.Abs(due - 0.2f) < 0.001f,
                first + " due " + due);

            due = 0.1f;
            int skipped = EmoteFrameNames.AdvanceByTime(0, delays, ref due, 0.25f, 3);
            Check("a slow tick catches up frame by frame",
                skipped == 2 && Math.Abs(due - 0.3f) < 0.001f, skipped + " due " + due);

            due = 0.1f;
            Check("catch-up is bounded by the step cap",
                EmoteFrameNames.AdvanceByTime(0, delays, ref due, 10f, 1) == 1);

            due = 0.1f;
            int[] fast = { 1, 1 };
            Check("absurdly short delays are floored to 20ms",
                EmoteFrameNames.AdvanceByTime(0, fast, ref due, 0.11f, 2) == 1 &&
                Math.Abs(due - 0.12f) < 0.001f, due.ToString());

            // --- setting the frame, not advancing it --------------------------------
            // Zeepkist rebuilds the chat field from its stored lines after every message, which
            // puts the emote back on frame 0. Retargeting recovers on the next tick.
            int swaps;
            string chat = "line one " + Sprite("Kappa") + " tail";
            string moved = EmoteFrameNames.Retarget(chat, "Kappa", 5, 3, out swaps);
            Check("retarget moves the emote to the given frame",
                swaps == 1 && moved.Contains(Sprite("Kappa~3")), moved);
            Check("retarget leaves the rest of the line alone",
                moved.StartsWith("line one ") && moved.EndsWith(" tail"), moved);

            string already = EmoteFrameNames.Retarget(moved, "Kappa", 5, 3, out swaps);
            Check("retarget is a no-op when the frame is already right",
                swaps == 0 && already == moved);

            string stale = Sprite("Kappa~2") + " and " + Sprite("Kappa");
            string pulled = EmoteFrameNames.Retarget(stale, "Kappa", 5, 4, out swaps);
            Check("a reset line is pulled back to the current frame",
                swaps == 2 && !pulled.Contains(Sprite("Kappa")) &&
                !pulled.Contains("Kappa~2") &&
                pulled == Sprite("Kappa~4") + " and " + Sprite("Kappa~4"), pulled);

            Check("retarget ignores lines without the emote",
                EmoteFrameNames.Retarget("nothing here", "Kappa", 5, 2, out swaps) == "nothing here" &&
                swaps == 0);

            Check("retarget ignores a still emote",
                EmoteFrameNames.Retarget(chat, "Kappa", 1, 3, out swaps) == chat && swaps == 0);

            Check("retarget wraps out of range frames",
                EmoteFrameNames.Retarget(chat, "Kappa", 4, 5, out swaps).Contains("Kappa~1"));

            Check("retarget does not touch a longer emote name",
                EmoteFrameNames.Retarget(Sprite("KappaPride"), "Kappa", 5, 2, out swaps)
                    .Contains("KappaPride") &&
                swaps == 0);

            Check("a still emote never advances on time",
                EmoteFrameNames.AdvanceByTime(0, new[] { 100 }, ref due, 99f, 2) == 0);
        }

        /// <summary>
        /// 7TV is where most animated emotes come from. The payloads below are trimmed from the
        /// real responses: WebP and AVIF files listed first, GIF last, and the animated flag in
        /// emote data rather than at the top level.
        ///
        /// Written with single quotes and converted, so the fixtures stay readable without
        /// layers of escaping.
        /// </summary>
        private static void SevenTvChecks()
        {
            Console.WriteLine();
            Console.WriteLine("7TV emotes");

            const string json =
                "{'emotes':[{'id':'01FCY','name':'RainTime','data':{'animated':true," +
                "'host':{'url':'//cdn.7tv.app/emote/01FCY','files':[" +
                "{'name':'1x.webp','format':'WEBP','width':32}," +
                "{'name':'2x.webp','format':'WEBP','width':64}," +
                "{'name':'1x.gif','format':'GIF','width':32}," +
                "{'name':'2x.gif','format':'GIF','width':64}," +
                "{'name':'4x.gif','format':'GIF','width':128}]}}}," +
                "{'id':'01STILL','name':'StillOne','data':{'animated':false," +
                "'host':{'url':'//cdn.7tv.app/emote/01STILL','files':[" +
                "{'name':'2x.gif','format':'GIF','width':64}]}}}," +
                "{'id':'01WEBP','name':'WebpOnly','data':{'animated':false," +
                "'host':{'url':'//cdn.7tv.app/emote/01WEBP','files':[" +
                "{'name':'2x.webp','format':'WEBP','width':64}]}}}]}";

            EmoteList parsed = EmoteProviderParsers.ParseSevenTv(Json(json));
            Check("7tv emotes parsed", parsed.Urls.Count == 2, parsed.Urls.Count.ToString());
            Check("7tv picks the gif closest to 64px",
                parsed.Urls.ContainsKey("RainTime") &&
                parsed.Urls["RainTime"] == "https://cdn.7tv.app/emote/01FCY/2x.gif",
                parsed.Urls.ContainsKey("RainTime") ? parsed.Urls["RainTime"] : "missing");
            Check("7tv emote without a gif is skipped", !parsed.Urls.ContainsKey("WebpOnly"));
            Check("7tv animated flag recorded",
                parsed.Animated.Contains("RainTime") && !parsed.Animated.Contains("StillOne"));

            const string channelJson =
                "{'emote_set':{'emotes':[{'name':'Chan7','data':{'animated':false," +
                "'host':{'url':'//cdn.7tv.app/emote/abc','files':[" +
                "{'name':'2x.gif','format':'GIF','width':64}]}}}]}}";

            EmoteList channel = EmoteProviderParsers.ParseSevenTv(Json(channelJson));
            Check("7tv channel emotes live under emote_set",
                channel.Urls.Count == 1 &&
                channel.Urls["Chan7"] == "https://cdn.7tv.app/emote/abc/2x.gif",
                channel.Urls.ContainsKey("Chan7") ? channel.Urls["Chan7"] : "missing");

            Check("7tv unknown shape yields nothing",
                EmoteProviderParsers.ParseSevenTv(Json("{'nope':1}")).Urls.Count == 0 &&
                EmoteProviderParsers.ParseSevenTv("not json").Urls.Count == 0);

            // --- every provider agrees on what "animated" means ----------------------
            EmoteList ffz = EmoteProviderParsers.ParseFfz(Json(
                "{'sets':{'1':{'emoticons':[{'id':1,'name':'Still'}," +
                "{'id':2,'name':'Moving','animated':true}]}}}"));
            Check("ffz animated flag recorded",
                ffz.Urls.Count == 2 && ffz.Animated.Count == 1 && ffz.Animated.Contains("Moving"));

            EmoteList bttv = EmoteProviderParsers.ParseBttv(Json(
                "[{'id':'a','code':'WebpOne','imageType':'webp'}," +
                "{'id':'b','code':'PngOne','imageType':'png'}," +
                "{'id':'c','code':'GifOne','imageType':'gif'}]"));
            Check("bttv webp dropped, png and gif kept", bttv.Urls.Count == 2, bttv.Urls.Count.ToString());
            Check("bttv gif emote marked animated",
                bttv.Animated.Count == 1 && bttv.Animated.Contains("GifOne"));
        }

        /// <summary>
        /// Full round trip for an animated emote: read the real 7TV list, download the GIF the
        /// mod would pick, and decode it. The offline checks prove the decoder against fixed
        /// bytes; this one proves the urls the parser builds are the ones that serve them.
        /// </summary>
        private static int CheckAnimatedEmoteRoundTrip()
        {
            const string label = "7TV animated emote round trip";

            try
            {
                EmoteList list = EmoteProviderParsers.ParseSevenTv(Fetch("https://7tv.io/v3/emote-sets/global"));

                string name = null;
                string url = null;
                foreach (KeyValuePair<string, string> emote in list.Urls)
                {
                    if (list.Animated.Contains(emote.Key) && emote.Value.EndsWith(".gif"))
                    {
                        name = emote.Key;
                        url = emote.Value;
                        break;
                    }
                }

                if (name == null)
                {
                    Console.WriteLine("  FAIL  " + label + ": no animated emote in the 7TV global set");
                    return 1;
                }

                byte[] bytes = Convert.FromBase64String(Convert.ToBase64String(FetchBytes(url)));

                if (!GifDecoder.TryDecode(bytes, 32, out List<GifDecoder.GifFrame> frames) || frames.Count < 2)
                {
                    Console.WriteLine("  FAIL  " + label + ": " + name + " did not decode to several frames (" +
                                      bytes.Length + " bytes)");
                    return 1;
                }

                Console.WriteLine("  PASS  " + label + ": " + name + " " + frames[0].Width + "x" +
                                  frames[0].Height + ", " + frames.Count + " frame(s), " + bytes.Length +
                                  " bytes, first delay " + frames[0].DelayMilliseconds + "ms");
                return 0;
            }
            catch (Exception exception)
            {
                Console.WriteLine("  FAIL  " + label + ": " + exception.GetType().Name + ": " + exception.Message);
                return 1;
            }
        }

        /// <summary>
        /// Real provider names that need aliasing: BTTV ships emotes called <c>:tf:</c> and
        /// <c>D:</c>, 7TV ships <c>:)</c>. This proves they survive parsing, get a tag-safe
        /// alias, are matched as words, and that the url the parser builds really serves an
        /// image.
        /// </summary>
        private static int CheckPunctuatedEmoteRoundTrip()
        {
            const string label = "punctuated emote names (BTTV)";

            try
            {
                EmoteList list = EmoteProviderParsers.ParseBttv(
                    Fetch("https://api.betterttv.net/3/cached/emotes/global"));

                List<string> punctuated = new List<string>();
                foreach (string name in list.Urls.Keys)
                {
                    if (!EmoteFrameNames.IsGlyphSafe(name))
                    {
                        punctuated.Add(name);
                    }
                }

                if (punctuated.Count == 0)
                {
                    Console.WriteLine("  FAIL  " + label + ": BTTV returned no punctuated names to check");
                    return 1;
                }

                string sample = punctuated[0];
                string alias = EmoteFrameNames.Alias(sample);
                string url = EmoteUrls.Bttv(list.Urls[sample]);
                byte[] bytes = FetchBytes(url);

                List<EmoteSpan> spans = EmoteWordScanner.Merge(
                    "try " + sample + " now", null, word => list.Urls.ContainsKey(word));

                bool ok = EmoteFrameNames.IsGlyphSafe(alias) && bytes.Length > 64 &&
                    spans.Count == 1 && spans[0].Name == sample && spans[0].Start == 4;

                Console.WriteLine("  " + (ok ? "PASS" : "FAIL") + "  " + label + ": " +
                    punctuated.Count + " punctuated name(s), e.g. " + sample + " -> alias " +
                    alias + ", " + bytes.Length + " bytes, matched at " +
                    (spans.Count > 0 ? spans[0].Start.ToString() : "nowhere"));

                return ok ? 0 : 1;
            }
            catch (Exception exception)
            {
                Console.WriteLine("  FAIL  " + label + ": " + exception.GetType().Name + ": " + exception.Message);
                return 1;
            }
        }

        /// <summary>
        /// The reported case end to end, offline apart from the image itself: a raw IRC line
        /// carrying an emote called <c>&lt;3</c>, through the real parser, into a sprite tag, and
        /// the bytes that Twitch's CDN actually serves for it.
        /// </summary>
        private static int CheckAngleBracketEmoteRoundTrip()
        {
            const string label = "angle bracket emote (<3) round trip";

            try
            {
                const string raw =
                    "@emotes=89:2-3;display-name=Someone :someone!someone@someone.tmi.twitch.tv " +
                    "PRIVMSG #channel :i <3 it";

                if (!TwitchIrcParser.TryParse(raw, out IrcMessage message))
                {
                    Console.WriteLine("  FAIL  " + label + ": the sample IRC line did not parse");
                    return 1;
                }

                string text = TwitchIrcClient.NormaliseChatText(message.Trailing);
                List<EmoteSpan> spans = TwitchEmoteTagParser.Parse(message.GetTag("emotes"), text);

                if (spans.Count != 1 || spans[0].Name != "<3" || spans[0].Id != "89")
                {
                    Console.WriteLine("  FAIL  " + label + ": span was not kept (" + spans.Count + ")");
                    return 1;
                }

                byte[] image = FetchBytes(EmoteUrls.Twitch(spans[0].Id));
                bool isPng = image.Length > 8 && image[0] == 0x89 && image[1] == 0x50 &&
                    image[2] == 0x4E && image[3] == 0x47;

                string glyph = EmoteFrameNames.Alias(spans[0].Name);
                string tag = EmoteFrameNames.Tag(glyph, 0, 1);
                bool safe = EmoteFrameNames.IsGlyphSafe(glyph) && !tag.Contains("<3");

                bool ok = isPng && safe && spans[0].Start == 2 && spans[0].Length == 2;
                Console.WriteLine("  " + (ok ? "PASS" : "FAIL") + "  " + label + ": span " +
                    spans[0] + " at " + spans[0].Start + ", glyph " + glyph + ", image " +
                    image.Length + " bytes (png=" + isPng + ")");
                return ok ? 0 : 1;
            }
            catch (Exception exception)
            {
                Console.WriteLine("  FAIL  " + label + ": " + exception.GetType().Name + ": " + exception.Message);
                return 1;
            }
        }

        private static string Fetch(string url)
        {
            return Encoding.UTF8.GetString(FetchBytes(url));
        }

        private static byte[] FetchBytes(string url)
        {
            System.Net.HttpWebRequest request =
                (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            request.UserAgent = "ZeepTwitchChat provider probe";
            request.Timeout = 15000;

            using (System.Net.WebResponse response = request.GetResponse())
            using (System.IO.Stream stream = response.GetResponseStream())
            using (System.IO.MemoryStream buffer = new System.IO.MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        /// <summary>Single-quoted JSON fixture to the double-quoted form the parsers read.</summary>
        private static string Json(string singleQuoted)
        {
            return singleQuoted.Replace('\u0027', '"');
        }

        /// <summary>
        /// A sprite tag, built without escaped quotes so the chat fixtures above stay readable.
        /// </summary>
        private static string Sprite(string emoteName)
        {
            char quote = (char)34;
            return "<sprite name=" + quote + emoteName + quote + ">";
        }

        private static bool SameColour(byte[] rgba, int width, int x, int y,
            byte r, byte g, byte b, byte a)
        {
            int height = rgba.Length / (width * 4);
            int offset = ((height - 1 - y) * width + x) * 4;

            return rgba[offset] == r && rgba[offset + 1] == g &&
                   rgba[offset + 2] == b && rgba[offset + 3] == a;
        }

        private static string Describe(byte[] rgba, int width, int x, int y)
        {
            int height = rgba.Length / (width * 4);
            int offset = ((height - 1 - y) * width + x) * 4;

            return "rgba(" + rgba[offset] + "," + rgba[offset + 1] + "," +
                   rgba[offset + 2] + "," + rgba[offset + 3] + ")";
        }
    }
}
