using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using ZeepTwitchChat.Chat;
using ZeepTwitchChat.Chat.Emotes;
using ZeepTwitchChat.Twitch;
using ZeepTwitchChat.Util;

namespace ZeepTwitchChat.LogicTests
{
    /// <summary>
    /// Dependency-free checks for the plugin's pure logic, plus a live probe against
    /// Twitch IRC. Exit code 0 = every check passed.
    /// </summary>
    internal static class Program
    {
        private static int checks;
        private static int failures;

        private static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--live")
            {
                return LiveProbe(args[1], args.Length >= 3 ? int.Parse(args[2]) : 20);
            }

            if (args.Length >= 1 && args[0] == "--providers")
            {
                return ProvidersProbe();
            }

            Console.WriteLine("ZeepTwitchChat logic checks");
            Console.WriteLine("===========================");

            ParserChecks();
            UserChecks();
            FormatterChecks();
            RateLimiterChecks();
            JsonChecks();
            EmoteChecks();

            Console.WriteLine();
            Console.WriteLine(checks + " checks, " + failures + " failure(s).");
            return failures == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- helpers

        private static void Check(string name, bool condition, string detail = null)
        {
            checks++;
            if (condition)
            {
                Console.WriteLine("  PASS  " + name);
                return;
            }

            failures++;
            Console.WriteLine("  FAIL  " + name + (detail == null ? string.Empty : "  -> " + detail));
        }

        private static void CheckEqual(string name, string expected, string actual)
        {
            Check(name, expected == actual, "expected '" + expected + "', got '" + actual + "'");
        }

        private static IrcMessage Parse(string line)
        {
            if (!TwitchIrcParser.TryParse(line, out IrcMessage message))
            {
                throw new InvalidOperationException("line did not parse: " + line);
            }

            return message;
        }

        // ---------------------------------------------------------------- parser

        private static void ParserChecks()
        {
            Console.WriteLine();
            Console.WriteLine("IRC parser");

            const string privmsg =
                "@badge-info=;badges=broadcaster/1,subscriber/12;color=#00FF00;display-name=SomeStreamer;" +
                "mod=0 :some_streamer!some_streamer@some_streamer.tmi.twitch.tv PRIVMSG #some_streamer :" +
                "hello : world <script>";

            IrcMessage message = Parse(privmsg);

            CheckEqual("command", "PRIVMSG", message.Command);
            CheckEqual("channel", "#some_streamer", message.Channel);
            CheckEqual("trailing keeps colons and angle brackets", "hello : world <script>", message.Trailing);
            CheckEqual("display-name tag", "SomeStreamer", message.GetTag("display-name"));
            CheckEqual("colour tag", "#00FF00", message.GetTag("color"));
            CheckEqual("prefix nick", "some_streamer", message.Nick);

            IrcMessage ping = Parse("PING :tmi.twitch.tv");
            CheckEqual("PING command", "PING", ping.Command);
            CheckEqual("PING trailing", "tmi.twitch.tv", ping.Trailing);

            IrcMessage welcome = Parse(":tmi.twitch.tv 001 justinfan1234 :Welcome, GLHF!");
            CheckEqual("numeric reply", "001", welcome.Command);
            CheckEqual("numeric welcome text", "Welcome, GLHF!", welcome.Trailing);
            CheckEqual("server prefix has no nick", string.Empty, welcome.Nick);

            IrcMessage escaped = Parse("@display-name=Test\\sUser;color= :t!t@t PRIVMSG #c :hi");
            CheckEqual("tag escape \\s", "Test User", escaped.GetTag("display-name"));
            CheckEqual("empty tag value falls back", "n/a", escaped.GetTag("color", "n/a"));

            Check("blank line rejected", !TwitchIrcParser.TryParse("   ", out _));
            Check("null line rejected", !TwitchIrcParser.TryParse(null, out _));
            Check("tags only rejected", !TwitchIrcParser.TryParse("@a=b", out _));

            IrcMessage noTrailing = Parse(":tmi.twitch.tv JOIN #channel");
            CheckEqual("JOIN command", "JOIN", noTrailing.Command);
            CheckEqual("JOIN has no trailing", null, noTrailing.Trailing);
            CheckEqual("JOIN channel param", "#channel", noTrailing.Channel);
        }

