using System;
using System.Collections.Generic;

namespace ZeepTwitchChat.Chat
{
    /// <summary>
    /// Works out which entries of the game's chat buffer a removal applies to.
    ///
    /// <c>OnlineChatUI.receivedChatList</c> holds rendered lines newest first and is capped at
    /// twenty; <c>receivedChatIDs</c> holds the matching steam ids. Erasing one line means
    /// dropping an entry from both lists and re-rendering, so the indices have to be right
    /// first. Pure logic, unit tested, because the alternative is discovering an off-by-one by
    /// watching a real chat window.
    /// </summary>
    public static class ChatBufferPlan
    {
        /// <summary>
        /// Indices to drop, ascending. Each requested line removes at most one entry: the first
        /// unclaimed exact match, searching newest first. Anything not found is skipped, and
        /// entries this mod did not add are never touched - the caller only ever asks for
        /// strings it recorded when it posted them.
        /// </summary>
        public static List<int> Removals(IReadOnlyList<string> entries, IReadOnlyList<string> lines)
        {
            List<int> removals = new List<int>();

            if (entries == null || lines == null || entries.Count == 0 || lines.Count == 0)
            {
                return removals;
            }

            List<int> claimed = new List<int>();

            for (int i = 0; i < lines.Count; i++)
            {
                string wanted = lines[i];
                if (string.IsNullOrEmpty(wanted))
                {
                    continue;
                }

                for (int index = 0; index < entries.Count; index++)
                {
                    if (claimed.Contains(index))
                    {
                        continue;
                    }

                    if (!string.Equals(entries[index], wanted, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    claimed.Add(index);
                    break;
                }
            }

            claimed.Sort();
            return claimed;
        }
    }
}
