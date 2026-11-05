using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using UnityEngine;
using ZeepTwitchChat.Twitch;
using ZeepTwitchChat.Util;

namespace ZeepTwitchChat.Chat.Emotes
{
    /// <summary>Emote behaviour switches, filled in from BepInEx config.</summary>
    public sealed class EmoteOptions
    {
        public bool Enabled = true;
        public bool EnableBttv = true;
        public bool EnableFfz = true;
        public bool EnableSevenTv = true;
        public int MaxCachedEmotes = 200;

        /// <summary>Download provider-listed emotes in the background so first use is instant.</summary>

        public bool PrewarmEmotes = true;

        /// <summary>Rendered emote size in pixels; the chat font is roughly 24px at 1080p.</summary>
        public int RenderPixels = 24;

        /// <summary>Play GIF emotes frame by frame instead of showing their first frame.</summary>
        public bool AnimateEmotes = true;

        /// <summary>How often animations are advanced. Frame timing follows each GIF; this only
        /// sets how smoothly that is sampled, and how often the chat text is rewritten.</summary>
        public int AnimationFramesPerSecond = 15;

        /// <summary>Frames decoded per animated emote (upper bound on atlas size and memory).</summary>
        public int MaxAnimationFrames = 24;

        /// <summary>How many emotes may hold animation frames at once.</summary>
        public int MaxAnimatedEmotes = 40;
    }

    /// <summary>Decoded emote textures plus the delays their container declared.</summary>
    internal sealed class DecodedEmote
    {
        public DecodedEmote(List<Texture2D> textures, int[] frameDelays)
        {
            Textures = textures;
            FrameDelays = frameDelays;
        }

        public List<Texture2D> Textures { get; private set; }

        /// <summary>One delay per frame in milliseconds, or null for a still image.</summary>
        public int[] FrameDelays { get; private set; }
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

        /// <summary>Ceiling on a provider list response (a busy 7TV channel set is ~2.4 MB).</summary>
        private const int MaxJsonCharacters = 8 * 1024 * 1024;

        /// <summary>
        /// Ceiling on remembered emote names. Provider lists are bounded, but the IRC emotes tag
        /// lets a hostile server introduce a new name on every message, so the registry needs a
        /// cap of its own; past it, unknown names simply stay text.
        /// </summary>
        private const int MaxKnownNames = 5000;

        private readonly EmoteOptions options;
        private readonly Action<string> log;
        private readonly EmoteSpriteAsset atlas;
        private readonly EmoteImageStore store;
        private readonly ConcurrentDictionary<string, string> knownUrls =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private readonly HashSet<string> fetchedUrls = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Names their provider reports as animated; those arrive as GIF.</summary>
        private readonly HashSet<string> animatedNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> prewarmQueue = new Queue<string>();

        private EmoteAnimator animator;
        private int animatedEmotes;
        private string channel;
        private string roomId;
        private float lastRebuildTime = -10f;
        private bool loggedReady;

        /// <summary>Decode summaries logged so far, per kind. A chatty log helps nobody.</summary>
        private int animatedDecodeLogs;
        private int singleFrameDecodeLogs;
        private bool loggedAnimation;


        public EmoteService(EmoteOptions options, Action<string> log)
        {
            this.options = options ?? new EmoteOptions();
            this.log = log ?? delegate { };

            if (this.options.Enabled)
            {
                atlas = new EmoteSpriteAsset(this.options.RenderPixels, this.log);
                store = new EmoteImageStore(this.options.MaxCachedEmotes);
            }

            if (atlas != null && this.options.AnimateEmotes && this.options.AnimationFramesPerSecond > 0)
            {
                animator = new EmoteAnimator(atlas, this.options.AnimationFramesPerSecond, this.log);
            }
        }

        /// <summary>Number of emotes that can currently be rendered as sprites.</summary>
        public int RenderableCount => atlas == null ? 0 : atlas.Count;

        /// <summary>
        /// True when this name belongs to a provider emote we know about (whether or not its
        /// image has arrived yet). Called from the IRC thread for every word of a message.
        /// </summary>
        public bool KnowsName(string emoteName)
        {
            return !string.IsNullOrEmpty(emoteName) && knownUrls.ContainsKey(emoteName);
        }

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

                    // The id comes straight off the IRC tag; Twitch ids are numeric.
                    if (!EmoteUrlPolicy.IsSafeId(span.Id))
                    {
                        continue;
                    }

                    url = EmoteUrls.Twitch(span.Id);

                    if (knownUrls.Count >= MaxKnownNames)
                    {
                        continue;
                    }

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

                DecodedEmote decoded = Decode(name, bytes);
                if (decoded != null)
                {
                    atlas.Add(name, decoded.Textures, decoded.FrameDelays);
                }
            }

            float now = Time.realtimeSinceStartup;

            // Animation only rewrites text that is already on screen, so it runs whether or
            // not the atlas changed this frame.
            if (animator != null)
            {
                animator.Update(now);
            }

            // Rebuild on a timer rather than only on frames that decoded an image: with a
            // single late download the atlas would otherwise stay stale until the next one.
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

