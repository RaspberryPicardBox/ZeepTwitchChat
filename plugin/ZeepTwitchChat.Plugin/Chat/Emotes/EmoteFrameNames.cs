using System;
using System.Globalization;

namespace ZeepTwitchChat.Chat.Emotes
{
    /// <summary>
    /// How an animation frame is named and reached. Kept in one Unity-free place so the atlas
    /// (which creates the glyphs) and the animator (which swaps the tags) cannot drift apart,
    /// and so the cycling can be unit tested outside the game.
    /// </summary>
    public static class EmoteFrameNames
    {
        /// <summary>
        /// Glyph name TMP looks up: the emote name itself for frame 0, then <c>Name~1</c>,
        /// <c>Name~2</c>, ... Frame 0 keeps the plain name so a still emote is unaffected.
        /// </summary>
        public static string Glyph(string emoteName, int frame)
        {
            if (string.IsNullOrEmpty(emoteName) || frame <= 0)
            {
                return emoteName;
            }

            return emoteName + "~" + frame.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Sprite tag for one frame, wrapping frame indices into range.</summary>
        public static string Tag(string emoteName, int frame, int frameCount)
        {
            if (string.IsNullOrEmpty(emoteName))
            {
                return null;
            }

            int index = frameCount > 0 ? ((frame % frameCount) + frameCount) % frameCount : 0;
            return "<sprite name=\"" + Glyph(emoteName, index) + "\">";
        }

        /// <summary>
        /// Rewrites every frame tag of one emote in <paramref name="text"/> to
        /// <paramref name="targetFrame"/>, returning the new text and how many swaps were made.
        ///
        /// Deliberately "set the frame" rather than "advance the frame": Zeepkist keeps its own
        /// twenty line buffer and rebuilds the chat field from the stored strings whenever a new
        /// message arrives, which silently undoes a swap. Setting the absolute frame means such a
        /// reset costs one repaint instead of leaving the emote stuck on frame 0 for good.
        /// </summary>
        public static string Retarget(string text, string emoteName, int frameCount, int targetFrame, out int swaps)
        {
            swaps = 0;

            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(emoteName) || frameCount <= 1)
            {
                return text;
            }

            // Cheap gate: no mention of the emote at all means nothing to do.
            if (text.IndexOf(emoteName, StringComparison.Ordinal) < 0)
            {
                return text;
            }

            int target = ((targetFrame % frameCount) + frameCount) % frameCount;
            string targetTag = Tag(emoteName, target, frameCount);
            string current = text;

            for (int frame = 0; frame < frameCount; frame++)
            {
                if (frame == target)
                {
                    continue;
                }

                string tag = Tag(emoteName, frame, frameCount);
                if (current.IndexOf(tag, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                current = current.Replace(tag, targetTag);
                swaps++;
            }

            return current;
        }

        /// <summary>
        /// Advances by elapsed time using the GIF's own frame delays, so an emote plays at the
        /// speed its author chose rather than at whatever rate the chat is being redrawn.
        /// <paramref name="due"/> is the moment the current frame started showing; it is moved
        /// forward past <paramref name="now"/>. Returns the frame to show now.
        /// </summary>
        public static int AdvanceByTime(int frame, int[] delaysMs, ref float due, float now, int maxSteps)
        {
            if (delaysMs == null || delaysMs.Length <= 1)
            {
                return 0;
            }

            int count = delaysMs.Length;
            int current = ((frame % count) + count) % count;

            for (int step = 0; step < maxSteps && now >= due; step++)
            {
                int delay = delaysMs[current];
                if (delay < 20)
                {
                    delay = 20;
                }
                else if (delay > 1000)
                {
                    delay = 1000;
                }

                due += delay / 1000f;
                current = (current + 1) % count;
            }

            return current;
        }

        /// <summary>The frame to show after <paramref name="frame"/>.</summary>
        public static int Advance(int frame, int frameCount)
        {
            if (frameCount <= 1)
            {
                return 0;
            }

            return ((frame % frameCount) + frameCount + 1) % frameCount;
        }
    }
}
