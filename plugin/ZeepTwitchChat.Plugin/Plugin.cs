using System.Collections.Generic;
using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ZeepTwitchChat.Chat;
using ZeepTwitchChat.Chat.Emotes;
using ZeepTwitchChat.Twitch;

namespace ZeepTwitchChat
{
    /// <summary>
    /// ZeepTwitchChat — mirrors your own Twitch chat into Zeepkist's in-game chat.
    ///
    /// Local only by design: lines are written into the local chat window and are never
    /// sent to the lobby or the network, so other players never see them.
    /// </summary>
    [BepInPlugin(Guid, PluginName, PluginVersion)]
    [BepInProcess("Zeepkist.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.nyxeonfox.zeepkist.twitchchat";
        public const string PluginName = "Twitch Chat";
        public const string PluginVersion = "1.3.1";

        private static ManualLogSource staticLogger;

        private Harmony harmony;
        private ConfigEntry<bool> modEnabled;
        private ConfigEntry<string> channel;
        private ConfigEntry<string> oauthToken;
        private ConfigEntry<string> host;
        private ConfigEntry<int> port;
        private ConfigEntry<bool> useTls;
        private ConfigEntry<bool> ignoreCertificateErrors;
        private ConfigEntry<bool> showLabel;
        private ConfigEntry<string> labelText;
        private ConfigEntry<string> labelColour;
        private ConfigEntry<bool> useTwitchColours;
        private ConfigEntry<bool> showBadges;
        private ConfigEntry<int> maxMessagesPerMinute;
        private ConfigEntry<float> minimumSecondsBetweenMessages;
        private ConfigEntry<int> maxMessageLength;
        private ConfigEntry<bool> renderEmotes;
        private ConfigEntry<bool> emoteBttv;
        private ConfigEntry<bool> emoteFfz;
        private ConfigEntry<bool> emoteSevenTv;
        private ConfigEntry<bool> emoteAnimate;
        private ConfigEntry<int> emoteAnimationFps;
        private ConfigEntry<int> emoteMaxFrames;
        private ConfigEntry<int> emoteMaxAnimated;
        private ConfigEntry<int> emoteMaxCached;
        private ConfigEntry<float> emoteWaitSeconds;
        private ConfigEntry<bool> emotePrewarm;
        private ConfigEntry<int> emoteSize;

        private ChatInjector injector;
        private TwitchIrcClient client;
        private EmoteService emotes;
        private DeferredChatQueue deferred;
        private readonly List<DeferredChatQueue.PendingLine> readyLines = new List<DeferredChatQueue.PendingLine>();
        private static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private float lastTimeoutLogTime = -30f;
        private int timeoutLogCount;
        private volatile ChatLineFormatter formatter;
        private bool loggedFirstMessage;

        internal static void LogInfo(object data)
        {
            staticLogger?.LogInfo(data);
        }

        private void Awake()
        {
            staticLogger = Logger;

            BindConfiguration();

            harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);

            ApplyConfiguration();

            Config.SettingChanged += OnSettingChanged;

            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded. " +
                           "Twitch messages appear in the chat window of online lobbies only.");
        }

        private void OnDestroy()
        {
            Config.SettingChanged -= OnSettingChanged;

            TwitchIrcClient activeClient = client;
            client = null;
            if (activeClient != null)
            {
                activeClient.Dispose();
            }

            EmoteService activeEmotes = emotes;
            emotes = null;
            if (activeEmotes != null)
            {
                activeEmotes.Dispose();
            }

            if (harmony != null)
            {
                harmony.UnpatchSelf();
                harmony = null;
            }

            staticLogger = null;
        }

        private void Update()
        {
            ChatInjector activeInjector = injector;
            if (activeInjector != null)
            {
                activeInjector.Drain();
            }

            EmoteService activeEmotes = emotes;
            if (activeEmotes != null)
            {
                activeEmotes.Update();
            }

            ReleaseDeferredLines();
        }

