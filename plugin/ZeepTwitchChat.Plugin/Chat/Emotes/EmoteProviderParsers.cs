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

        /// <summary>
        /// 7TV hands out its files as names relative to a per-emote host, e.g.
        /// <c>//cdn.7tv.app/emote/&lt;id&gt;</c> plus <c>2x.gif</c>.
        /// </summary>
        public static string SevenTv(string hostUrl, string fileName)
        {
            string baseUrl = hostUrl.StartsWith("//", StringComparison.Ordinal) ? "https:" + hostUrl : hostUrl;
            return baseUrl.TrimEnd('/') + "/" + fileName;
        }
    }

    /// <summary>
    /// One provider's emote list: name to CDN url, plus the names that provider marks as
    /// animated. Animated names are not pre-warmed (an animation is many frames to decode)
    /// and they download as GIF, which <see cref="GifDecoder"/> handles.
    /// </summary>
    public sealed class EmoteList
    {
        public EmoteList()
        {
            Urls = new Dictionary<string, string>(StringComparer.Ordinal);
            Animated = new HashSet<string>(StringComparer.Ordinal);
        }

        public Dictionary<string, string> Urls { get; private set; }

        public HashSet<string> Animated { get; private set; }
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
        public static EmoteList ParseBttv(string json)
        {
            EmoteList result = new EmoteList();
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
        public static EmoteList ParseFfz(string json)
        {
            EmoteList result = new EmoteList();
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
                        result.Urls[name] = id;

                        // FrankerFaceZ serves a still first frame at the plain url, and that
                        // is what a decoded emote becomes here. The flag is still recorded so
                        // the animation pass knows the emote is not a still.
                        if (GetBool(emoticon, "animated"))
                        {
                            result.Animated.Add(name);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 7TV: <c>emote-sets/{id}</c> returns <c>emotes</c> at the root, while the user
        /// endpoint nests the same objects under <c>emote_set</c>. Each emote lists its files
        /// as WebP, AVIF and GIF; GIF is the one format here that can be decoded without a
        /// third-party library, so that is the one that is picked.
        /// </summary>
        public static EmoteList ParseSevenTv(string json)
        {
            EmoteList result = new EmoteList();
            if (!MiniJson.TryParse(json, out object root) || !(root is Dictionary<string, object> obj))
            {
                return result;
            }

            AddSevenTvEntries(result, GetList(obj, "emotes"));

            if (obj.TryGetValue("emote_set", out object set) && set is Dictionary<string, object> setObject)
            {
                AddSevenTvEntries(result, GetList(setObject, "emotes"));
            }

            return result;
        }

        private static void AddSevenTvEntries(EmoteList result, List<object> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (!(entries[i] is Dictionary<string, object> emote))
                {
                    continue;
                }

                string name = GetString(emote, "name");
                if (!IsUsableName(name))
                {
                    continue;
                }

                Dictionary<string, object> data = GetDictionary(emote, "data");
                Dictionary<string, object> host = GetDictionary(data, "host");
                if (host == null)
                {
                    continue;
                }

                string file = PickGif(GetList(host, "files"));
                string hostUrl = GetString(host, "url");
                if (file == null || string.IsNullOrEmpty(hostUrl) || hostUrl.Length > 200)
                {
                    continue;
                }

                result.Urls[name] = EmoteUrls.SevenTv(hostUrl, file);

                if (GetBool(data, "animated"))
                {
                    result.Animated.Add(name);
                }
            }
        }

        /// <summary>GIF file closest to 64px: enough detail at chat size, smallest download.</summary>
        private static string PickGif(List<object> files)
        {
            string best = null;
            int bestDelta = int.MaxValue;

            for (int i = 0; i < files.Count; i++)
            {
                if (!(files[i] is Dictionary<string, object> file))
                {
                    continue;
                }

                string name = GetString(file, "name");
                string format = GetString(file, "format");
                if (string.IsNullOrEmpty(name) ||
                    !string.Equals(format, "GIF", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int delta = Math.Abs((int)GetNumber(file, "width", 64) - 64);
                if (best == null || delta < bestDelta)
                {
                    best = name;
                    bestDelta = delta;
                }
            }

            return best;
        }

        private static void AddBttvEntries(EmoteList result, List<object> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (!(entries[i] is Dictionary<string, object> emote))
                {
                    continue;
                }

                string id = GetString(emote, "id");
                string code = GetString(emote, "code");

                // BTTV reports the real container. PNG goes straight to Unity's loader, GIF
                // (which is how BTTV serves its animated emotes) goes through GifDecoder, and
                // WebP is skipped: nothing here decodes it, so that emote keeps its text name
                // rather than leaving a hole in the line.
                string imageType = GetString(emote, "imageType");
                bool isGif = string.Equals(imageType, "gif", StringComparison.OrdinalIgnoreCase);
                bool isPng = string.IsNullOrEmpty(imageType) ||
                    string.Equals(imageType, "png", StringComparison.OrdinalIgnoreCase);

                if (!isPng && !isGif)
                {
                    continue;
                }

                if (IsUsable(code, id))
                {
                    result.Urls[code] = id;

                    if (isGif)
                    {
                        result.Animated.Add(code);
                    }
                }
            }
        }

        private static bool IsUsable(string name, string id)
        {
            return IsUsableName(name) && !string.IsNullOrEmpty(id) && id.Length <= 64;
        }

        private static bool IsUsableName(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                   name.Length <= 64 &&
                   Twitch.TwitchEmoteTagParser.IsSafeEmoteName(name);
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> obj, string key)
        {
            if (obj != null && obj.TryGetValue(key, out object value) && value is Dictionary<string, object> map)
            {
                return map;
            }

            return null;
        }

        private static bool GetBool(Dictionary<string, object> obj, string key)
        {
            return obj != null && obj.TryGetValue(key, out object value) && value is bool flag && flag;
        }

        private static double GetNumber(Dictionary<string, object> obj, string key, double fallback)
        {
            if (obj != null && obj.TryGetValue(key, out object value))
            {
                if (value is double number)
                {
                    return number;
                }

                if (value is string text &&
                    double.TryParse(text, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                {
                    return parsed;
                }
            }

            return fallback;
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
