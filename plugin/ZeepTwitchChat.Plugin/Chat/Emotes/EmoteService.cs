using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using UnityEngine;
using ZeepTwitchChat.Twitch;

namespace ZeepTwitchChat.Chat.Emotes
{
    /// <summary>Emote behaviour switches, filled in from BepInEx config.</summary>
    public sealed class EmoteOptions
    {
        public bool Enabled = true;
        public bool EnableBttv = true;
        public bool EnableFfz = true;
        public int MaxCachedEmotes = 200;

        /// <summary>Download provider-listed emotes in the background so first use is instant.</summary>

        public bool PrewarmEmotes = true;

        /// <summary>Rendered emote size in pixels; the chat font is roughly 24px at 1080p.</summary>
        public int RenderPixels = 24;
    }

    /// <summary>
    /// Glue between Twitch messages and the sprite atlas: decides which emote names can be
    /// rendered, fetches the provider lists and the images, and decodes textures on the main
    /// thread.
    ///
    /// Native Twitch emotes need no lookup at all (the IRC tag carries their id). BTTV, 7TV
    /// and FFZ emotes are resolved by name from their public lists.
    /// </summary>
    public sealed class EmoteService : IEmoteTagProvider, IDisposable
    {
        private const float RebuildInterval = 0.5f;

        private readonly EmoteOptions options;
        private readonly Action<string> log;
        private readonly EmoteSpriteAsset atlas;
        private readonly EmoteImageStore store;
        private readonly ConcurrentDictionary<string, string> knownUrls =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private readonly HashSet<string> fetchedUrls = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> prewarmQueue = new Queue<string>();

        private string channel;
        private string roomId;
        private float lastRebuildTime = -10f;
        private bool loggedReady;

        public EmoteService(EmoteOptions options, Action<string> log)
        {
            this.options = options ?? new EmoteOptions();
            this.log = log ?? delegate { };

            if (this.options.Enabled)
            {
                atlas = new EmoteSpriteAsset(this.options.RenderPixels, this.log);
                store = new EmoteImageStore(this.options.MaxCachedEmotes);
            }
        }

        /// <summary>Number of emotes that can currently be rendered as sprites.</summary>
        public int RenderableCount => atlas == null ? 0 : atlas.Count;

        /// <summary>True when this emote can currently be drawn as a sprite.</summary>
        public bool IsRenderable(string emoteName)
        {
            return atlas != null && atlas.GetSpriteTag(emoteName) != null;
        }

        /// <summary>Sprite tag for an emote, or null when it is not renderable yet.</summary>
        public string GetSpriteTag(string emoteName)
        {
            return atlas == null ? null : atlas.GetSpriteTag(emoteName);
        }

        /// <summary>
        /// Registers a new channel. Third-party emote lists are fetched in the background;
        /// BTTV and 7TV channel emotes need the numeric room id, so they are retried when it
        /// arrives.
        /// </summary>
        public void SetChannel(string channelLogin, string numericRoomId)
        {
            if (!options.Enabled)
            {
                return;
            }

            if (!string.IsNullOrEmpty(channelLogin))
            {
                channel = channelLogin;
            }

            if (!string.IsNullOrEmpty(numericRoomId))
            {
                roomId = numericRoomId;
            }

            if (string.IsNullOrEmpty(channel))
            {
                return;
            }

            FetchProviderLists(channel, roomId);
        }

        /// <summary>
        /// Called for every incoming message (IRC thread): records emote urls and queues any
        /// missing downloads. Safe to call from a background thread.
        /// </summary>
        public void OnMessage(IReadOnlyList<EmoteSpan> emotes)
        {
            if (atlas == null || emotes == null)
            {
                return;
            }

            for (int i = 0; i < emotes.Count; i++)
            {
                EmoteSpan span = emotes[i];

                if (!knownUrls.TryGetValue(span.Name, out string url))
                {
                    // A native Twitch emote carries its id in the IRC tag, so its url is
                    // known without any provider lookup.
                    if (string.IsNullOrEmpty(span.Id))
                    {
                        continue;
                    }

                    url = EmoteUrls.Twitch(span.Id);
                    knownUrls[span.Name] = url;
                }

                store.Request(span.Name, url);
            }
        }

        /// <summary>Main thread: decode finished downloads and rebuild the atlas once it settles.</summary>
        public void Update()
        {
            if (atlas == null)
            {
                return;
            }

            // A lobby can be joined after the first rebuild, so keep the attachment fresh.
            atlas.EnsureAttached();

            DrainPrewarm();

            // A few per frame: enough to fill quickly, few enough not to hitch the game.
            for (int guard = 0; guard < 4; guard++)
            {
                if (!store.TryTakeCompleted(out string name, out byte[] bytes))
                {
                    break;
                }

                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    name = name
                };

                bool loaded;
                try
                {
                    loaded = ImageConversion.LoadImage(texture, bytes);
                }
                catch (Exception)
                {
                    loaded = false;
                }

                if (!loaded)
                {
                    UnityEngine.Object.Destroy(texture);
                    continue;
                }

                atlas.Add(name, texture);
            }

