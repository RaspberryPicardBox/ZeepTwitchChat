using System;
using System.Collections.Generic;
using System.Threading;
using ZeepTwitchChat.Chat;
using ZeepTwitchChat.Twitch;

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

            Console.WriteLine("ZeepTwitchChat logic checks");
            Console.WriteLine("===========================");

            ParserChecks();
            UserChecks();
            FormatterChecks();
            RateLimiterChecks();

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
                client.MessageReceived += (user, text) =>
                {
                    received++;
                    Console.WriteLine();
                    Console.WriteLine("  raw: " + user.DisplayName + " (" + user.Login + "): " + text);
                    Console.WriteLine("  tmp: " + formatter.Format(user, text));
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
