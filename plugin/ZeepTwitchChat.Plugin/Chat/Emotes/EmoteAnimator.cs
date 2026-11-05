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
        private readonly Action<string> log;
        private readonly float interval;
        private readonly Dictionary<string, int> currentFrame = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> animated = new List<string>();

        /// <summary>When the frame currently shown started, per emote: drives GIF timing.</summary>
        private readonly Dictionary<string, float> frameDue = new Dictionary<string, float>(StringComparer.Ordinal);

        private float nextUpdate;
        private int animatedStamp = -1;
        private bool loggedWrite;

        public EmoteAnimator(EmoteSpriteAsset atlas, int framesPerSecond, Action<string> log)
        {
            this.atlas = atlas;
            this.log = log ?? delegate { };
            interval = framesPerSecond > 0 ? 1f / framesPerSecond : 0f;
        }

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

            // One target frame per emote per tick, shared by both chat fields, so the lobby and
            // the in-race field always show the same frame of the same emote.
            List<int> targets = new List<int>(names.Length);
            for (int i = 0; i < names.Length; i++)
            {
                targets.Add(NextFrame(names[i], now));
            }

            int swaps = 0;
            swaps += AnimateField(ChatTextTargets.GetLogField(chatUi, false), names, targets);
            swaps += AnimateField(ChatTextTargets.GetLogField(chatUi, true), names, targets);

            if (!loggedWrite && swaps > 0)
            {
                loggedWrite = true;
                log("Twitch Chat: emote animation is rewriting the chat text.");
            }
        }

        /// <summary>
        /// Absolute frame for an emote at this moment, from the GIF's own delays. Keeping a
        /// clock rather than "advance what I last saw" is what makes the animation survive the
        /// game rebuilding the chat text from its stored lines after every new message.
        /// </summary>
        private int NextFrame(string name, float now)
        {
            int[] delays = atlas.FrameDelays(name);
            if (delays == null || delays.Length <= 1)
            {
                return 0;
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
            currentFrame[name] = next;
            return next;
        }

        private int AnimateField(TMP_Text field, string[] names, List<int> targets)
        {
            if (field == null)
            {
                return 0;
            }

            string text = field.text;
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            string updated = text;
            int swaps = 0;

            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                int frames = atlas.FrameCount(name);
                if (frames <= 1)
                {
                    continue;
                }

                int count = 0;
                updated = EmoteFrameNames.Retarget(updated, atlas.AliasFor(name), frames, targets[i], out count);
                swaps += count;
            }

            if (swaps == 0 || string.Equals(updated, text, StringComparison.Ordinal))
            {
                return 0;
            }

            // Assigning text re-parses the line set; the visible window is the game's business
            // (it drives chat scrolling), so it is put back exactly as it was.
            int firstVisible = field.firstVisibleCharacter;
            int maxVisible = field.maxVisibleCharacters;

            field.text = updated;

            field.firstVisibleCharacter = firstVisible;
            field.maxVisibleCharacters = maxVisible;

            return swaps;
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
