using System;
using System.Collections.Generic;

namespace ZeepTwitchChat.Twitch
{
    /// <summary>
    /// A single chat message, reduced to what the mod needs in order to render it.
    /// Dependency free: built from <see cref="IrcMessage"/> and consumed by the formatter.
    /// </summary>
    public sealed class TwitchChatUser
    {
        public TwitchChatUser(string login, string displayName, string colourHex, IList<string> badges)
        {
            Login = string.IsNullOrEmpty(login) ? "unknown" : login;
            DisplayName = string.IsNullOrEmpty(displayName) ? Login : displayName;
            ColourHex = colourHex;
            Badges = badges ?? new List<string>();
        }

        /// <summary>Lower-case Twitch login, always present (used as the stable colour seed).</summary>
        public string Login { get; }

        /// <summary>Name to display, honouring the user's capitalisation.</summary>
        public string DisplayName { get; }

        /// <summary>Colour chosen by the chatter in Twitch, as <c>#RRGGBB</c>, or null if unset.</summary>
        public string ColourHex { get; }

        /// <summary>Badge names from the <c>badges</c> tag, e.g. <c>broadcaster</c>, <c>moderator</c>, <c>subscriber</c>.</summary>
        public IList<string> Badges { get; }

        public bool HasBadge(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            for (int i = 0; i < Badges.Count; i++)
            {
                if (string.Equals(Badges[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Builds the user from a PRIVMSG. Falls back to the message prefix nick when
        /// the <c>display-name</c> tag is not present (for example when tags were not
        /// requested or the connection is anonymous on an older server).
        /// </summary>
        public static TwitchChatUser FromMessage(IrcMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            string login = message.GetTag("login") ?? message.Nick;
            string displayName = message.GetTag("display-name") ?? login;
            string colour = message.GetTag("color");

            List<string> badges = new List<string>();
            string badgesTag = message.GetTag("badges");
            if (!string.IsNullOrEmpty(badgesTag))
            {
                string[] entries = badgesTag.Split(',');
                for (int i = 0; i < entries.Length; i++)
                {
                    string entry = entries[i];
                    if (entry.Length == 0)
                    {
                        continue;
                    }

                    int slash = entry.IndexOf('/');
                    badges.Add(slash < 0 ? entry : entry.Substring(0, slash));
                }
            }

            // The mod flag arrives as its own tag for moderators when badges are trimmed.
            if (message.GetTag("mod") == "1" && !badges.Contains("moderator"))
            {
                badges.Add("moderator");
            }

            return new TwitchChatUser(login, displayName, colour, badges);
        }
    }
}