            if (!loggedAnimation && animator != null)
            {
                // Asked of the atlas directly: the animator's own count is only refreshed on
                // its next tick, which can be after this rebuild.
                List<string> animatedNow = atlas.AnimatedNames();
                if (animatedNow.Count > 0)
                {
                    loggedAnimation = true;
                    int mostFrames = 0;
                    for (int i = 0; i < animatedNow.Count; i++)
                    {
                        mostFrames = Math.Max(mostFrames, atlas.FrameCount(animatedNow[i]));
                    }

                    log("Twitch Chat: emote animation active (" + animatedNow.Count +
                        " animated emote(s), up to " + mostFrames + " frames, " +
                        options.AnimationFramesPerSecond + " ticks/s).");
                }
            }
        }

        /// <summary>
        /// Turns downloaded bytes into textures. GIF is decoded here (Unity's loader reads
        /// PNG/JPG only), everything else goes through <c>ImageConversion.LoadImage</c>.
        /// </summary>
        private DecodedEmote Decode(string name, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            if (GifDecoder.LooksLikeGif(bytes))
            {
                return DecodeGif(name, bytes);
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
                return null;
            }

            return new DecodedEmote(new List<Texture2D> { texture }, null);
        }

        /// <summary>
        /// Decodes a GIF. Frames are only kept for emotes the provider calls animated, and
        /// only while the animated budget lasts; anything else keeps its first frame.
        /// </summary>
        private DecodedEmote DecodeGif(string name, byte[] bytes)
        {
            // The payload decides, not the provider's animated flag: a GIF that really has
            // several frames is animated, whatever a list said about it. The flag is still used
            // to keep animations out of the pre-warm pass, but a missing or wrong flag can no
            // longer cost the user their animation.
            bool animate = options.AnimateEmotes && animatedEmotes < options.MaxAnimatedEmotes;

            int maxFrames = animate ? Math.Max(1, options.MaxAnimationFrames) : 1;

            if (!GifDecoder.TryDecode(bytes, maxFrames, out List<GifDecoder.GifFrame> frames) ||
                frames.Count == 0)
            {
                return null;
            }

            List<Texture2D> textures = new List<Texture2D>(frames.Count);
            int[] delays = new int[frames.Count];

            for (int i = 0; i < frames.Count; i++)
            {
                delays[i] = frames[i].DelayMilliseconds;
            }

            for (int i = 0; i < frames.Count; i++)
            {
                GifDecoder.GifFrame frame = frames[i];
                Texture2D texture = new Texture2D(frame.Width, frame.Height, TextureFormat.RGBA32, false)
                {
                    name = name
                };

                try
                {
                    texture.LoadRawTextureData(frame.Rgba);
                    texture.Apply(false, false);
                }
                catch (Exception)
                {
                    UnityEngine.Object.Destroy(texture);
                    break;
                }

                textures.Add(texture);
            }

            if (textures.Count == 0)
            {
                return null;
            }

            if (textures.Count > 1)
            {
                animatedEmotes++;
                LogDecode(ref animatedDecodeLogs, name + ": " + textures.Count +
                    " frame(s), first delay " + delays[0] + "ms");
            }
            else
            {
                LogDecode(ref singleFrameDecodeLogs, name + ": 1 frame");
            }

            return new DecodedEmote(textures, delays);
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

        /// <summary>Logs the first few decodes of each kind: enough to diagnose, not a transcript.</summary>
        private void LogDecode(ref int logged, string detail)
        {
            logged++;

            if (logged > 3)
            {
                return;
            }

            log("Twitch Chat: emote decoded " + detail + ".");
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

            if (options.EnableSevenTv)
            {
                // 7TV quotes whole urls per emote (host + file), so the builder is the
                // identity function.
                Fetch("7TV global", "https://7tv.io/v3/emote-sets/global",
                    EmoteProviderParsers.ParseSevenTv, KeepUrl);

                if (!string.IsNullOrEmpty(numericRoomId))
                {
                    Fetch("7TV channel", "https://7tv.io/v3/users/twitch/" + numericRoomId,
                        EmoteProviderParsers.ParseSevenTv, KeepUrl);
                }
            }
        }

        private static string KeepUrl(string url)
        {
            return url;
        }

        private void Fetch(
            string label,
            string url,
            Func<string, EmoteList> parser,
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

                    EmoteList emotes = parser(json);
                    int added = 0;

                    foreach (KeyValuePair<string, string> emote in emotes.Urls)
                    {
                        if (knownUrls.Count >= MaxKnownNames)
                        {
                            break;
                        }

                        if (knownUrls.TryAdd(emote.Key, urlBuilder(emote.Value)))
                        {
                            added++;
                        }
                    }

                    lock (animatedNames)
                    {
                        foreach (string name in emotes.Animated)
                        {
                            animatedNames.Add(name);
                        }
                    }

                    if (options.PrewarmEmotes)
                    {
                        lock (prewarmQueue)
                        {
                            foreach (string name in emotes.Urls.Keys)
                            {
                                // Animated emotes are left out: a GIF is many frames to
                                // decode, and pre-warming everything would cost far more
                                // memory than the handful of emotes people actually use.
                                if (emotes.Animated.Contains(name))
                                {
                                    continue;
                                }

                                if (prewarmQueue.Count < this.options.MaxCachedEmotes)
                                {
                                    prewarmQueue.Enqueue(name);
                                }
                            }
                        }
                    }

                    log("Twitch Chat: " + label + " emotes loaded (" + added + " new, " +
                        emotes.Animated.Count + " animated).");
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

            // Lists are only read from the provider APIs, over https.
            if (!EmoteUrlPolicy.TryNormalise(url, EmoteUrlKind.Api, out Uri uri))
            {
                return false;
            }

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.AllowAutoRedirect = false;
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
                        char[] chunk = new char[16384];
                        System.Text.StringBuilder builder =
                            new System.Text.StringBuilder(MaxJsonCharacters);

                        while (true)
                        {
                            int read = reader.Read(chunk, 0, chunk.Length);
                            if (read <= 0)
                            {
                                break;
                            }

                            // 7TV channel sets reach a couple of megabytes; this is a ceiling
                            // on a hostile or runaway response, not a working limit.
                            if (builder.Length + read > MaxJsonCharacters)
                            {
                                return false;
                            }

                            builder.Append(chunk, 0, read);
                        }

                        json = builder.ToString();
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