        // ---------------------------------------------------------------- user model

        private static void UserChecks()
        {
            Console.WriteLine();
            Console.WriteLine("Chatter model");

            IrcMessage message = Parse(
                "@badges=moderator/1,subscriber/3;color=#FF0000;display-name=Cool_Viewer;mod=1 " +
                ":cool_viewer!cool_viewer@cool_viewer.tmi.twitch.tv PRIVMSG #c :hi");

            TwitchChatUser user = TwitchChatUser.FromMessage(message);

            CheckEqual("display name", "Cool_Viewer", user.DisplayName);
            CheckEqual("login", "cool_viewer", user.Login);
            CheckEqual("colour", "#FF0000", user.ColourHex);
            Check("moderator badge", user.HasBadge("moderator"));
            Check("subscriber badge", user.HasBadge("subscriber"));
            Check("no broadcaster badge", !user.HasBadge("broadcaster"));

            IrcMessage plain = Parse(":someone!someone@someone.tmi.twitch.tv PRIVMSG #c :yo");
            TwitchChatUser fallback = TwitchChatUser.FromMessage(plain);
            CheckEqual("display name falls back to nick", "someone", fallback.DisplayName);

            IrcMessage modFlagOnly = Parse("@mod=1 :m!m@m PRIVMSG #c :hi");
            Check("mod tag implies moderator badge", TwitchChatUser.FromMessage(modFlagOnly).HasBadge("moderator"));

            CheckEqual("/me becomes an action line",
                "* waves hello", TwitchIrcClient.NormaliseChatText("\u0001ACTION waves hello\u0001"));
            CheckEqual("plain text untouched", "hello", TwitchIrcClient.NormaliseChatText("hello"));
            CheckEqual("empty text untouched", string.Empty, TwitchIrcClient.NormaliseChatText(string.Empty));
        }

        // ---------------------------------------------------------------- formatter

        private static void FormatterChecks()
        {
            Console.WriteLine();
            Console.WriteLine("Chat line formatter");

            ChatLineFormatter formatter = new ChatLineFormatter(new ChatLineOptions());

            TwitchChatUser broadcaster = new TwitchChatUser(
                "some_streamer", "SomeStreamer", "#00FF00",
                new List<string> { "broadcaster", "subscriber" });

            string line = formatter.Format(broadcaster, "hello world");

            Check("label is Twitch purple",
                line.Contains("<color=#9147FF><b>[TWITCH]</b></color>"), line);
            Check("broadcaster badge", line.Contains("[HOST]"), line);
            Check("subscriber badge", line.Contains("[SUB]"), line);
            Check("name uses the chatter colour", line.Contains("<color=#00FF00><b>SomeStreamer</b></color>: "), line);
            Check("message appended verbatim", line.EndsWith("hello world"), line);

            TwitchChatUser noColour = new TwitchChatUser("viewer", "Viewer", null, new List<string>());
            string first = ChatLineFormatter.ResolveColour(noColour, true);
            string second = ChatLineFormatter.ResolveColour(noColour, true);
            CheckEqual("fallback colour is stable", first, second);
            Check("fallback colour is a hex colour", ChatLineFormatter.IsValidHexColour(first), first);

            TwitchChatUser invalid = new TwitchChatUser("viewer", "Viewer", "#GGGGGG", new List<string>());
            Check("invalid colour rejected", !ChatLineFormatter.IsValidHexColour(invalid.ColourHex));
            CheckEqual("invalid colour falls back", first, ChatLineFormatter.ResolveColour(invalid, true));

            TwitchChatUser coloured = new TwitchChatUser("viewer", "Viewer", "#123abc", new List<string>());
            CheckEqual("twitch colour normalised", "#123ABC", ChatLineFormatter.ResolveColour(coloured, true));
            CheckEqual("twitch colour ignored when disabled", first, ChatLineFormatter.ResolveColour(coloured, false));

            // Rich-text injection is the interesting attack surface: everything a chatter
            // types must be inert in TextMeshPro.
            string hostile = formatter.Format(noColour, "<size=200>BIG</size> <sprite=\"achievement 2\"> <link=\"1\">x</link>");
            Check("angle brackets stripped", !hostile.Contains("<size=200>"), hostile);
            Check("sprite tag stripped", !hostile.Contains("<sprite="), hostile);
            Check("link tag stripped", !hostile.Contains("<link="), hostile);
            Check("visible text preserved", hostile.EndsWith("size=200BIG/size sprite=\"achievement 2\" link=\"1\"x/link"), hostile);

            CheckEqual("newlines and tabs collapse",
                "a b c", ChatLineFormatter.SanitiseText("a\r\n\tb   c", 100));

            string longMessage = ChatLineFormatter.SanitiseMessage(new string('a', 400), 60);
            Check("long message truncated", longMessage.Length <= 60, longMessage.Length.ToString());
            Check("truncation is marked", longMessage.EndsWith("..."), longMessage);

            ChatLineFormatter noLabel = new ChatLineFormatter(new ChatLineOptions { ShowLabel = false, ShowBadges = false });
            string plain = noLabel.Format(noColour, "hi");
            Check("label can be hidden", !plain.Contains("[TWITCH]"), plain);
            Check("badges can be hidden", !plain.Contains("[SUB]") && !plain.Contains("[HOST]"), plain);

            ChatLineFormatter custom = new ChatLineFormatter(new ChatLineOptions
            {
                LabelText = "TW",
                LabelColourHex = "#FF0000"
            });
            Check("label text and colour configurable",
                custom.Format(noColour, "hi").Contains("<color=#FF0000><b>[TW]</b></color>"));
        }