        /// <summary>
        /// Posts lines that were held back for their emote images. Called every frame on the
        /// main thread; the queue is empty unless an emote was still downloading.
        /// </summary>
        private void ReleaseDeferredLines()
        {
            DeferredChatQueue activeDeferred = deferred;
            ChatLineFormatter activeFormatter = formatter;
            ChatInjector activeInjector = injector;
            if (activeDeferred == null || activeFormatter == null || activeInjector == null)
            {
                return;
            }

            EmoteService activeEmotes = emotes;
            readyLines.Clear();
            activeDeferred.Take(Now, name => activeEmotes != null && activeEmotes.IsRenderable(name), readyLines);

            for (int i = 0; i < readyLines.Count; i++)
            {
                DeferredChatQueue.PendingLine line = readyLines[i];
                activeInjector.Enqueue(activeFormatter.Format(line.User, line.Text, line.Spans));

                if (HasUnreadyEmote(line.Spans, activeEmotes))
                {
                    LogTimedOutLine();
                }
            }
        }

        private static double Now => clock.Elapsed.TotalSeconds;

        private void BindConfiguration()
        {
            modEnabled = Config.Bind("Twitch", "Enabled", true,
                "Mirror your Twitch chat into the in-game chat window. Turn off to keep the mod " +
                "installed but idle.");

            channel = Config.Bind("Twitch", "Channel", string.Empty,
                "Twitch channel to read, for example 'your_channel'. No '#', no twitch.tv link. " +
                "Leave empty and the mod will not connect.");

            oauthToken = Config.Bind("Twitch", "OAuthToken", string.Empty,
                "Optional. OAuth token with the chat:read scope, to connect as a real account. " +
                "Leave empty for anonymous read-only access, which is all this mod needs.");

            host = Config.Bind("Advanced", "IrcHost", "irc.chat.twitch.tv",
                "Twitch IRC host. Only change this if you know why.");

            port = Config.Bind("Advanced", "IrcPort", 6697,
                "Twitch IRC port: 6697 is TLS, 6667 is plain text.");

            useTls = Config.Bind("Advanced", "UseTls", true,
                "Encrypt the IRC connection. Disable only if TLS fails on your machine; " +
                "the chat being read is public either way.");

            ignoreCertificateErrors = Config.Bind("Advanced", "IgnoreCertificateErrors", true,
                "Accept Twitch's certificate even when the Mono trust store cannot validate it, " +
                "which is common inside Proton prefixes. Set false to enforce strict validation.");

            showLabel = Config.Bind("Display", "ShowLabel", true,
                "Show a coloured label in front of every Twitch line so it is obvious at a glance " +
                "that the message is not from a lobby player.");

            labelText = Config.Bind("Display", "LabelText", "TWITCH",
                "Text inside the label. Empty hides the label even when ShowLabel is true.");

            labelColour = Config.Bind("Display", "LabelColour", "#9147FF",
                "Colour of the label, as #RRGGBB. Twitch purple by default.");

            useTwitchColours = Config.Bind("Display", "UseTwitchColours", true,
                "Colour each name with the colour the chatter picked in Twitch. When they have none " +
                "(or this is off) a stable colour is derived from their login.");

            showBadges = Config.Bind("Display", "ShowBadges", true,
                "Show [HOST], [MOD], [VIP] and [SUB] tags in front of names.");

            maxMessagesPerMinute = Config.Bind("Limits", "MaxMessagesPerMinute", 30,
                "Upper bound on Twitch lines inserted per minute. 0 means unlimited. The in-game " +
                "chat only holds 20 lines, so a busy channel scrolls fast.");

            minimumSecondsBetweenMessages = Config.Bind("Limits", "MinimumSecondsBetweenMessages", 0.75f,
                "Hard floor between two inserted lines, in seconds. 0 means no floor.");

            maxMessageLength = Config.Bind("Limits", "MaxMessageLength", 180,
                "Longer Twitch messages are truncated to this many characters.");

            renderEmotes = Config.Bind("Emotes", "RenderEmotes", true,
                "Draw emotes as images instead of their text names. Emotes are downloaded on " +
                "first use and cached for the session; until one is ready, its name is shown.");

            emoteBttv = Config.Bind("Emotes", "EnableBttv", true,
                "Resolve BetterTTV emotes (global and channel) by name.");

            emoteFfz = Config.Bind("Emotes", "EnableFfz", true,
                "Resolve FrankerFaceZ emotes (global and channel) by name.");

            emoteSevenTv = Config.Bind("Emotes", "EnableSevenTv", true,
                "Resolve 7TV emotes (global and channel) by name. 7TV emotes are downloaded " +
                "as GIF, which this mod decodes itself.");

            emoteAnimate = Config.Bind("Emotes", "AnimateEmotes", true,
                "Play animated emotes frame by frame. Zeepkist stores rendered chat text, so " +
                "animation means rewriting the visible lines a few times a second; turn this " +
                "off if a busy chat feels heavy and the first frame is enough.");

            emoteAnimationFps = Config.Bind("Emotes", "AnimationFps", 15,
                "How many times a second animated emotes are advanced. Each GIF plays at its own " +
                "speed; this only sets how smoothly that is sampled (and how often the chat text " +
                "is rewritten). 0 stops the animation pass entirely.");

            emoteMaxFrames = Config.Bind("Emotes", "MaxAnimationFrames", 24,
                "Frames decoded per animated emote. Higher is smoother and uses more memory.");

            emoteMaxAnimated = Config.Bind("Emotes", "MaxAnimatedEmotes", 40,
                "How many emotes may keep animation frames at once. Past this, more emotes " +
                "still render, as a still first frame.");

            emoteMaxCached = Config.Bind("Emotes", "MaxCachedEmotes", 200,
                "Upper bound on emotes downloaded and kept in the atlas for this session.");

            emoteWaitSeconds = Config.Bind("Emotes", "EmoteWaitSeconds", 3f,
                "How long a line may wait for its emote images before being posted with the " +
                "emote's text name instead. The game stores the rendered text, so first use of " +
                "an emote needs this window to show the image. 0 disables waiting.");

            emotePrewarm = Config.Bind("Emotes", "PrewarmEmotes", true,
                "Download BetterTTV and FrankerFaceZ emote images in the background after " +
                "connecting, so their first use is instant. Uses a little bandwidth and memory.");

            emoteSize = Config.Bind("Emotes", "EmoteSize", 24,
                "Rendered emote size in pixels. The chat font is roughly 24px at 1080p; raise " +
                "this if emotes look small, lower it if they crowd the line.");
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs args)
        {
            // Reconnect rather than trying to diff individual keys: reconnects are cheap and
            // this keeps behaviour predictable whatever the user edits.
            ApplyConfiguration();
        }

