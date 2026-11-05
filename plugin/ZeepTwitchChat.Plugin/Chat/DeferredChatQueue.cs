using System;
using System.Collections.Generic;
using ZeepTwitchChat.Twitch;

namespace ZeepTwitchChat.Chat
{
    /// <summary>
    /// Holds chat lines that contain emotes which are still downloading, so the first time a
    /// chatter uses an emote it can render as an image instead of its text name.
    ///
    /// Why this is necessary: the game stores the *rendered* string in its twenty-line buffer,
    /// so a line written before its emote arrived keeps the text name for as long as it is on
    /// screen. Waiting a moment is the only way to get the image on first use.
    ///
    /// A line is released as soon as every emote in it is drawable, or when the wait budget
    /// expires (so a broken provider can never hold chat hostage). Non-emote lines never enter
    /// this queue.
    /// </summary>
    public sealed class DeferredChatQueue
    {
        private const int MaxQueuedLines = 20;

        private readonly object gate = new object();
        private readonly List<PendingLine> lines = new List<PendingLine>();
        private readonly float waitSeconds;

        public DeferredChatQueue(float waitSeconds)
        {
            this.waitSeconds = waitSeconds < 0f ? 0f : waitSeconds;
        }

        /// <summary>A chat line waiting for its emote images.</summary>
        public sealed class PendingLine
        {
            public TwitchChatUser User;
            public string Text;
            public IReadOnlyList<EmoteSpan> Spans;
            public double AddedAt;

            /// <summary>Twitch's id for the message, so a deletion can cancel it.</summary>
            public string MessageId;
        }

        public int Count
        {
            get
            {
                lock (gate)
                {
                    return lines.Count;
                }
            }
        }

        /// <summary>Called from the IRC thread.</summary>
        public void Add(TwitchChatUser user, string text, IReadOnlyList<EmoteSpan> spans, double now)
        {
            Add(user, text, spans, now, null);
        }

        /// <summary>Called from the IRC thread; the id lets a deletion cancel the line.</summary>
        public void Add(TwitchChatUser user, string text, IReadOnlyList<EmoteSpan> spans, double now,
            string messageId)
        {
            lock (gate)
            {
                lines.Add(new PendingLine
                {
                    User = user,
                    Text = text,
                    Spans = spans,
                    AddedAt = now,
                    MessageId = messageId
                });

                // Never build an unbounded backlog: the oldest lines fall through immediately.
                while (lines.Count > MaxQueuedLines)
                {
                    lines.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// Drops lines a moderator removed before they could be shown: by message id, by login,
        /// or everything when both are empty. Returns how many went.
        /// </summary>
        public int Remove(string messageId, string login)
        {
            bool all = string.IsNullOrEmpty(messageId) && string.IsNullOrEmpty(login);

            lock (gate)
            {
                int removed = 0;

                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    PendingLine line = lines[i];
                    bool match = all ||
                        (!string.IsNullOrEmpty(messageId) &&
                         string.Equals(line.MessageId, messageId, StringComparison.Ordinal)) ||
                        (!string.IsNullOrEmpty(login) && line.User != null &&
                         string.Equals(line.User.Login, login, StringComparison.OrdinalIgnoreCase));

                    if (match)
                    {
                        lines.RemoveAt(i);
                        removed++;
                    }
                }

                return removed;
            }
        }

        /// <summary>
        /// Moves every line that is ready (all emotes drawable, or out of patience) into
        /// <paramref name="ready"/>, oldest first so chat order is preserved.
        /// </summary>
        public void Take(double now, Func<string, bool> isRenderable, List<PendingLine> ready)
        {
            if (ready == null)
            {
                return;
            }

            lock (gate)
            {
                for (int i = 0; i < lines.Count;)
                {
                    PendingLine line = lines[i];
                    bool expired = now - line.AddedAt >= waitSeconds;

                    if (expired || AllEmotesReady(line.Spans, isRenderable))
                    {
                        ready.Add(line);
                        lines.RemoveAt(i);
                        continue;
                    }

                    i++;
                }
            }
        }

        /// <summary>Drops everything (used when the pipeline is rebuilt).</summary>
        public void Clear()
        {
            lock (gate)
            {
                lines.Clear();
            }
        }

        private static bool AllEmotesReady(IReadOnlyList<EmoteSpan> spans, Func<string, bool> isRenderable)
        {
            if (spans == null || isRenderable == null)
            {
                return true;
            }

            for (int i = 0; i < spans.Count; i++)
            {
                if (!isRenderable(spans[i].Name))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
