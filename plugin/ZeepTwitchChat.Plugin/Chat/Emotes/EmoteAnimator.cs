using System;
using System.Collections.Generic;
using TMPro;

namespace ZeepTwitchChat.Chat.Emotes
{
    /// <summary>
    /// Plays animated emotes by swapping the sprite tag inside the chat text.
    ///
    /// Zeepkist keeps its own 20 line buffer and stores rendered rich text, so there is no
    /// per-emote object to animate: the only lever is the text itself. Every frame of a
    /// GIF is packed into the atlas as its own glyph (<c>Name</c>, <c>Name~1</c>, ...), and
    /// this class rewrites the tags of the lines currently on screen at a fixed rate.
    ///
    /// Deliberately conservative: it only touches the two chat fields, only when an
    /// animated emote is actually present in the text, and only a few times a second. A
    /// line posted by the game is simply re-read on the next pass, so there is no state to
    /// keep in sync with the buffer.
    /// </summary>
    public sealed class EmoteAnimator
    {
        private readonly EmoteSpriteAsset atlas;
        private readonly float interval;
        private readonly Dictionary<string, int> currentFrame = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> animated = new List<string>();

        /// <summary>When the frame currently shown started, per emote: drives GIF timing.</summary>
        private readonly Dictionary<string, float> frameDue = new Dictionary<string, float>(StringComparer.Ordinal);

        private float nextUpdate;
        private int animatedStamp = -1;

        public EmoteAnimator(EmoteSpriteAsset atlas, int framesPerSecond)
        {
            this.atlas = atlas;
            interval = framesPerSecond > 0 ? 1f / framesPerSecond : 0f;
        }

        /// <summary>Number of animated emotes being played, for the log.</summary>
        public int AnimatedCount => animated.Count;

        /// <summary>Called once per frame; does nothing until the next interval has passed.</summary>
        public void Update(float now)
        {
            if (atlas == null || interval <= 0f || now < nextUpdate)
            {
                return;
            }

            nextUpdate = now + interval;

            string[] names = RefreshNames();
            if (names.Length == 0)
            {
                return;
            }

            OnlineChatUI chatUi = ChatInjector.Instance;
            if (chatUi == null)
            {
                return;
            }

            // One transition list per tick, shared by both chat fields: the lobby and the
            // in-race field must not advance the same emote twice.
            List<string> fromTags = new List<string>();
            List<string> toTags = new List<string>();
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                int[] delays = atlas.FrameDelays(name);
                if (delays == null || delays.Length <= 1)
                {
                    continue;
                }

                int frame;
                if (!currentFrame.TryGetValue(name, out frame))
                {
                    frame = 0;
                }

                float due;
                if (!frameDue.TryGetValue(name, out due))
                {
                    // Show frame 0 for its own duration before the first advance.
                    due = now + Math.Max(20, delays[frame]) / 1000f;
                }
                else if (due < now - 1f)
                {
                    // After a long hitch, restart from the current frame rather than
                    // fast-forwarding through a whole animation in one tick.
                    due = now;
                }

                int next = EmoteFrameNames.AdvanceByTime(frame, delays, ref due, now, delays.Length);
                frameDue[name] = due;

                if (next == frame)
                {
                    continue;
                }

                string from = atlas.GetFrameTag(name, frame);
                string to = atlas.GetFrameTag(name, next);

                if (from == null || to == null || from == to)
                {
                    continue;
                }

                fromTags.Add(from);
                toTags.Add(to);
                currentFrame[name] = next;
            }

            if (fromTags.Count == 0)
            {
                return;
            }

            AnimateField(ChatTextTargets.GetLogField(chatUi, false), fromTags, toTags);
            AnimateField(ChatTextTargets.GetLogField(chatUi, true), fromTags, toTags);
        }

        private void AnimateField(TMP_Text field, List<string> fromTags, List<string> toTags)
        {
            if (field == null)
            {
                return;
            }

            string text = field.text;
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            string updated = text;

            for (int i = 0; i < fromTags.Count; i++)
            {
                if (updated.IndexOf(fromTags[i], StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                updated = updated.Replace(fromTags[i], toTags[i]);
            }

            if (string.Equals(updated, text, StringComparison.Ordinal))
            {
                return;
            }

            // Assigning text re-parses the line set; the visible window is the game's business
            // (it drives chat scrolling), so it is put back exactly as it was.
            int firstVisible = field.firstVisibleCharacter;
            int maxVisible = field.maxVisibleCharacters;

            field.text = updated;

            field.firstVisibleCharacter = firstVisible;
            field.maxVisibleCharacters = maxVisible;
        }

        /// <summary>Animated names change when the atlas rebuilds, so they are cached by rebuild.</summary>
        private string[] RefreshNames()
        {
            // Renderable count moves independently of Count when a rebuild resolves glyphs
            // that were not resolvable before, so both are part of the stamp.
            int stamp = (atlas.Count * 1000) + atlas.RenderableCount;
            if (stamp != animatedStamp || animated.Count == 0)
            {
                animated.Clear();
                animated.AddRange(atlas.AnimatedNames());
                animatedStamp = stamp;
            }

            return animated.ToArray();
        }
    }
}
