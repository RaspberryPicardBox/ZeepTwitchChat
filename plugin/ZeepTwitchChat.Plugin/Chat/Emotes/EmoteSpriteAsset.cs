using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;

namespace ZeepTwitchChat.Chat.Emotes
{
    /// <summary>
    /// Builds one TextMeshPro sprite asset holding every emote the mod has downloaded, and
    /// registers it so the game's chat can resolve <c>&lt;sprite name="..."&gt;</c> tags.
    ///
    /// Why this shape:
    ///   * The chat's TMP text has no sprite asset of its own; the game relies on
    ///     <c>TMP_Settings.defaultSpriteAsset</c> (that is how player badges render), and
    ///     <c>TMP_SpriteAsset.SearchForSpriteByHashCode</c> walks
    ///     <c>defaultSpriteAsset.fallbackSpriteAssets</c>. Our asset is appended there.
    ///   * <c>faceInfo</c> has an internal setter, so its backing field is written through
    ///     Harmony's field accessor; without a sane point size TMP cannot scale a sprite.
    ///   * Only emote names present in an immutable snapshot are offered as tags, and that
    ///     snapshot is the only state a non-Unity thread touches.
    /// </summary>
    public sealed class EmoteSpriteAsset : IDisposable
    {
        /// <summary>Texture resolution per emote; independent of how large it renders.</summary>
        private const int CellPixels = 32;

        private const int MinColumns = 8;
        private const int MaxColumns = 64;

        private static AccessTools.FieldRef<TMP_SpriteAsset, FaceInfo> faceInfoRef;
        private static AccessTools.FieldRef<TMP_SpriteAsset, string> versionRef;

        /// <summary>Frames per emote, frame 0 first. One glyph per frame; a still emote has one.</summary>
        private readonly Dictionary<string, List<Texture2D>> frames =
            new Dictionary<string, List<Texture2D>>(StringComparer.Ordinal);

        private readonly Dictionary<string, int> frameCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Per-frame delay in milliseconds, as the GIF declared it; null for stills.</summary>
        private readonly Dictionary<string, int[]> frameDelays = new Dictionary<string, int[]>(StringComparer.Ordinal);
        private readonly List<string> order = new List<string>();
        private readonly List<UnityEngine.Object> retired = new List<UnityEngine.Object>();
        private readonly int renderPixels;
        private readonly Action<string> log;

        private TMP_SpriteAsset asset;
        private TMP_SpriteAsset registeredFallback;
        private Texture2D atlas;
        private volatile HashSet<string> renderable = new HashSet<string>(StringComparer.Ordinal);
        private volatile HashSet<string> renderableGlyphs = new HashSet<string>(StringComparer.Ordinal);
        private bool dirty;

        public EmoteSpriteAsset(int renderPixels, Action<string> log)
        {
            this.renderPixels = Math.Max(8, renderPixels);
            this.log = log ?? delegate { };
        }

        /// <summary>Name put in the sprite tag; also the asset's name in TMP.</summary>
        public string AssetName => "ZeepTwitchChat Emotes";

        public int Count => order.Count;

        /// <summary>True when TMP can resolve sprites at all (default sprite asset present).</summary>
        public bool CanRender => TMP_Settings.defaultSpriteAsset != null;

        /// <summary>Number of emote names TMP can currently resolve.</summary>
        public int RenderableCount => renderable.Count;

        /// <summary>True when new emotes are waiting to be packed into the atlas.</summary>
        public bool IsDirty => dirty;

        /// <summary>
        /// Re-attaches the current asset to the chat fields. Cheap and idempotent; called each
        /// frame so a lobby joined after the first rebuild still gets emotes.
        /// </summary>
        public void EnsureAttached()
        {
            TMP_SpriteAsset current = asset;
            if (current != null)
            {
                ChatTextTargets.Apply(ChatInjector.Instance, current, log);
            }
        }

        /// <summary>Adds a decoded single-frame emote. Call on the main thread.</summary>
        public void Add(string name, Texture2D texture)
        {
            Add(name, new List<Texture2D> { texture }, null);
        }

