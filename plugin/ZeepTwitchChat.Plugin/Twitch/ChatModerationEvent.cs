using System;

namespace ZeepTwitchChat.Twitch
{
    /// <summary>
    /// A moderation action Twitch reported on the chat stream.
    ///
    /// Two IRC commands produce these: <c>CLEARMSG</c> names a single deleted message by id,
    /// and <c>CLEARCHAT</c> names a user who was timed out or banned, or carries no target at
    /// all when the whole chat was cleared. Twitch sends them to moderators and the broadcaster,
    /// so a connection without a moderator token may never see any of them.
    /// </summary>
    public sealed class ChatModerationEvent
    {
        /// <summary>Id of the deleted message (<c>CLEARMSG</c>), or null.</summary>
        public string MessageId;

        /// <summary>Login the action applies to, or null/empty when the whole chat was cleared.</summary>
        public string Login;

        /// <summary>True when <c>CLEARCHAT</c> arrived with no target: everything was cleared.</summary>
        public bool IsWholeChat;

        /// <summary>Timeout length in seconds; 0 for a permanent ban or a chat clear.</summary>
        public int BanSeconds;

        /// <summary>True for <c>CLEARMSG</c>.</summary>
        public bool IsSingleMessage => !string.IsNullOrEmpty(MessageId);

        /// <summary>
        /// Builds the event from a parsed IRC line, or null when the command is not a moderation
        /// action. Pure, so the tag handling is unit tested against real line shapes.
        /// </summary>
        public static ChatModerationEvent FromMessage(IrcMessage message)
        {
            if (message == null)
            {
                return null;
            }

            if (string.Equals(message.Command, "CLEARMSG", StringComparison.Ordinal))
            {
                return new ChatModerationEvent
                {
                    MessageId = message.GetTag("target-msg-id"),
                    Login = message.GetTag("login")
                };
            }

            if (!string.Equals(message.Command, "CLEARCHAT", StringComparison.Ordinal))
            {
                return null;
            }

            // With a target it is a ban or a timeout; without one the whole chat was cleared.
            string login = FirstNonEmpty(message.GetTag("target-login"), message.Trailing);

            return new ChatModerationEvent
            {
                Login = login,
                IsWholeChat = string.IsNullOrEmpty(message.GetTag("target-user-id")) &&
                    string.IsNullOrEmpty(login),
                BanSeconds = ParseSeconds(message.GetTag("ban-duration"))
            };
        }

        private static string FirstNonEmpty(string first, string second)
        {
            return !string.IsNullOrEmpty(first) ? first : second;
        }

        /// <summary>Timeout length in seconds; 0 when the tag is absent or nonsense.</summary>
        private static int ParseSeconds(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return 0;
            }

            int seconds;
            return int.TryParse(value, out seconds) && seconds > 0 ? seconds : 0;
        }

        public override string ToString()
        {
            if (IsSingleMessage)
            {
                return "message " + MessageId + " deleted (" + Login + ")";
            }

            if (IsWholeChat)
            {
                return "chat cleared";
            }

            return "removed (" + Login + (BanSeconds > 0 ? ", timeout " + BanSeconds + "s" : ", banned") + ")";
        }
    }
}