        // ---------------------------------------------------------------- rate limiter

        private static void RateLimiterChecks()
        {
            Console.WriteLine();
            Console.WriteLine("Rate limiter");

            double now = 0d;
            MessageRateLimiter limiter = new MessageRateLimiter(3, 60d, 0d, () => now);

            Check("first message allowed", limiter.TryAcquire());
            Check("second message allowed", limiter.TryAcquire());
            Check("third message allowed", limiter.TryAcquire());
            Check("fourth message blocked", !limiter.TryAcquire());
            CheckEqual("window count", "3", limiter.CountInWindow().ToString());

            now = 59d;
            Check("still blocked before the window clears", !limiter.TryAcquire());

            now = 61d;
            Check("allowed again after the window", limiter.TryAcquire());

            double gapNow = 0d;
            MessageRateLimiter spaced = new MessageRateLimiter(0, 60d, 1d, () => gapNow);
            Check("gap: first allowed", spaced.TryAcquire());
            gapNow = 0.5d;
            Check("gap: too soon blocked", !spaced.TryAcquire());
            gapNow = 1.0d;
            Check("gap: allowed at the boundary", spaced.TryAcquire());

            MessageRateLimiter unlimited = new MessageRateLimiter(0, 60d, 0d, () => 0d);
            bool allAllowed = true;
            for (int i = 0; i < 500; i++)
            {
                allAllowed &= unlimited.TryAcquire();
            }

            Check("0 per minute means unlimited", allAllowed);
        }

        // ---------------------------------------------------------------- JSON