        /// <summary>Adds a decoded emote with the per-frame delays the GIF declared.</summary>
        public void Add(string name, List<Texture2D> emoteFrames, int[] delaysMs)
        {
            AddInternal(name, emoteFrames, delaysMs);
        }

        /// <summary>
        /// Adds a decoded emote, one texture per animation frame. Call on the main thread.
        /// The first texture is what a still line shows; the rest are the animation.
        /// </summary>
        public void Add(string name, List<Texture2D> emoteFrames)
        {
            AddInternal(name, emoteFrames, null);
        }

        private void AddInternal(string name, List<Texture2D> emoteFrames, int[] delaysMs)
        {
            bool usable = !string.IsNullOrEmpty(name) && emoteFrames != null && emoteFrames.Count > 0;

            if (usable)
            {
                for (int i = 0; i < emoteFrames.Count && usable; i++)
                {
                    usable = emoteFrames[i] != null;
                }
            }

            if (!usable || frames.ContainsKey(name))
            {
                DestroyAll(emoteFrames);
                return;
            }

            frames[name] = emoteFrames;
            frameCounts[name] = emoteFrames.Count;

            if (delaysMs != null && delaysMs.Length == emoteFrames.Count)
            {
                frameDelays[name] = delaysMs;
            }
            order.Add(name);
            dirty = true;
        }

        /// <summary>Number of animation frames for an emote (1 for a still emote).</summary>
        public int FrameCount(string name)
        {
            if (name == null)
            {
                return 0;
            }

            int count;
            return frameCounts.TryGetValue(name, out count) ? count : 0;
        }

        /// <summary>Sprite tag for one animation frame of an emote, or null if it is not renderable.</summary>
        public string GetFrameTag(string name, int frame)
        {
            if (string.IsNullOrEmpty(name) || !renderable.Contains(name))
            {
                return null;
            }

            int count = FrameCount(name);
            int index = count > 0 ? ((frame % count) + count) % count : 0;

            if (!renderableGlyphs.Contains(EmoteFrameNames.Glyph(name, index)))
            {
                return null;
            }

            return EmoteFrameNames.Tag(name, index, count);
        }

        /// <summary>Frame delays for an emote, or null when it is a still.</summary>
        public int[] FrameDelays(string name)
        {
            if (name == null)
            {
                return null;
            }

            int[] delays;
            return frameDelays.TryGetValue(name, out delays) ? delays : null;
        }

        /// <summary>Emotes that have more than one frame and can therefore animate.</summary>
        public List<string> AnimatedNames()
        {
            List<string> result = new List<string>();
            for (int i = 0; i < order.Count; i++)
            {
                if (FrameCount(order[i]) > 1 && renderable.Contains(order[i]))
                {
                    result.Add(order[i]);
                }
            }

            return result;
        }

        private static void DestroyAll(List<Texture2D> textures)
        {
            if (textures == null)
            {
                return;
            }

            for (int i = 0; i < textures.Count; i++)
            {
                if (textures[i] != null)
                {
                    UnityEngine.Object.Destroy(textures[i]);
                }
            }
        }

        /// <summary>
        /// Sprite tag for an emote, or null when it is not renderable yet. Safe to call from
        /// the IRC thread: it only reads an immutable snapshot.
        ///
        /// The plain form is deliberate: it resolves against the chat text's own sprite asset
        /// (which we set when it had none) instead of the asset-name lookup that TMP routes
        /// through MaterialReferenceManager.
        /// </summary>
        public string GetSpriteTag(string emoteName)
        {
            if (string.IsNullOrEmpty(emoteName) || !renderable.Contains(emoteName))
            {
                return null;
            }

            return EmoteFrameNames.Tag(emoteName, 0, FrameCount(emoteName));
        }

        /// <summary>Rebuilds the atlas when new emotes arrived. Main thread, once per frame.</summary>
        public void RebuildIfDirty()
        {
            if (!dirty || order.Count == 0)
            {
                return;
            }

            dirty = false;

            try
            {
                Rebuild();
            }
            catch (Exception exception)
            {
                log("Twitch Chat: could not build the emote atlas (" + Describe(exception) +
                    "); emotes stay as text.");
                renderable = new HashSet<string>(StringComparer.Ordinal);
                renderableGlyphs = new HashSet<string>(StringComparer.Ordinal);
                dirty = false;
            }
        }

