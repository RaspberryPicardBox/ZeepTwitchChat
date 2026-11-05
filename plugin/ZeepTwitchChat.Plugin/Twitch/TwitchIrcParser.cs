using System;
using System.Collections.Generic;
using System.Text;

namespace ZeepTwitchChat.Twitch
{
    /// <summary>
    /// Parser for Twitch IRC lines. Dependency free so it can be unit tested
    /// without the game or BepInEx present.
    /// </summary>
    public static class TwitchIrcParser
    {
        /// <summary>
        /// Parses a single IRC line (without the trailing CR/LF).
        /// Returns false for blank lines and lines with no command.
        /// </summary>
        public static bool TryParse(string line, out IrcMessage message)
        {
            message = null;

            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            int index = 0;
            Dictionary<string, string> tags = null;

            // --- optional @tags block -------------------------------------------------
            if (line[0] == '@')
            {
                int space = line.IndexOf(' ');
                if (space < 0)
                {
                    return false;
                }

                tags = ParseTags(line.Substring(1, space - 1));
                index = space + 1;
            }

            SkipSpaces(line, ref index);
            if (index >= line.Length)
            {
                return false;
            }

            // --- optional :prefix -----------------------------------------------------
            string prefix = string.Empty;
            if (line[index] == ':')
            {
                int space = line.IndexOf(' ', index);
                if (space < 0)
                {
                    return false;
                }

                prefix = line.Substring(index + 1, space - index - 1);
                index = space + 1;
                SkipSpaces(line, ref index);
                if (index >= line.Length)
                {
                    return false;
                }
            }

            // --- command --------------------------------------------------------------
            int commandEnd = line.IndexOf(' ', index);
            string command = commandEnd < 0
                ? line.Substring(index)
                : line.Substring(index, commandEnd - index);

            if (command.Length == 0)
            {
                return false;
            }

            index = commandEnd < 0 ? line.Length : commandEnd;

            // --- parameters, with the trailing one delimited by " :" ------------------
            List<string> parameters = null;
            string trailing = null;

            while (index < line.Length)
            {
                SkipSpaces(line, ref index);
                if (index >= line.Length)
                {
                    break;
                }

                if (line[index] == ':')
                {
                    trailing = line.Substring(index + 1);
                    break;
                }

                int end = line.IndexOf(' ', index);
                if (end < 0)
                {
                    parameters = parameters ?? new List<string>();
                    parameters.Add(line.Substring(index));
                    break;
                }

                parameters = parameters ?? new List<string>();
                parameters.Add(line.Substring(index, end - index));
                index = end;
            }

            message = new IrcMessage(tags, prefix, command.ToUpperInvariant(), parameters, trailing);
            return true;
        }

        /// <summary>
        /// Parses the <c>@key=value;key2=value2</c> tag block. Keys are lower-cased
        /// and values are unescaped, as required by IRCv3.
        /// </summary>
        public static Dictionary<string, string> ParseTags(string tagBlock)
        {
            Dictionary<string, string> tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(tagBlock))
            {
                return tags;
            }

            string[] pairs = tagBlock.Split(';');
            for (int i = 0; i < pairs.Length; i++)
            {
                string pair = pairs[i];
                if (pair.Length == 0)
                {
                    continue;
                }

                int equals = pair.IndexOf('=');
                if (equals < 0)
                {
                    tags[pair.ToLowerInvariant()] = string.Empty;
                    continue;
                }

                string key = pair.Substring(0, equals).ToLowerInvariant();
                tags[key] = UnescapeTagValue(pair.Substring(equals + 1));
            }

            return tags;
        }

        /// <summary>Resolves the IRCv3 tag value escapes (<c>\s</c>, <c>\:</c>, <c>\\</c>, <c>\r</c>, <c>\n</c>).</summary>
        public static string UnescapeTagValue(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0)
            {
                return value ?? string.Empty;
            }

            StringBuilder builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c != '\\' || i + 1 >= value.Length)
                {
                    builder.Append(c);
                    continue;
                }

                char next = value[++i];
                switch (next)
                {
                    case 's':
                        builder.Append(' ');
                        break;
                    case ':':
                        builder.Append(';');
                        break;
                    case '\\':
                        builder.Append('\\');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    default:
                        // Unknown escape: keep the backslash so nothing is silently lost.
                        builder.Append('\\').Append(next);
                        break;
                }
            }

            return builder.ToString();
        }

        private static void SkipSpaces(string line, ref int index)
        {
            while (index < line.Length && line[index] == ' ')
            {
                index++;
            }
        }
    }
}
