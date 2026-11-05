using System;
using System.Collections.Generic;

namespace ZeepTwitchChat.Chat.Emotes
{
    /// <summary>Which allow list a url has to satisfy.</summary>
    public enum EmoteUrlKind
    {
        /// <summary>Emote images: the four provider CDNs, nothing else.</summary>
        Image,

        /// <summary>Provider emote lists: the three provider APIs, nothing else.</summary>
        Api
    }

    /// <summary>
    /// Decides which urls the mod is willing to fetch.
    ///
    /// Emote ids and 7TV host urls come from provider JSON, and Twitch emote ids come from the
    /// IRC tag on a chat line, so neither is fully trusted. Without a policy the only thing
    /// stopping a <c>file://</c> or <c>http://</c> url would be the remote data itself, and
    /// <c>WebRequest</c> happily reads local files. Everything must therefore be https, on a
    /// known host, with no credentials, no fragment and no traversal.
    ///
    /// Pure logic: unit tested, including the rejected cases.
    /// </summary>
    public static class EmoteUrlPolicy
    {
        private const int MaxUrlLength = 512;

        private static readonly string[] ImageHosts =
        {
            "static-cdn.jtvnw.net",
            "cdn.betterttv.net",
            "cdn.frankerfacez.com",
            "cdn.7tv.app"
        };

        private static readonly string[] ApiHosts =
        {
            "api.betterttv.net",
            "api.frankerfacez.com",
            "7tv.io"
        };

        /// <summary>True when the url may be fetched for this purpose.</summary>
        public static bool IsAllowed(string url, EmoteUrlKind kind)
        {
            Uri uri;
            return TryNormalise(url, kind, out uri);
        }

        /// <summary>
        /// Normalises (protocol relative urls become https) and validates. Returns false for
        /// anything that is not a plain https url on the expected host.
        /// </summary>
        public static bool TryNormalise(string url, EmoteUrlKind kind, out Uri uri)
        {
            uri = null;

            if (string.IsNullOrEmpty(url) || url.Length > MaxUrlLength)
            {
                return false;
            }

            // Checked before parsing: Uri silently normalises dot segments away, so
            // "https://cdn/x/../../etc" would otherwise look like a plain same-host path.
            if (url.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            string candidate = url[0] == '/' ? "https:" + url : url;

            try
            {
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out uri))
                {
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            {
                return false;
            }

            string[] hosts = kind == EmoteUrlKind.Image ? ImageHosts : ApiHosts;
            bool hostMatches = false;

            for (int i = 0; i < hosts.Length; i++)
            {
                if (string.Equals(uri.Host, hosts[i], StringComparison.OrdinalIgnoreCase))
                {
                    hostMatches = true;
                    break;
                }
            }

            if (!hostMatches || HasTraversal(uri.AbsolutePath))
            {
                return false;
            }

            return true;
        }

        /// <summary>Twitch emote ids are numeric; anything else is not worth a request.</summary>
        public static bool IsSafeId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 32)
            {
                return false;
            }

            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool allowed = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                               (c >= '0' && c <= '9') || c == '_' || c == '-';

                if (!allowed)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasTraversal(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string[] segments = path.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                if (string.Equals(segments[i], "..", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
