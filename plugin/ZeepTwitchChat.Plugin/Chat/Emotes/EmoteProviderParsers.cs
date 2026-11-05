using System;
using System.Collections.Generic;
using ZeepTwitchChat.Util;

namespace ZeepTwitchChat.Chat.Emotes
{
    /// <summary>
    /// CDN url patterns for the four emote providers. Kept together so a provider change
    /// is a one-line edit; all urls are public and require no credentials.
    /// </summary>
    public static class EmoteUrls
    {
        /// <summary>Native Twitch emote, 112px (the 3.0 "default" dark variant).</summary>
        public static string Twitch(string id)
        {
            return "https://static-cdn.jtvnw.net/emoticons/v2/" + id + "/default/dark/3.0";
        }

        public static string Bttv(string id)
        {
            return "https://cdn.betterttv.net/emote/" + id + "/3x";
        }

        public static string Ffz(string id)
        {
            return "https://cdn.frankerfacez.com/emoticon/" + id + "/4";
        }
    }

    /// <summary>
    /// Parsers for the third-party emote lists. Every parser returns a name to id map and
    /// never throws: a provider that changes its schema should degrade to "no emotes from
    /// that provider", not break chat.
    ///
    /// Pure logic, unit tested against trimmed copies of the real responses.
    /// </summary>
    public static class EmoteProviderParsers
    {
        /// <summary>
        /// BetterTTV: the global endpoint is a bare array of <c>{id, code}</c>; the channel
        /// endpoints wrap the same objects in <c>emotes</c> and <c>sharedEmotes</c>.
        /// </summary>
        public static Dictionary<string, string> ParseBttv(string json)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!MiniJson.TryParse(json, out object root))
            {
                return result;
            }

            if (root is List<object> list)
            {
                AddBttvEntries(result, list);
                return result;
            }

            if (root is Dictionary<string, object> obj)
            {
                AddBttvEntries(result, GetList(obj, "emotes"));
                AddBttvEntries(result, GetList(obj, "sharedEmotes"));
            }

            return result;
        }

        /// <summary>FrankerFaceZ: <c>sets</c> is an object of set id to <c>{emoticons:[{id: number, name}]}</c>.</summary>
        public static Dictionary<string, string> ParseFfz(string json)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!MiniJson.TryParse(json, out object root) || !(root is Dictionary<string, object> obj))
            {
                return result;
            }

            if (!obj.TryGetValue("sets", out object sets) || !(sets is Dictionary<string, object> setObject))
            {
                return result;
            }

            foreach (KeyValuePair<string, object> set in setObject)
            {
                if (!(set.Value is Dictionary<string, object> setValue))
                {
                    continue;
                }

                List<object> emoticons = GetList(setValue, "emoticons");
                for (int i = 0; i < emoticons.Count; i++)
                {
                    if (!(emoticons[i] is Dictionary<string, object> emoticon))
                    {
                        continue;
                    }

                    string name = GetString(emoticon, "name");
                    string id = GetId(emoticon);
                    if (IsUsable(name, id))
                    {
                        result[name] = id;
                    }
                }
            }

            return result;
        }

        private static void AddBttvEntries(Dictionary<string, string> result, List<object> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (!(entries[i] is Dictionary<string, object> emote))
                {
                    continue;
                }

                string id = GetString(emote, "id");
                string code = GetString(emote, "code");

                // BTTV reports the real container: only PNG can be decoded by Unity's
                // Texture2D.LoadImage, so animated or WebP emotes are skipped (they keep
                // their text name in chat instead of appearing as a hole).
                string imageType = GetString(emote, "imageType");
                if (!string.IsNullOrEmpty(imageType) &&
                    !string.Equals(imageType, "png", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsUsable(code, id))
                {
                    result[code] = id;
                }
            }
        }

        private static bool IsUsable(string name, string id)
        {
            return !string.IsNullOrEmpty(name) &&
                   !string.IsNullOrEmpty(id) &&
                   Twitch.TwitchEmoteTagParser.IsSafeEmoteName(name) &&
                   id.Length <= 64;
        }

        private static List<object> GetList(Dictionary<string, object> obj, string key)
        {
            if (obj != null && obj.TryGetValue(key, out object value) && value is List<object> list)
            {
                return list;
            }

            return new List<object>();
        }

        private static string GetString(Dictionary<string, object> obj, string key)
        {
            if (obj != null && obj.TryGetValue(key, out object value) && value is string text)
            {
                return text;
            }

            return null;
        }

        /// <summary>FFZ ids are numbers in the JSON; accept both shapes.</summary>
        private static string GetId(Dictionary<string, object> obj)
        {
            if (obj == null || !obj.TryGetValue("id", out object value))
            {
                return null;
            }

            if (value is string text)
            {
                return text;
            }

            if (value is double number)
            {
                return ((long)number).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return null;
        }
    }
}