            // Rebuild on a timer rather than only on frames that decoded an image: with a
            // single late download the atlas would otherwise stay stale until the next one.
            float now = Time.realtimeSinceStartup;
            if (!atlas.IsDirty || now - lastRebuildTime < RebuildInterval)
            {
                return;
            }

            lastRebuildTime = now;
            atlas.RebuildIfDirty();

            if (!loggedReady && atlas.RenderableCount > 0)
            {
                loggedReady = true;
                log("Twitch Chat: emote rendering active (" + atlas.RenderableCount + " emote(s) ready).");
            }
        }

        /// <summary>
        /// Requests a few pre-listed emotes per frame. Keeps first use of a BTTV/FFZ emote
        /// instant without hammering the network when the lists arrive.
        /// </summary>
        private void DrainPrewarm()
        {
            if (!options.PrewarmEmotes || !store.AcceptsMore)
            {
                return;
            }

            for (int i = 0; i < 4; i++)
            {
                string name;
                lock (prewarmQueue)
                {
                    if (prewarmQueue.Count == 0)
                    {
                        return;
                    }

                    name = prewarmQueue.Dequeue();
                }

                if (knownUrls.TryGetValue(name, out string url))
                {
                    store.Request(name, url);
                }
            }
        }

        /// <summary>Fetches every enabled provider list once per channel, in the background.</summary>
        private void FetchProviderLists(string channelLogin, string numericRoomId)
        {
            if (options.EnableBttv)
            {
                Fetch("BTTV global", "https://api.betterttv.net/3/cached/emotes/global",
                    EmoteProviderParsers.ParseBttv, EmoteUrls.Bttv);

                if (!string.IsNullOrEmpty(numericRoomId))
                {
                    Fetch("BTTV channel", "https://api.betterttv.net/3/cached/users/twitch/" + numericRoomId,
                        EmoteProviderParsers.ParseBttv, EmoteUrls.Bttv);
                }
            }

            if (options.EnableFfz)
            {
                Fetch("FFZ global", "https://api.frankerfacez.com/v1/set/global",
                    EmoteProviderParsers.ParseFfz, EmoteUrls.Ffz);

                Fetch("FFZ channel", "https://api.frankerfacez.com/v1/room/" + channelLogin,
                    EmoteProviderParsers.ParseFfz, EmoteUrls.Ffz);
            }
        }

        private void Fetch(
            string label,
            string url,
            Func<string, Dictionary<string, string>> parser,
            Func<string, string> urlBuilder)
        {
            lock (fetchedUrls)
            {
                if (!fetchedUrls.Add(url) || !store.AcceptsMore)
                {
                    return;
                }
            }

            Thread thread = new Thread(() =>
            {
                try
                {
                    if (!TryGetText(url, out string json, out bool notFound))
                    {
                        log(notFound
                            ? "Twitch Chat: no " + label + " emotes registered; continuing without them."
                            : "Twitch Chat: " + label + " emotes unavailable; continuing without them.");
                        return;
                    }

                    Dictionary<string, string> emotes = parser(json);
                    int added = 0;

                    foreach (KeyValuePair<string, string> emote in emotes)
                    {
                        if (knownUrls.TryAdd(emote.Key, urlBuilder(emote.Value)))
                        {
                            added++;
                        }
                    }

                    if (options.PrewarmEmotes)
                    {
                        lock (prewarmQueue)
                        {
                            foreach (string name in emotes.Keys)
                            {
                                if (prewarmQueue.Count < this.options.MaxCachedEmotes)
                                {
                                    prewarmQueue.Enqueue(name);
                                }
                            }
                        }
                    }

                    log("Twitch Chat: " + label + " emotes loaded (" + added + " new).");
                }
                catch (Exception exception)
                {
                    log("Twitch Chat: " + label + " emotes failed (" +
                        exception.GetType().Name + ": " + exception.Message + ").");
                }
            })
            {
                IsBackground = true,
                Name = "ZeepTwitchChat " + label
            };

            thread.Start();
        }

        private static bool TryGetText(string url, out string json, out bool notFound)
        {
            json = null;
            notFound = false;

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "ZeepTwitchChat/1.2 (+https://mod.io/g/zeepkist)";
            request.Accept = "application/json";
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            try
            {
                using (WebResponse response = request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        return false;
                    }

                    using (StreamReader reader = new StreamReader(stream))
                    {
                        json = reader.ReadToEnd();
                        return true;
                    }
                }
            }
            catch (WebException exception)
            {
                // A 404 means the channel simply has no emotes on that provider, which is
                // normal and should not read like a failure.
                HttpWebResponse errorResponse = exception.Response as HttpWebResponse;
                notFound = errorResponse != null && errorResponse.StatusCode == HttpStatusCode.NotFound;
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (store != null)
            {
                store.Dispose();
            }

            if (atlas != null)
            {
                atlas.Dispose();
            }
        }
    }
}