        private void ApplyConfiguration()
        {
            TwitchIrcClient previousClient = client;
            client = null;
            if (previousClient != null)
            {
                previousClient.Dispose();
            }

            ChatLineOptions lineOptions = new ChatLineOptions
            {
                ShowLabel = showLabel.Value,
                LabelText = labelText.Value,
                LabelColourHex = labelColour.Value,
                UseTwitchColours = useTwitchColours.Value,
                ShowBadges = showBadges.Value,
                MaxMessageLength = maxMessageLength.Value
            };

            EmoteService previousEmotes = emotes;
            emotes = new EmoteService(
                new EmoteOptions
                {
                    Enabled = renderEmotes.Value,
                    EnableBttv = emoteBttv.Value,
                    EnableFfz = emoteFfz.Value,
                    EnableSevenTv = emoteSevenTv.Value,
                    AnimateEmotes = emoteAnimate.Value,
                    AnimationFramesPerSecond = emoteAnimationFps.Value,
                    MaxAnimationFrames = emoteMaxFrames.Value,
                    MaxAnimatedEmotes = emoteMaxAnimated.Value,
                    MaxCachedEmotes = emoteMaxCached.Value,
                    PrewarmEmotes = emotePrewarm.Value,
                    RenderPixels = emoteSize.Value
                },
                message => Logger.LogInfo(message));

            if (previousEmotes != null)
            {
                previousEmotes.Dispose();
            }

            DeferredChatQueue previousDeferred = deferred;
            if (previousDeferred != null)
            {
                previousDeferred.Clear();
            }

            deferred = renderEmotes.Value && emoteWaitSeconds.Value > 0f
                ? new DeferredChatQueue(emoteWaitSeconds.Value)
                : null;

            formatter = new ChatLineFormatter(lineOptions, emotes);

            MessageRateLimiter limiter = new MessageRateLimiter(
                Math.Max(0, maxMessagesPerMinute.Value),
                60d,
                Math.Max(0d, minimumSecondsBetweenMessages.Value));

            injector = new ChatInjector(limiter, message => Logger.LogInfo(message));

            if (!modEnabled.Value)
            {
                Logger.LogInfo("Twitch Chat is disabled in the config.");
                return;
            }

            TwitchIrcOptions options = new TwitchIrcOptions
            {
                Host = host.Value,
                Port = port.Value,
                UseTls = useTls.Value,
                Channel = channel.Value,
                OAuthToken = oauthToken.Value,
                IgnoreCertificateErrors = ignoreCertificateErrors.Value
            };

            if (string.IsNullOrEmpty(options.NormalisedChannel))
            {
                Logger.LogInfo("Twitch Chat has no channel configured. Set Twitch/Channel in " +
                               "BepInEx/config/" + Guid + ".cfg to your Twitch name.");
                return;
            }

            TwitchIrcClient newClient = new TwitchIrcClient(options);
            newClient.Log += message => Logger.LogInfo(message);
            newClient.MessageReceived += OnTwitchMessage;
            newClient.ConnectionChanged += (connected, detail) =>
                Logger.LogInfo("Twitch Chat: " + detail + ".");
            newClient.RoomIdKnown += roomId =>
            {
                // Channel emotes (BTTV, 7TV) are keyed by the numeric room id.
                EmoteService activeEmotes = emotes;
                if (activeEmotes != null)
                {
                    activeEmotes.SetChannel(options.NormalisedChannel, roomId);
                }
            };
            client = newClient;

            newClient.Start();

            EmoteService emoteService = emotes;
            if (emoteService != null)
            {
                emoteService.SetChannel(options.NormalisedChannel, null);
            }
        }