        private static void JsonChecks()
        {
            Console.WriteLine();
            Console.WriteLine("Mini JSON");

            Check("object with mixed values", MiniJson.TryParse(
                "{\"a\":\"x\",\"b\":2,\"c\":true,\"d\":null,\"e\":[1,\"two\"]}", out object root) &&
                root is Dictionary<string, object> obj &&
                (string)obj["a"] == "x" &&
                (double)obj["b"] == 2d &&
                (bool)obj["c"] &&
                obj["d"] == null &&
                ((List<object>)obj["e"]).Count == 2, "nested parse mismatch");

            Check("escapes", MiniJson.TryParse("{\"s\":\"a\\\"b\\\\c\\u0041\\n\"}", out object escaped) &&
                             (string)((Dictionary<string, object>)escaped)["s"] == "a\"b\\cA\n");

            Check("array of objects", MiniJson.TryParse("[{\"id\":\"1\"},{\"id\":\"2\"}]", out object array) &&
                                      array is List<object> list && list.Count == 2);

            Check("empty containers", MiniJson.TryParse("{\"a\":[],\"b\":{}}", out object empty) &&
                                      ((Dictionary<string, object>)empty)["a"] is List<object> &&
                                      ((Dictionary<string, object>)empty)["b"] is Dictionary<string, object>);

            Check("invalid json rejected", !MiniJson.TryParse("{\"a\":}", out _));
            Check("empty json rejected", !MiniJson.TryParse(string.Empty, out _));
            Check("truncated json rejected", !MiniJson.TryParse("{\"a\":\"unterminated", out _));
        }

        /// <summary>Stand-in for the Unity sprite atlas: knows a fixed set of names.</summary>
        private sealed class FakeEmoteProvider : IEmoteTagProvider
        {
            private readonly HashSet<string> known;

            public FakeEmoteProvider(params string[] names)
            {
                known = new HashSet<string>(names, StringComparer.Ordinal);
            }

            public string GetSpriteTag(string emoteName)
            {
                return known.Contains(emoteName)
                    ? "<sprite name=\"" + emoteName + "\">"
                    : null;
            }
        }

        // ---------------------------------------------------------------- emotes

