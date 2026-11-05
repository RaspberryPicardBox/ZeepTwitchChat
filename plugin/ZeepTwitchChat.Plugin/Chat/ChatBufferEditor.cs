using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using ZeepTwitchChat.Chat.Emotes;

namespace ZeepTwitchChat.Chat
{
    /// <summary>
    /// Removes lines from the game's own chat buffer.
    ///
    /// <c>OnlineChatUI</c> keeps its chat as <c>receivedChatList</c> (rendered lines, newest
    /// first) and <c>receivedChatIDs</c> (steam ids), capped at twenty, and
    /// <c>UpdateChatFields</c> inserts one entry and then rebuilds both text fields from those
    /// lists. So erasing a line is: drop the entry from both lists, then replay the survivors
    /// through the game's own method oldest first. Replaying rather than re-implementing means
    /// the font size, the Steam link markup and the twenty line cap stay the game's business.
    ///
    /// Only lines this mod posted are ever named here, so lobby chat is never touched.
    /// </summary>
    public static class ChatBufferEditor
    {
        private static AccessTools.FieldRef<OnlineChatUI, List<string>> listRef;
        private static AccessTools.FieldRef<OnlineChatUI, List<ulong>> idsRef;

        /// <summary>
        /// Erases the given rendered lines. Returns how many buffer entries were removed, or 0
        /// when nothing matched. Main thread only.
        /// </summary>
        public static int Remove(OnlineChatUI chatUi, ChatInjector injector,
            IReadOnlyList<string> renderedLines, Action<string> log)
        {
            if (chatUi == null || injector == null || renderedLines == null || renderedLines.Count == 0)
            {
                return 0;
            }

            try
            {
                List<string> lines = GetList(chatUi);
                List<ulong> ids = GetIds(chatUi);

                if (lines == null || ids == null || lines.Count == 0)
                {
                    return 0;
                }

                int count = Math.Min(lines.Count, ids.Count);
                List<int> removals = ChatBufferPlan.Removals(
                    lines.Count == count ? lines : lines.GetRange(0, count), renderedLines);

                if (removals.Count == 0)
                {
                    return 0;
                }

                // Capture what is going away while the entries are still readable.
                List<string> removed = new List<string>(removals.Count);
                for (int i = 0; i < removals.Count; i++)
                {
                    removed.Add(lines[removals[i]]);
                }

                // Keep what survives, then rebuild through the game's own append.
                List<KeyValuePair<string, ulong>> keep = new List<KeyValuePair<string, ulong>>(count);
                for (int i = 0; i < count; i++)
                {
                    if (!removals.Contains(i))
                    {
                        keep.Add(new KeyValuePair<string, ulong>(lines[i], ids[i]));
                    }
                }

                List<string> before = new List<string>(lines);
                List<ulong> beforeIds = new List<ulong>(ids);

                lines.Clear();
                ids.Clear();

                try
                {
                    // Oldest first: the game inserts at index 0, so this restores the order.
                    for (int i = keep.Count - 1; i >= 0; i--)
                    {
                        injector.Append(chatUi, keep[i].Key, keep[i].Value);
                    }
                }
                catch (Exception replay)
                {
                    // Never leave the game with an empty buffer: put the original entries back so
                    // the next message still rebuilds a correct window.
                    Restore(lines, ids, before, beforeIds);
                    throw new InvalidOperationException("replaying the chat buffer failed", replay);
                }

                if (keep.Count == 0)
                {
                    // Nothing to replay, so the fields would keep their old text.
                    ClearFields(chatUi);
                }

                if (log != null)
                {
                    for (int i = 0; i < removed.Count; i++)
                    {
                        log("Twitch Chat: erased a line the moderators removed: " +
                            ChatLineFormatter.SanitiseText(removed[i], 60));
                    }
                }

                return removals.Count;
            }
            catch (Exception exception)
            {
                Exception root = exception is TargetInvocationException && exception.InnerException != null
                    ? exception.InnerException
                    : exception;

                if (log != null)
                {
                    log("Twitch Chat: could not erase a deleted line (" + root.GetType().Name +
                        ": " + root.Message + "); the line stays until it scrolls away.");
                }

                return 0;
            }
        }

        /// <summary>Puts the buffer back the way it was found (newest first, so insert in reverse).</summary>
        private static void Restore(List<string> lines, List<ulong> ids,
            List<string> before, List<ulong> beforeIds)
        {
            lines.Clear();
            ids.Clear();

            for (int i = before.Count - 1; i >= 0; i--)
            {
                lines.Insert(0, before[i]);
                ids.Insert(0, i < beforeIds.Count ? beforeIds[i] : 0UL);
            }
        }

        private static void ClearFields(OnlineChatUI chatUi)
        {
            TextMeshProUGUI big = ChatTextTargets.GetLogField(chatUi, false);
            TextMeshProUGUI small = ChatTextTargets.GetLogField(chatUi, true);

            if (big != null)
            {
                big.text = string.Empty;
            }

            if (small != null)
            {
                small.text = string.Empty;
            }
        }

        private static List<string> GetList(OnlineChatUI chatUi)
        {
            if (listRef == null)
            {
                listRef = AccessTools.FieldRefAccess<OnlineChatUI, List<string>>("receivedChatList");
            }

            return listRef(chatUi);
        }

        private static List<ulong> GetIds(OnlineChatUI chatUi)
        {
            if (idsRef == null)
            {
                idsRef = AccessTools.FieldRefAccess<OnlineChatUI, List<ulong>>("receivedChatIDs");
            }

            return idsRef(chatUi);
        }
    }
}