        /// <summary>Type, message and the first stack frame: enough to locate a failure from a log.</summary>
        private static string Describe(Exception exception)
        {
            string stack = exception.StackTrace;
            if (!string.IsNullOrEmpty(stack))
            {
                int newline = stack.IndexOf('\n');
                stack = (newline > 0 ? stack.Substring(0, newline) : stack).Trim();
                return exception.GetType().Name + ": " + exception.Message + " @ " + stack;
            }

            return exception.GetType().Name + ": " + exception.Message;
        }

        private void Rebuild()
        {
            TMP_SpriteAsset defaultAsset = GetDefaultSpriteAsset();

            // One glyph per frame, so an animated emote costs as many cells as it has
            // frames. Columns grow with the glyph count to keep the atlas roughly square.
            List<string> glyphNames = new List<string>();
            List<Texture2D> glyphTextures = new List<Texture2D>();

            for (int e = 0; e < order.Count; e++)
            {
                List<Texture2D> emoteFrames = frames[order[e]];
                for (int f = 0; f < emoteFrames.Count; f++)
                {
                    glyphNames.Add(EmoteFrameNames.Glyph(order[e], f));
                    glyphTextures.Add(emoteFrames[f]);
                }
            }

            int columns = ColumnsFor(glyphNames.Count);
            int rows = (glyphNames.Count + columns - 1) / columns;
            int width = columns * CellPixels;
            int height = rows * CellPixels;

            Texture2D newAtlas = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "ZeepTwitchChat Emotes"
            };

            Color32[] pixels = new Color32[width * height];
            Color32 transparent = new Color32(255, 255, 255, 0);
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = transparent;
            }

            for (int i = 0; i < glyphTextures.Count; i++)
            {
                int cellX = (i % columns) * CellPixels;
                int cellY = (rows - 1 - (i / columns)) * CellPixels;
                BlitCell(pixels, width, glyphTextures[i], cellX, cellY);
            }

            newAtlas.SetPixels32(pixels);
            newAtlas.Apply(false, false);

            Material material = CreateSpriteMaterial(defaultAsset, newAtlas);
            if (material == null)
            {
                UnityEngine.Object.Destroy(newAtlas);
                log("Twitch Chat: no TextMeshPro sprite shader is available; emotes stay as text.");
                return;
            }

            TMP_SpriteAsset created = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
            created.name = AssetName;
            created.spriteSheet = newAtlas;

            // Guard against TMP's legacy upgrade path. UpdateLookupTables() calls
            // UpgradeSpriteAsset() when "material != null && version is empty", and that
            // method walks spriteInfoList, which is null on a runtime-created asset. It also
            // clears the glyph tables. Marking the asset current and assigning the material
            // only afterwards keeps that path from ever running (verified against the
            // shipped TMP build).
            created.spriteInfoList = new List<TMP_Sprite>();
            MarkCurrentVersion(created);

            SetFaceInfo(created, CreateFaceInfo(defaultAsset));

            created.spriteCharacterTable.Clear();
            created.spriteGlyphTable.Clear();

            for (int i = 0; i < glyphNames.Count; i++)
            {
                GlyphRect rect = new GlyphRect(
                    (i % columns) * CellPixels,
                    (rows - 1 - (i / columns)) * CellPixels,
                    CellPixels,
                    CellPixels);

                GlyphMetrics metrics = new GlyphMetrics(
                    renderPixels, renderPixels, 0f, renderPixels, renderPixels + 2f);

                TMP_SpriteGlyph glyph = new TMP_SpriteGlyph((uint)(i + 1), metrics, rect, 1f, 0);
                TMP_SpriteCharacter character = new TMP_SpriteCharacter((uint)(0xE000 + i), glyph)
                {
                    name = glyphNames[i]
                };

                created.spriteGlyphTable.Add(glyph);
                created.spriteCharacterTable.Add(character);
            }

            created.UpdateLookupTables();

            // Material last: with the version marked above this is belt and braces, but it
            // also means a future TMP change cannot re-enter the legacy upgrade path here.
            created.material = material;

