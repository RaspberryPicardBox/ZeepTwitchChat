using System;
using System.Collections.Generic;

namespace ZeepTwitchChat.Chat
{
    /// <summary>
    /// What the mod knows about the lines it has put on screen, so a deletion can be acted on.
    ///
    /// Twitch signals deletions with <c>CLEARMSG</c> (one message, by id) and <c>CLEARCHAT</c>
    /// (everything from one user, or the whole chat). A line may still be waiting in the
    /// deferred queue at that moment, or already be in the game's twenty line buffer, so the
    /// ledger tracks both states: a pending line is simply never posted, a posted one is handed
    /// back to the main thread to be erased.
    ///
    /// Thread safe on purpose: the IRC thread records and cancels, the main thread confirms
    /// posts and takes removals.
    /// </summary>
    public sealed class ChatLineLedger
    {
        /// <summary>Lines remembered at once; older ones have long since scrolled away.</summary>
        private const int MaxEntries = 128;

        private readonly object gate = new object();
        private readonly List<Entry> entries = new List<Entry>(MaxEntries);

        /// <summary>Rendered lines the main thread still has to erase from the chat window.</summary>
        private readonly List<string> removals = new List<string>();

        /// <summary>Number of lines currently remembered (diagnostics).</summary>
        public int Count
        {
            get
            {
                lock (gate)
                {
                    return entries.Count;
                }
            }
        }

        /// <summary>Number of rendered lines waiting to be erased.</summary>
        public int PendingRemovals
        {
            get
            {
                lock (gate)
                {
                    return removals.Count;
                }
            }
        }

        /// <summary>
        /// Registers a line that is about to be queued. Called on the IRC thread before the line
        /// reaches the injector, so a deletion that overtakes it can still stop it. The rendered
        /// text is filled in later: an emote may finish downloading while the line waits, which
        /// changes the string that is actually shown.
        /// </summary>
        public void RecordPending(string messageId, string login)
        {

            lock (gate)
            {
                TrimLocked();

                entries.Add(new Entry
                {
                    MessageId = messageId ?? string.Empty,
                    Login = login ?? string.Empty,
                    Rendered = null,
                    Posted = false,
                    Cancelled = false,
                    Erased = false
                });
            }
        }

        /// <summary>
        /// Called by the injector with the final string, immediately before writing it. Returns
        /// false when the line was cancelled while it was queued and must not be shown at all.
        /// </summary>
        public bool TryPost(string messageId, string rendered)
        {
            lock (gate)
            {
                Entry entry = FindLocked(messageId, rendered);
                if (entry == null)
                {
                    return true;
                }

                if (entry.Cancelled)
                {
                    return false;
                }

                entry.Posted = true;
                entry.Rendered = rendered;
                return true;
            }
        }

        /// <summary>
        /// Handles <c>CLEARMSG</c>: one message, by id. A posted line is queued for erasing, a
        /// pending one is cancelled. Returns true when the ledger knew about it.
        /// </summary>
        public ModerationOutcome MessageDeleted(string messageId)
        {
            if (string.IsNullOrEmpty(messageId))
            {
                return ModerationOutcome.Nothing;
            }

            lock (gate)
            {
                Entry entry = FindByIdLocked(messageId);
                return entry == null ? ModerationOutcome.Nothing : CancelOrEraseLocked(entry);
            }
        }

        /// <summary>
        /// Handles <c>CLEARCHAT</c>: every line from one login, or the whole chat when
        /// <paramref name="login"/> is empty. Returns the number of lines affected.
        /// </summary>
        public ModerationOutcome UserCleared(string login)
        {
            bool all = string.IsNullOrEmpty(login);

            lock (gate)
            {
                ModerationOutcome outcome = ModerationOutcome.Nothing;

                for (int i = 0; i < entries.Count; i++)
                {
                    Entry entry = entries[i];

                    if (!all && !string.Equals(entry.Login, login, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (entry.Erased)
                    {
                        continue;
                    }

                    outcome = outcome.Plus(CancelOrEraseLocked(entry));
                }

                return outcome;
            }
        }

        /// <summary>
        /// Takes the rendered lines the main thread should erase, and forgets them. Called from
        /// <c>Plugin.Update</c>, which is the only place allowed to touch the game's chat buffer.
        /// </summary>
        public List<string> TakeRemovals()
        {
            lock (gate)
            {
                if (removals.Count == 0)
                {
                    return null;
                }

                List<string> taken = new List<string>(removals);
                removals.Clear();
                return taken;
            }
        }

        /// <summary>Marks a line cancelled (never shown) or erased (shown, needs removing).</summary>
        private ModerationOutcome CancelOrEraseLocked(Entry entry)
        {
            if (!entry.Posted)
            {
                if (entry.Cancelled)
                {
                    return ModerationOutcome.Nothing;
                }

                entry.Cancelled = true;
                return new ModerationOutcome(1, 0);
            }

            if (entry.Erased)
            {
                return ModerationOutcome.Nothing;
            }

            entry.Erased = true;
            removals.Add(entry.Rendered);
            return new ModerationOutcome(0, 1);
        }

        private Entry FindLocked(string messageId, string rendered)
        {
            if (!string.IsNullOrEmpty(messageId))
            {
                Entry byId = FindByIdLocked(messageId);
                if (byId != null)
                {
                    return byId;
                }
            }

            if (string.IsNullOrEmpty(rendered))
            {
                return null;
            }

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (string.Equals(entries[i].Rendered, rendered, StringComparison.Ordinal))
                {
                    return entries[i];
                }
            }

            return null;
        }

        private Entry FindByIdLocked(string messageId)
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (string.Equals(entries[i].MessageId, messageId, StringComparison.Ordinal))
                {
                    return entries[i];
                }
            }

            return null;
        }

        /// <summary>Forgets everything, for a reconnect or a scene change.</summary>
        public void Clear()
        {
            lock (gate)
            {
                entries.Clear();
                removals.Clear();
            }
        }

        /// <summary>Keeps the ledger bounded, dropping the oldest entries first.</summary>
        private void TrimLocked()
        {
            while (entries.Count >= MaxEntries)
            {
                entries.RemoveAt(0);
            }
        }

        private sealed class Entry
        {
            public string MessageId;
            public string Login;
            public string Rendered;
            public bool Posted;
            public bool Cancelled;
            public bool Erased;
        }
    }

    /// <summary>
    /// What one moderation event did: how many queued lines were cancelled before they could
    /// appear, and how many lines on screen have to be erased. Used for the audit log.
    /// </summary>
    public struct ModerationOutcome
    {
        public static readonly ModerationOutcome Nothing = new ModerationOutcome(0, 0);

        public ModerationOutcome(int cancelled, int toErase)
        {
            Cancelled = cancelled;
            ToErase = toErase;
        }

        /// <summary>Lines that will never be shown because they were still queued.</summary>
        public int Cancelled { get; }

        /// <summary>Lines on screen that have to be removed.</summary>
        public int ToErase { get; }

        /// <summary>True when the moderation event matched anything this mod had mirrored.</summary>
        public bool Known
        {
            get { return Cancelled > 0 || ToErase > 0; }
        }

        public ModerationOutcome Plus(ModerationOutcome other)
        {
            return new ModerationOutcome(Cancelled + other.Cancelled, ToErase + other.ToErase);
        }

        public override string ToString()
        {
            return Cancelled + " queued / " + ToErase + " on screen";
        }
    }
}
