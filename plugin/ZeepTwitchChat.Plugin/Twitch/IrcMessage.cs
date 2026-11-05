using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace ZeepTwitchChat.Twitch
{
    /// <summary>
    /// One line of Twitch IRC (IRCv3), parsed into its parts.
    ///
    /// Pure data + parsing only: no Unity, no BepInEx, no networking. This file is
    /// compiled into the test harness as well, so it must stay dependency free.
    ///
    /// Wire format:
    ///   [@tag=value;tag2=value2] [:prefix] COMMAND [param ...] [:trailing]
    /// </summary>
    public sealed class IrcMessage
    {
        private static readonly IReadOnlyDictionary<string, string> NoTags =
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

        private static readonly IReadOnlyList<string> NoParameters =
            new ReadOnlyCollection<string>(new List<string>());

        public IrcMessage(
            IReadOnlyDictionary<string, string> tags,
            string prefix,
            string command,
            IReadOnlyList<string> parameters,
            string trailing)
        {
            Tags = tags ?? NoTags;
            Prefix = prefix ?? string.Empty;
            Command = command ?? string.Empty;
            Parameters = parameters ?? NoParameters;
            Trailing = trailing;
        }

        /// <summary>IRCv3 tags (the <c>@...</c> block), keys lower-cased.</summary>
        public IReadOnlyDictionary<string, string> Tags { get; }

        /// <summary>Everything after <c>:</c> and before the command, e.g. <c>nick!nick@nick.tmi.twitch.tv</c>.</summary>
        public string Prefix { get; }

        /// <summary>Command or numeric reply, e.g. <c>PRIVMSG</c>, <c>PING</c>, <c>001</c>.</summary>
        public string Command { get; }

        /// <summary>Middle parameters (everything that is not the trailing parameter).</summary>
        public IReadOnlyList<string> Parameters { get; }

        /// <summary>Trailing parameter (the text after <c>: </c>), or null when absent.</summary>
        public string Trailing { get; }

        /// <summary>The nick part of the prefix, or an empty string for server messages.</summary>
        public string Nick
        {
            get
            {
                if (Prefix.Length == 0)
                {
                    return string.Empty;
                }

                // User prefixes are "nick!user@host" (or "nick@host"); a bare host is a server
                // message and has no nick.
                int end = Prefix.IndexOf('!');
                if (end < 0)
                {
                    end = Prefix.IndexOf('@');
                }

                return end < 0 ? string.Empty : Prefix.Substring(0, end);
            }
        }

        /// <summary>Channel the message was sent to, e.g. <c>#somechannel</c>. Empty when absent.</summary>
        public string Channel => Parameters.Count > 0 ? Parameters[0] : string.Empty;

        /// <summary>Looks a tag up, returning <paramref name="fallback"/> when it is missing or empty.</summary>
        public string GetTag(string key, string fallback = null)
        {
            if (key == null || !Tags.TryGetValue(key, out string value) || string.IsNullOrEmpty(value))
            {
                return fallback;
            }

            return value;
        }

        public override string ToString()
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(Command);
            if (Parameters.Count > 0)
            {
                builder.Append(' ').Append(string.Join(" ", Parameters));
            }

            if (Trailing != null)
            {
                builder.Append(" :").Append(Trailing);
            }

            return builder.ToString();
        }
    }
}
