using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace ZeepTwitchChat.Twitch
{
    /// <summary>Connection settings for <see cref="TwitchIrcClient"/>.</summary>
    public sealed class TwitchIrcOptions
    {
        public string Host = "irc.chat.twitch.tv";
        public int Port = 6697;
        public bool UseTls = true;

        /// <summary>Twitch channel login, without the leading '#'.</summary>
        public string Channel = string.Empty;

        /// <summary>
        /// Optional OAuth token for a real login. Empty means the read-only anonymous
        /// "justinfan" login, which is all this mod needs to read a channel.
        /// </summary>
        public string OAuthToken = string.Empty;

        /// <summary>
        /// Twitch's certificate chain can fail validation on stale Mono trust stores,
        /// which is common inside Proton prefixes. The only data crossing this socket is
        /// public, read-only chat, so accepting the certificate is a pragmatic default.
        /// </summary>
        public bool IgnoreCertificateErrors = true;

        public string NormalisedChannel
        {
            get
            {
                string channel = (Channel ?? string.Empty).Trim();
                if (channel.StartsWith("#", StringComparison.Ordinal))
                {
                    channel = channel.Substring(1);
                }

                return channel.ToLowerInvariant();
            }
        }
    }

    /// <summary>
    /// Minimal read-only Twitch IRC client (IRCv3 over TCP or TLS).
    ///
    /// Runs on its own background thread and raises events from that thread; consumers
    /// are responsible for marshalling onto the Unity main thread. Deliberately free of
    /// Unity and BepInEx types so it can be exercised by the test harness and a live
    /// probe outside the game.
    /// </summary>
    public sealed class TwitchIrcClient : IDisposable
    {
        private const int MaxBackoffSeconds = 60;

        private readonly TwitchIrcOptions options;
        /// <summary>Largest IRC line the mod will hold; anything longer is skipped.</summary>
        private const int MaxLineLength = 8192;

        private readonly object gate = new object();

        private Thread thread;
        private TcpClient tcpClient;
        private StreamWriter writer;
        private volatile bool running;
        private bool disposed;

        public TwitchIrcClient(TwitchIrcOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Raised for every chat message: the chatter, the text, any emote spans, and Twitch's
        /// id for the message (used to recognise a later deletion).
        /// </summary>
        public event Action<TwitchChatUser, string, IReadOnlyList<EmoteSpan>, string> MessageReceived;

        /// <summary>
        /// Raised for <c>CLEARMSG</c> and <c>CLEARCHAT</c>: a moderator deleted a message, timed
        /// out or banned a user, or cleared the chat. Whether these arrive at all depends on the
        /// connection's identity - Twitch sends them to moderators and the broadcaster.
        /// </summary>
        public event Action<ChatModerationEvent> Moderated;

        /// <summary>Raised when the connection comes up (true) or drops (false).</summary>
        public event Action<bool, string> ConnectionChanged;

        /// <summary>Raised once the server tells us the channel's numeric id (needed for BTTV/7TV channel emotes).</summary>
        public event Action<string> RoomIdKnown;

        /// <summary>Diagnostics for the BepInEx log.</summary>
        public event Action<string> Log;

        /// <summary>Twitch's numeric id for the channel, or null until the server sends it.</summary>
        public string RoomId { get; private set; }

        public bool IsRunning => running;

        public void Start()
        {
            if (running || disposed)
            {
                return;
            }

            if (string.IsNullOrEmpty(options.NormalisedChannel))
            {
                RaiseLog("No Twitch channel configured; the client will not connect.");
                return;
            }

            running = true;
            thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = "ZeepTwitchChat IRC"
            };
            thread.Start();
        }

        public void Stop()
        {
            running = false;

            lock (gate)
            {
                try
                {
                    writer?.Close();
                }
                catch (Exception)
                {
                    // Closing the stream is best-effort; the socket close below unblocks the reader.
                }

                try
                {
                    tcpClient?.Close();
                }
                catch (Exception)
                {
                }
            }

            Thread current = thread;
            if (current != null && current.IsAlive)
            {
                current.Join(2000);
            }

            thread = null;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Stop();
        }

        private void RunLoop()
        {
            int attempt = 0;

            while (running)
            {
                try
                {
                    ConnectAndRead();
                    attempt = 0;
                }
                catch (Exception exception)
                {
                    // A read that fails during shutdown is expected: Stop() closes the socket
                    // underneath the reader. Only report failures we did not ask for.
                    if (running)
                    {
                        string inner = exception.InnerException == null
                            ? string.Empty
                            : " (" + exception.InnerException.Message + ")";
                        RaiseLog("Twitch IRC: " + exception.GetType().Name + ": " + exception.Message + inner);
                    }
                }

                if (!running)
                {
                    break;
                }

                attempt++;
                int delaySeconds = Math.Min(MaxBackoffSeconds, 1 << Math.Min(attempt, 6));
                RaiseLog("Twitch IRC: reconnecting in " + delaySeconds + "s (attempt " + attempt + ").");
                RaiseConnectionChanged(false, "reconnecting");

                for (int i = 0; i < delaySeconds * 4 && running; i++)
                {
                    Thread.Sleep(250);
                }
            }
        }

        private void ConnectAndRead()
        {
            string channel = options.NormalisedChannel;
            RaiseLog("Twitch IRC: connecting to " + options.Host + ":" + options.Port +
                     (options.UseTls ? " (TLS)" : " (plain)") + " for #" + channel);

            using (TcpClient client = new TcpClient())
            {
                client.NoDelay = true;
                client.Connect(options.Host, options.Port);

                Stream stream = client.GetStream();
                if (options.UseTls)
                {
                    SslStream ssl = new SslStream(stream, false, ValidateCertificate);
                    ssl.AuthenticateAsClient(options.Host, null, SslProtocols.Tls12, false);
                    stream = ssl;
                }

                lock (gate)
                {
                    if (!running)
                    {
                        return;
                    }

                    tcpClient = client;
                }

                using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false), false))
                using (StreamWriter streamWriter = new StreamWriter(stream, new UTF8Encoding(false))
                {
                    AutoFlush = true,
                    NewLine = "\r\n"
                })
                {
                    lock (gate)
                    {
                        writer = streamWriter;
                    }

                    // Ask for the tag block (colour, display-name, badges) and server commands.
                    streamWriter.WriteLine("CAP REQ :twitch.tv/tags twitch.tv/commands");

                    if (!string.IsNullOrEmpty(options.OAuthToken))
                    {
                        streamWriter.WriteLine("PASS oauth:" + options.OAuthToken.Trim());
                    }

                    streamWriter.WriteLine("NICK " + BuildNickname());
                    streamWriter.WriteLine("JOIN #" + channel);

                    string line;
                    while (running && (line = ReadLineCapped(reader, MaxLineLength)) != null)
                    {
                        if (!HandleLine(line, streamWriter))
                        {
                            break;
                        }
                    }
                }
            }
        }

        private void RaiseModerated(ChatModerationEvent moderation)
        {
            Action<ChatModerationEvent> handler = Moderated;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(moderation);
            }
            catch (Exception exception)
            {
                RaiseLog("Twitch IRC: could not handle a moderation event (" +
                    exception.GetType().Name + ": " + exception.Message + ").");
            }
        }


        /// <summary>Timeout length in seconds; 0 when the tag is absent or nonsense.</summary>

        /// <summary>
        /// Reads one line, holding at most <paramref name="maxLength"/> characters. IRC lines
        /// from Twitch are well under a kilobyte; the cap exists so a misbehaving or hostile
        /// server cannot make the mod allocate a huge string. An over-long line is consumed to
        /// its end and returned empty, which the parser then ignores, keeping the stream in
        /// sync. Returns null only at end of stream.
        /// </summary>
        private static string ReadLineCapped(StreamReader reader, int maxLength)
        {
            StringBuilder builder = new StringBuilder(256);
            bool overlong = false;

            while (true)
            {
                int value = reader.Read();
                if (value < 0)
                {
                    if (builder.Length == 0)
                    {
                        return null;
                    }

                    return overlong ? string.Empty : builder.ToString();
                }

                char c = (char)value;

                if (c == (char)10)
                {
                    return overlong ? string.Empty : builder.ToString();
                }

                if (c == (char)13)
                {
                    continue;
                }

                if (builder.Length >= maxLength)
                {
                    overlong = true;
                    continue;
                }

                builder.Append(c);
            }
        }

        private string BuildNickname()
        {
            if (!string.IsNullOrEmpty(options.OAuthToken))
            {
                return "ztc" + Math.Abs(Environment.TickCount);
            }

            // Anonymous read-only login understood by Twitch's IRC server.
            Random random = new Random();
            return "justinfan" + random.Next(10000, 99999);
        }

        /// <summary>Handles one line; returns false when the read loop should restart the connection.</summary>
        private bool HandleLine(string line, StreamWriter streamWriter)
        {
            if (line.StartsWith("PING", StringComparison.Ordinal) || line.IndexOf(" PING ", StringComparison.Ordinal) >= 0)
            {
                try
                {
                    streamWriter.WriteLine("PONG :tmi.twitch.tv");
                }
                catch (Exception)
                {
                    return false;
                }

                return true;
            }

            if (!TwitchIrcParser.TryParse(line, out IrcMessage message))
            {
                return true;
            }

            string roomId = message.GetTag("room-id");
            if (!string.IsNullOrEmpty(roomId) && roomId != RoomId)
            {
                RoomId = roomId;
                Action<string> roomHandler = RoomIdKnown;
                if (roomHandler != null)
                {
                    try
                    {
                        roomHandler(roomId);
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            switch (message.Command)
            {
                case "001":
                    RaiseLog("Twitch IRC: joined #" + options.NormalisedChannel + " (read-only).");
                    RaiseConnectionChanged(true, "connected");
                    return true;

                case "RECONNECT":
                    RaiseLog("Twitch IRC: server asked us to reconnect.");
                    RaiseConnectionChanged(false, "server reconnect");
                    return false;

                case "PRIVMSG":
                    HandlePrivMsg(message);
                    return true;

                case "CLEARMSG":
                case "CLEARCHAT":
                {
                    ChatModerationEvent moderation = ChatModerationEvent.FromMessage(message);
                    if (moderation != null)
                    {
                        RaiseModerated(moderation);
                    }

                    return true;
                }

                case "NOTICE":
                    RaiseLog("Twitch IRC notice: " + message.Trailing);
                    return true;

                default:
                    return true;
            }
        }

        /// <summary>
        /// Strips CTCP framing from a message body. Twitch sends <c>/me</c> as
        /// <c>\u0001ACTION waves\u0001</c>; rendered as <c>* waves</c> that matches how IRC
        /// clients (and the chatter's own view of Twitch chat) show it.
        /// </summary>
        public static string NormaliseChatText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            if (text[0] != '\u0001')
            {
                return text;
            }

            string inner = text.Trim('\u0001');
            const string actionPrefix = "ACTION ";

            if (inner.StartsWith(actionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return "* " + inner.Substring(actionPrefix.Length);
            }

            return inner;
        }

        private void HandlePrivMsg(IrcMessage message)
        {
            string text = NormaliseChatText(message.Trailing);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            TwitchChatUser user;
            try
            {
                user = TwitchChatUser.FromMessage(message);
            }
            catch (Exception exception)
            {
                RaiseLog("Twitch IRC: could not read chatter info: " + exception.Message);
                return;
            }

            List<EmoteSpan> emotes;
            try
            {
                emotes = TwitchEmoteTagParser.Parse(message.GetTag("emotes"), text);
            }
            catch (Exception exception)
            {
                RaiseLog("Twitch IRC: could not read emotes: " + exception.Message);
                emotes = new List<EmoteSpan>();
            }

            Action<TwitchChatUser, string, IReadOnlyList<EmoteSpan>, string> handler = MessageReceived;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(user, text, emotes, message.GetTag("id"));
            }
            catch (Exception exception)
            {
                RaiseLog("Twitch IRC: message handler failed: " + exception.Message);
            }
        }

        private bool ValidateCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None)
            {
                return true;
            }

            if (options.IgnoreCertificateErrors)
            {
                RaiseLog("Twitch IRC: ignoring certificate error (" + errors + "). " +
                         "Set IgnoreCertificateErrors to false to enforce strict validation.");
                return true;
            }

            RaiseLog("Twitch IRC: certificate rejected (" + errors + ").");
            return false;
        }

        private void RaiseLog(string message)
        {
            Action<string> handler = Log;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(message);
            }
            catch (Exception)
            {
                // Logging must never take the connection down.
            }
        }

        private void RaiseConnectionChanged(bool connected, string detail)
        {
            Action<bool, string> handler = ConnectionChanged;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(connected, detail);
            }
            catch (Exception)
            {
            }
        }
    }
}