            // Only advertise names TMP can actually resolve, so a failed glyph never costs
            // the chatter their emote name in the chat line.
            HashSet<string> verified = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> verifiedGlyphs = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < glyphNames.Count; i++)
            {
                if (created.GetSpriteIndexFromName(glyphNames[i]) >= 0)
                {
                    verifiedGlyphs.Add(glyphNames[i]);
                }
            }

            for (int i = 0; i < order.Count; i++)
            {
                if (verifiedGlyphs.Contains(order[i]))
                {
                    verified.Add(order[i]);
                }
            }

            UnregisterFallback();
            if (defaultAsset != null)
            {
                if (defaultAsset.fallbackSpriteAssets == null)
                {
                    defaultAsset.fallbackSpriteAssets = new List<TMP_SpriteAsset>();
                }

                defaultAsset.fallbackSpriteAssets.Add(created);
                registeredFallback = created;
            }

            // The chat text must be able to see the asset: a text with a null spriteAsset
            // resolves nothing at all (checked in TMP_Text.ParseText).
            int attached = ChatTextTargets.Apply(ChatInjector.Instance, created, log);

            // Keep the previous atlas alive briefly: chat lines already rendered reference it.
            Retire(asset);
            Retire(atlas);
            asset = created;
            atlas = newAtlas;
            renderable = verified;
            renderableGlyphs = verifiedGlyphs;

            log("Twitch Chat: emote atlas rebuilt (" + verified.Count + " of " + order.Count +
                " emote(s) resolvable; " + attached + " chat field(s) attached; source=" +
                (defaultAsset == null ? "none" : defaultAsset.name) +
                "; shader=" + (material.shader == null ? "none" : material.shader.name) + ").");
        }

        /// <summary>Square-ish layout: enough columns that the sheet is not taller than wide.</summary>
        private static int ColumnsFor(int glyphCount)
        {
            int columns = MinColumns;
            while ((long)columns * columns < glyphCount && columns < MaxColumns)
            {
                columns *= 2;
            }

            return columns;
        }

        /// <summary>Copies one emote texture into an atlas cell, scaling to the cell size.</summary>
        private static void BlitCell(Color32[] target, int targetWidth, Texture2D source, int cellX, int cellY)
        {
            if (source == null)
            {
                return;
            }

            Color32[] sourcePixels;
            try
            {
                sourcePixels = source.GetPixels32();
            }
            catch (Exception)
            {
                // Texture not readable: leave the cell transparent instead of failing the rebuild.
                return;
            }

            int sourceWidth = source.width;
            int sourceHeight = source.height;
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                return;
            }

            for (int y = 0; y < CellPixels; y++)
            {
                int sourceY = (y * sourceHeight) / CellPixels;
                int rowOffset = (cellY + y) * targetWidth + cellX;

                for (int x = 0; x < CellPixels; x++)
                {
                    int sourceX = (x * sourceWidth) / CellPixels;
                    target[rowOffset + x] = sourcePixels[sourceY * sourceWidth + sourceX];
                }
            }
        }

        /// <summary>
        /// The game's default sprite asset, or null. Guarded because TMP's settings object can
        /// fail to load, which makes the property throw rather than return null.
        /// </summary>
        private static TMP_SpriteAsset GetDefaultSpriteAsset()
        {
            try
            {
                return TMP_Settings.defaultSpriteAsset;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Clones the sprite material when possible, otherwise builds one from TMP's sprite shader.</summary>
        private static Material CreateSpriteMaterial(TMP_SpriteAsset source, Texture2D atlas)
        {
            try
            {
                if (source != null && source.material != null)
                {
                    return new Material(source.material)
                    {
                        mainTexture = atlas,
                        name = "ZeepTwitchChat Emotes"
                    };
                }
            }
            catch (Exception)
            {
                // Fall through to the shader lookup below.
            }

            Shader shader = Shader.Find("TextMeshPro/Sprite");
            if (shader == null)
            {
                shader = Shader.Find("TextMeshPro/Mobile/Bitmap");
            }

            if (shader == null)
            {
                return null;
            }

            Material material = new Material(shader)
            {
                name = "ZeepTwitchChat Emotes"
            };

            material.mainTexture = atlas;
            return material;
        }

        /// <summary>
        /// Face metrics for the emote asset. Copied from the game's sprite asset when there is
        /// one, otherwise derived from the configured emote size: TMP scales a sprite by
        /// <c>fontSize / pointSize</c>, so pointSize must never be zero.
        /// </summary>
        private FaceInfo CreateFaceInfo(TMP_SpriteAsset source)
        {
            if (source != null)
            {
                FaceInfo template = source.faceInfo;
                if (template.pointSize > 0)
                {
                    template.pointSize = renderPixels;
                    return template;
                }
            }

            int pointSize = renderPixels;
            return new FaceInfo
            {
                familyName = AssetName,
                styleName = "Regular",
                pointSize = pointSize,
                scale = 1f,
                lineHeight = pointSize,
                ascentLine = pointSize * 0.8f,
                capLine = pointSize * 0.7f,
                meanLine = pointSize * 0.5f,
                baseline = 0f,
                descentLine = pointSize * -0.2f,
                superscriptOffset = pointSize * 0.5f,
                superscriptSize = 0.5f,
                subscriptOffset = pointSize * 0.5f,
                subscriptSize = 0.5f,
                underlineOffset = pointSize * -0.1f,
                underlineThickness = 1f,
                strikethroughOffset = pointSize * 0.25f,
                strikethroughThickness = 1f,
                tabWidth = pointSize * 4f
            };
        }

        /// <summary><c>faceInfo</c> has an internal setter, so its field is written directly.</summary>
        private static void SetFaceInfo(TMP_SpriteAsset spriteAsset, FaceInfo faceInfo)
        {
            if (faceInfoRef == null)
            {
                faceInfoRef = AccessTools.FieldRefAccess<TMP_SpriteAsset, FaceInfo>("m_FaceInfo");
            }

            faceInfoRef(spriteAsset) = faceInfo;
        }

        /// <summary>
        /// Sets TMP's private version string so <c>UpdateLookupTables</c> treats the asset as
        /// already upgraded and skips <c>UpgradeSpriteAsset</c>.
        /// </summary>
        private static void MarkCurrentVersion(TMP_SpriteAsset spriteAsset)
        {
            if (versionRef == null)
            {
                versionRef = AccessTools.FieldRefAccess<TMP_SpriteAsset, string>("m_Version");
            }

            versionRef(spriteAsset) = "1.1.0";
        }

        private void UnregisterFallback()
        {
            TMP_SpriteAsset defaultAsset = TMP_Settings.defaultSpriteAsset;
            if (registeredFallback == null || defaultAsset == null || defaultAsset.fallbackSpriteAssets == null)
            {
                registeredFallback = null;
                return;
            }

            defaultAsset.fallbackSpriteAssets.Remove(registeredFallback);
            registeredFallback = null;
        }

        /// <summary>Holds a replaced Unity object so live text keeps rendering, then frees the oldest.</summary>
        private void Retire(UnityEngine.Object value)
        {
            if (value == null)
            {
                return;
            }

            retired.Add(value);
            while (retired.Count > 3)
            {
                UnityEngine.Object.Destroy(retired[0]);
                retired.RemoveAt(0);
            }
        }

        public void Dispose()
        {
            renderable = new HashSet<string>(StringComparer.Ordinal);
            renderableGlyphs = new HashSet<string>(StringComparer.Ordinal);
            UnregisterFallback();

            for (int i = 0; i < retired.Count; i++)
            {
                if (retired[i] != null)
                {
                    UnityEngine.Object.Destroy(retired[i]);
                }
            }

            retired.Clear();

            foreach (KeyValuePair<string, List<Texture2D>> emote in frames)
            {
                DestroyAll(emote.Value);
            }

            frames.Clear();
            frameCounts.Clear();
            frameDelays.Clear();
            order.Clear();

            if (asset != null)
            {
                UnityEngine.Object.Destroy(asset.material);
                UnityEngine.Object.Destroy(asset);
                asset = null;
            }

            if (atlas != null)
            {
                UnityEngine.Object.Destroy(atlas);
                atlas = null;
            }
        }
    }
}
