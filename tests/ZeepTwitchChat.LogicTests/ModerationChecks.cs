using System;
using System.Collections.Generic;
using ZeepTwitchChat.Chat;
using ZeepTwitchChat.Twitch;

namespace ZeepTwitchChat.LogicTests
{
    /// <summary>
    /// Deletions and bans: how Twitch reports them, what gets cancelled before it appears, and
    /// which chat buffer entries get removed. All pure, so the parts that decide what happens
    /// to a real chat window are tested without the game.
    /// </summary>
    internal static partial class Program
    {
        private static void ModerationChecks()
        {
            Console.WriteLine();
            Console.WriteLine("Deletions and bans");

            // --- how the IRC lines are read -------------------------------------------
            const string clearMsg =
                "@login=victim;room-id=1;target-msg-id=abc-123;tmi-sent-ts=1000 " +
                ":tmi.twitch.tv CLEARMSG #channel :hello there";
            const string ban =
                "@ban-duration=600;room-id=1;target-user-id=555;tmi-sent-ts=1000 " +
                ":tmi.twitch.tv CLEARCHAT #channel :victim";
            const string permanentBan =
                "@room-id=1;target-user-id=555;tmi-sent-ts=1000 " +
                ":tmi.twitch.tv CLEARCHAT #channel :victim";
            const string clearAll =
                "@room-id=1;tmi-sent-ts=1000 :tmi.twitch.tv CLEARCHAT #channel";

            Check("CLEARMSG names the message and the user",
                DescribeModeration(clearMsg) == "message abc-123 deleted (victim)",
                DescribeModeration(clearMsg));
            Check("a timeout carries its length",
                DescribeModeration(ban) == "removed (victim, timeout 600s)", DescribeModeration(ban));
            Check("a permanent ban has no length",
                DescribeModeration(permanentBan) == "removed (victim, banned)",
                DescribeModeration(permanentBan));
            Check("a CLEARCHAT with no target clears everything",
                DescribeModeration(clearAll) == "chat cleared", DescribeModeration(clearAll));
            Check("an ordinary message is not a moderation event",
                ModerationOf("@id=x :a!a@a PRIVMSG #c :hi") == null);
            Check("a JOIN is not a moderation event",
                ModerationOf(":a!a@a JOIN #c") == null);

            // --- the ledger ------------------------------------------------------------
            ChatLineLedger ledger = new ChatLineLedger();

            ledger.RecordPending("m1", "viewer");
            Check("a queued line can be posted", ledger.TryPost("m1", "line one"));

            ledger.RecordPending("m2", "viewer");
            ModerationOutcome pendingDelete = ledger.MessageDeleted("m2");
            Check("deleting a queued line cancels it",
                pendingDelete.Cancelled == 1 && pendingDelete.ToErase == 0);
            Check("a cancelled line is refused at post time", !ledger.TryPost("m2", "line two"));
            Check("a cancelled line never reaches the window", ledger.PendingRemovals == 0);

            ModerationOutcome postedDelete = ledger.MessageDeleted("m1");
            Check("deleting a posted line queues it for removal",
                postedDelete.Cancelled == 0 && postedDelete.ToErase == 1);

            List<string> removals = ledger.TakeRemovals();
            Check("the removal carries the rendered line",
                removals != null && removals.Count == 1 && removals[0] == "line one");
            Check("removals are handed over once", ledger.TakeRemovals() == null);
            Check("a second delete of the same line does nothing",
                !ledger.MessageDeleted("m1").Known);
            Check("an unknown message id is ignored",
                !ledger.MessageDeleted("never-seen").Known &&
                !ledger.MessageDeleted(null).Known &&
                !ledger.MessageDeleted(string.Empty).Known);

            // --- clearing a user -------------------------------------------------------
            ChatLineLedger userLedger = new ChatLineLedger();
            userLedger.RecordPending("a1", "Spammer");
            userLedger.TryPost("a1", "spam one");
            userLedger.RecordPending("a2", "spammer");
            userLedger.TryPost("a2", "spam two");
            userLedger.RecordPending("a3", "spammer");
            userLedger.RecordPending("b1", "Innocent");
            userLedger.TryPost("b1", "hello");

            ModerationOutcome cleared = userLedger.UserCleared("spammer");
            Check("a ban removes every line from that user, ignoring case",
                cleared.Cancelled == 1 && cleared.ToErase == 2, cleared.ToString());
            Check("other users are untouched",
                userLedger.TakeRemovals().Count == 2);

            ChatLineLedger chatWide = new ChatLineLedger();
            chatWide.RecordPending("c1", "one");
            chatWide.TryPost("c1", "first");
            chatWide.RecordPending("c2", "two");
            chatWide.TryPost("c2", "second");
            ModerationOutcome all = chatWide.UserCleared(null);
            Check("clearing the chat takes every mirrored line",
                all.ToErase == 2 && chatWide.TakeRemovals().Count == 2);

            ChatLineLedger bounded = new ChatLineLedger();
            for (int i = 0; i < 200; i++)
            {
                bounded.RecordPending("id" + i, "user");
                bounded.TryPost("id" + i, "line " + i);
            }

            Check("the ledger stays bounded", bounded.Count <= 128, bounded.Count.ToString());

            // --- choosing buffer entries ----------------------------------------------
            List<string> buffer = new List<string> { "newest", "middle", "oldest", "middle" };
            List<int> plan = ChatBufferPlan.Removals(buffer, new List<string> { "middle" });
            Check("one request removes one entry",
                plan.Count == 1 && plan[0] == 1, plan.Count.ToString());

            List<int> planTwo = ChatBufferPlan.Removals(buffer, new List<string> { "middle", "middle" });
            Check("duplicate lines are removed once each",
                planTwo.Count == 2 && planTwo[0] == 1 && planTwo[1] == 3,
                string.Join(",", planTwo));

            Check("a line that is not in the buffer is skipped",
                ChatBufferPlan.Removals(buffer, new List<string> { "absent" }).Count == 0);
            Check("lobby lines are never touched",
                ChatBufferPlan.Removals(buffer, new List<string> { "Player joined" }).Count == 0);
            Check("an empty request removes nothing",
                ChatBufferPlan.Removals(buffer, null).Count == 0 &&
                ChatBufferPlan.Removals(null, new List<string> { "middle" }).Count == 0);
        }

        private static string DescribeModeration(string rawLine)
        {
            ChatModerationEvent moderation = ModerationOf(rawLine);
            return moderation == null ? "none" : moderation.ToString();
        }

        private static ChatModerationEvent ModerationOf(string rawLine)
        {
            return TwitchIrcParser.TryParse(rawLine, out IrcMessage message)
                ? ChatModerationEvent.FromMessage(message)
                : null;
        }
    }
}