        /// <summary>
        /// Records a line that was posted with at least one emote still unavailable. Throttled:
        /// chat can be busy and this is a support signal, not a message log.
        /// </summary>
        private void LogTimedOutLine()
        {
            timeoutLogCount++;

            float now = (float)Now;
            if (now - lastTimeoutLogTime < 10f)
            {
                return;
            }

            lastTimeoutLogTime = now;
            Logger.LogInfo("Twitch Chat: " + timeoutLogCount + " line(s) posted with an emote still " +
                           "downloading. Raise Emotes/EmoteWaitSeconds if that happens often.");
            timeoutLogCount = 0;
        }

        private static bool HasUnreadyEmote(IReadOnlyList<EmoteSpan> spans, EmoteService activeEmotes)
        {
            if (activeEmotes == null)
            {
                return false;
            }

            for (int i = 0; i < spans.Count; i++)
            {
                if (!activeEmotes.IsRenderable(spans[i].Name))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Raised on the IRC background thread: format the line, then hand it to the injector.</summary>
        private void OnTwitchMessage(TwitchChatUser user, string text, IReadOnlyList<EmoteSpan> spans)
        {
            ChatLineFormatter activeFormatter = formatter;
            ChatInjector activeInjector = injector;
            if (activeFormatter == null || activeInjector == null)
            {
                return;
            }

            EmoteService activeEmotes = emotes;
            if (activeEmotes != null)
            {
                activeEmotes.OnMessage(spans);
            }

            DeferredChatQueue activeDeferred = deferred;
            if (activeDeferred != null && spans != null && spans.Count > 0 && HasUnreadyEmote(spans, activeEmotes))
            {
                // The game keeps the rendered string, so wait for the images instead of
                // writing the emote's text name permanently into the chat buffer.
                activeDeferred.Add(user, text, spans, Now);
                return;
            }

            activeInjector.Enqueue(activeFormatter.Format(user, text, spans));

            if (!loggedFirstMessage)
            {
                loggedFirstMessage = true;
                LogInfo("Twitch Chat: first message received from " + user.Login + ".");
            }
        }
    }
}