        private static void EmoteChecks()
        {
            Console.WriteLine();
            Console.WriteLine("Emotes");

            // --- Twitch's emotes tag -------------------------------------------------
            List<EmoteSpan> spans = TwitchEmoteTagParser.Parse("25:0-4/25:10-14", "Kappa and Kappa");
            Check("two spans parsed", spans.Count == 2, spans.Count.ToString());
            Check("span text and id", spans.Count == 2 && spans[0].Name == "Kappa" &&
                                      spans[0].Id == "25" && spans[1].Start == 10);

            List<EmoteSpan> multi = TwitchEmoteTagParser.Parse("1902:0-4,6-10", "Keepo Keepo");
            Check("multiple ranges in one group", multi.Count == 2 && multi[1].Start == 6);

            Check("out of range dropped", TwitchEmoteTagParser.Parse("25:0-99", "short").Count == 0);
            Check("reversed range dropped", TwitchEmoteTagParser.Parse("25:4-2", "Kappa").Count == 0);
            Check("garbage dropped", TwitchEmoteTagParser.Parse("x:y-z", "Kappa").Count == 0);
            Check("overlap dropped", TwitchEmoteTagParser.Parse("25:0-4/26:2-6", "Kappa Kappa").Count == 1);
            Check("unsafe emote name dropped", TwitchEmoteTagParser.Parse("25:0-5", "<b>x</b>").Count == 0);
            Check("empty tag yields none", TwitchEmoteTagParser.Parse(null, "Kappa").Count == 0);

            // --- provider response parsing -------------------------------------------
            Dictionary<string, string> bttv = EmoteProviderParsers.ParseBttv(
                "[{\"id\":\"aaa\",\"code\":\"PogU\"},{\"id\":\"bbb\",\"code\":\"catJAM\"}]");
            Check("bttv global", bttv.Count == 2 && bttv["PogU"] == "aaa");

            Dictionary<string, string> bttvChannel = EmoteProviderParsers.ParseBttv(
                "{\"id\":\"x\",\"emotes\":[{\"id\":\"e1\",\"code\":\"ChanA\"}]," +
                "\"sharedEmotes\":[{\"id\":\"e2\",\"code\":\"SharedB\"}]}");
            Check("bttv channel", bttvChannel.Count == 2 && bttvChannel["SharedB"] == "e2");

            Dictionary<string, string> ffz = EmoteProviderParsers.ParseFfz(
                "{\"sets\":{\"1\":{\"emoticons\":[{\"id\":12345,\"name\":\"FFZEmote\"}]}}}");
            Check("ffz numeric id", ffz.Count == 1 && ffz["FFZEmote"] == "12345");

            Check("unsafe provider name dropped",
                EmoteProviderParsers.ParseBttv("[{\"id\":\"x\",\"code\":\"bad<name>\"}]").Count == 0);
            Check("non-png bttv emote dropped",
                EmoteProviderParsers.ParseBttv(
                    "[{\"id\":\"a\",\"code\":\"WebpOne\",\"imageType\":\"webp\"}," +
                    "{\"id\":\"b\",\"code\":\"PngOne\",\"imageType\":\"png\"}]").Count == 1);
            Check("garbage provider json", EmoteProviderParsers.ParseBttv("not json").Count == 0);

            // --- formatter substitution ----------------------------------------------
            ChatLineOptions options = new ChatLineOptions();
            ChatLineFormatter withEmotes = new ChatLineFormatter(options, new FakeEmoteProvider("Kappa"));
            ChatLineFormatter withoutEmotes = new ChatLineFormatter(options, new FakeEmoteProvider());
            TwitchChatUser user = new TwitchChatUser("viewer", "Viewer", "#123456", new List<string>());

            string rendered = withEmotes.Format(user, "hi Kappa there",
                TwitchEmoteTagParser.Parse("25:3-7", "hi Kappa there"));
            Check("known emote becomes a sprite tag", rendered.Contains("<sprite name=\"Kappa\">"), rendered);
            Check("emote name replaced by the sprite", !rendered.EndsWith("hi Kappa there"), rendered);

            string notReady = withoutEmotes.Format(user, "hi Kappa there",
                TwitchEmoteTagParser.Parse("25:3-7", "hi Kappa there"));
            Check("emote that is not ready keeps its name", notReady.EndsWith("hi Kappa there"), notReady);

            Check("no spans keeps the name", withEmotes.Format(user, "hi Kappa there", null).EndsWith("hi Kappa there"));

            const string hostile = "<b>Kappa</b>";
            string sanitised = withEmotes.Format(user, hostile, TwitchEmoteTagParser.Parse("25:3-7", hostile));
            string body = sanitised.Substring(sanitised.IndexOf(": ", StringComparison.Ordinal) + 2);
            Check("markup around an emote is stripped",
                !body.Contains("<b>") && !body.Contains("</b>") && body.Contains("<sprite name=\"Kappa\">"), body);

            // Truncation must never leave a half-written tag in the line.
            StringBuilder message = new StringBuilder("start");
            List<EmoteSpan> many = new List<EmoteSpan>();
            for (int i = 0; i < 20; i++)
            {
                message.Append(" Kappa");
                many.Add(new EmoteSpan(message.Length - 5, 5, "Kappa", "25"));
            }

            ChatLineFormatter tiny = new ChatLineFormatter(
                new ChatLineOptions { MaxMessageLength = 32 }, new FakeEmoteProvider("Kappa"));

            string truncated = tiny.Format(user, message.ToString(), many);
            Check("truncation keeps every tag whole",
                CountOccurrences(truncated, "<sprite") == CountOccurrences(truncated, "<sprite name=\"Kappa\">"),
                truncated);
            Check("truncated line is marked", truncated.EndsWith("..."), truncated);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        /// <summary>Prints emote spans for the live probe in a compact form.</summary>
        private static string DescribeSpans(IReadOnlyList<EmoteSpan> spans)
        {
            if (spans == null || spans.Count == 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(" [");
            for (int i = 0; i < spans.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(spans[i].Name).Append('#').Append(spans[i].Id)
                       .Append('@').Append(spans[i].Start);
            }

            return builder.Append(']').ToString();
        }

        // ---------------------------------------------------------------- provider probe

        /// <summary>
        /// Fetches the real third-party emote lists and runs the parsers over them. This is
        /// the only way to catch a provider changing its schema without launching the game.
        /// </summary>
        private static int ProvidersProbe()
        {
            Console.WriteLine("Emote provider probe");
            Console.WriteLine("====================");

            int failures = 0;
            failures += CheckProvider("BTTV global", "https://api.betterttv.net/3/cached/emotes/global",
                EmoteProviderParsers.ParseBttv, EmoteUrls.Bttv);
            failures += CheckProvider("FFZ global", "https://api.frankerfacez.com/v1/set/global",
                EmoteProviderParsers.ParseFfz, EmoteUrls.Ffz);

            Console.WriteLine();
            Console.WriteLine("provider probe: " + failures + " failure(s).");
            return failures == 0 ? 0 : 1;
        }

        private static int CheckProvider(
            string label,
            string url,
            Func<string, Dictionary<string, string>> parser,
            Func<string, string> urlBuilder)
        {
            try
            {
                System.Net.HttpWebRequest request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                request.UserAgent = "ZeepTwitchChat/1.1 provider probe";
                request.Accept = "application/json";
                request.Timeout = 15000;

                string json;
                using (System.Net.WebResponse response = request.GetResponse())
                using (System.IO.Stream stream = response.GetResponseStream())
                using (System.IO.StreamReader reader = new System.IO.StreamReader(stream))
                {
                    json = reader.ReadToEnd();
                }

                Dictionary<string, string> emotes = parser(json);
                string sample = null;
                string sampleUrl = null;
                foreach (KeyValuePair<string, string> emote in emotes)
                {
                    sample = emote.Key;
                    sampleUrl = urlBuilder(emote.Value);
                    break;
                }

                bool ok = emotes.Count > 0 && sample != null;
                Console.WriteLine("  " + (ok ? "PASS" : "FAIL") + "  " + label + ": " +
                                  emotes.Count + " emote(s), e.g. " + sample + " -> " + sampleUrl);
                return ok ? 0 : 1;
            }
            catch (Exception exception)
            {
                Console.WriteLine("  FAIL  " + label + ": " + exception.GetType().Name + ": " + exception.Message);
                return 1;
            }
        }

        // ---------------------------------------------------------------- live probe

        /// <summary>
        /// Connects to Twitch IRC for real and prints the formatted lines. This validates
        /// the socket, TLS, tag parsing and formatting against live traffic, none of which
        /// the offline checks can cover.
        /// </summary>
        private static int LiveProbe(string channel, int seconds)
        {
            Console.WriteLine("Live IRC probe");
            Console.WriteLine("==============");
            Console.WriteLine("channel: #" + channel.TrimStart('#') + "  duration: " + seconds + "s  TLS: on");

            ChatLineFormatter formatter = new ChatLineFormatter(new ChatLineOptions());

            TwitchIrcOptions options = new TwitchIrcOptions
            {
                Channel = channel,
                UseTls = true
            };

            int received = 0;
            bool connected = false;

            using (TwitchIrcClient client = new TwitchIrcClient(options))
            {
                client.Log += message => Console.WriteLine("  [log]   " + message);
                client.ConnectionChanged += (isConnected, detail) =>
                {
                    connected |= isConnected;
                    Console.WriteLine("  [state] " + detail);
                };
                client.MessageReceived += (user, text, spans) =>
                {
                    received++;
                    Console.WriteLine();
                    Console.WriteLine("  raw: " + user.DisplayName + " (" + user.Login + "): " + text);
                    Console.WriteLine("  emotes: " + spans.Count + DescribeSpans(spans));
                    Console.WriteLine("  tmp: " + formatter.Format(user, text, spans));
                };

                client.Start();
                Thread.Sleep(seconds * 1000);
            }

            Console.WriteLine();
            Console.WriteLine("joined #" + options.NormalisedChannel + ": " + (connected ? "yes" : "no"));
            Console.WriteLine(received + " message(s) received in " + seconds + "s.");

            if (!connected)
            {
                Console.WriteLine("FAIL: could not join the channel.");
                return 1;
            }

            if (received == 0)
            {
                Console.WriteLine("NOTE: joined fine but the channel was quiet. Run the probe on a busy " +
                                  "channel to exercise live message parsing and formatting.");
            }

            return 0;
        }
    }
}
