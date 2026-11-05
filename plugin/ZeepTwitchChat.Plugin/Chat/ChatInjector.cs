using System;
using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ZeepTwitchChat.Chat
{
    /// <summary>
    /// Pushes formatted lines into the game's own chat window.
    ///
    /// The chat UI (<c>OnlineChatUI</c>) only renders lines that have been placed in its
    /// internal twenty-entry buffer, which is refilled by the private
    /// <c>UpdateChatFields(string, ulong)</c> method. That method is invoked through a
    /// Harmony <see cref="MethodInvoker"/> rather than ZeepSDK's <c>ChatApi</c>, because
    /// the game build ships that method as <c>private</c> while ZeepSDK's binding calls it
    /// as if it were public (a mismatch that can throw <c>MethodAccessException</c>).
    ///
    /// A steam id of 0 is passed so the line is rendered without the clickable Steam
    /// profile link the game adds to real lobby messages.
    /// </summary>
    public sealed class ChatInjector
    {
        private const int MaxQueuedLines = 40;

        private readonly Action<string> log;
        private readonly MessageRateLimiter limiter;
        private readonly ConcurrentQueue<string> pending = new ConcurrentQueue<string>();

        private MethodInfo updateChatFields;
        private FastInvokeHandler fastInvoker;
        private bool methodResolutionFailed;
        private string lastDropReason;
        private float lastDropLogTime;
        private int droppedSinceLog;

        public ChatInjector(MessageRateLimiter limiter, Action<string> log)
        {
            this.limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
            this.log = log ?? delegate { };
        }

        /// <summary>
        /// The live chat UI, captured by <see cref="Patches.OnlineChatUIAwakePatch"/>.
        /// Null (or Unity-destroyed) whenever no online lobby scene is loaded.
        /// </summary>
        public static OnlineChatUI Instance { get; set; }

        /// <summary>Lines waiting to be written, for diagnostics.</summary>
        public int Pending => pending.Count;

        /// <summary>Safe to call from the IRC background thread.</summary>
        public void Enqueue(string richLine)
        {
            if (string.IsNullOrEmpty(richLine))
            {
                return;
            }

            pending.Enqueue(richLine);

            // Never let a busy channel build an unbounded backlog: drop the oldest lines.
            while (pending.Count > MaxQueuedLines && pending.TryDequeue(out _))
            {
            }
        }

        /// <summary>Called once per frame from <c>Plugin.Update</c>; must run on the main thread.</summary>
        public void Drain()
        {
            if (pending.IsEmpty)
            {
                return;
            }

            OnlineChatUI chatUi = ResolveChatUi();
            if (chatUi == null)
            {
                DropQueued("no chat window (not in an online lobby)");
                return;
            }

            while (!pending.IsEmpty)
            {
                if (!limiter.TryAcquire())
                {
                    // Rate limited: discard now rather than replaying stale chat later.
                    DropQueued("rate limit reached");
                    return;
                }

                if (!pending.TryDequeue(out string line))
                {
                    return;
                }

                if (!Write(chatUi, line))
                {
                    return;
                }
            }
        }

        private bool Write(OnlineChatUI chatUi, string line)
        {
            if (!ResolveChatMethod())
            {
                return false;
            }

            try
            {
                if (fastInvoker != null)
                {
                    try
                    {
                        fastInvoker(chatUi, line, 0UL);
                        return true;
                    }
                    catch (Exception invokerFailure)
                    {
                        // Degrade to reflection instead of giving up: the emitted delegate can
                        // fail for reasons that do not affect MethodInfo.Invoke.
                        fastInvoker = null;
                        Exception invokerRoot = Unwrap(invokerFailure);
                        log("Twitch Chat: falling back to reflection for the chat window (" +
                            invokerRoot.GetType().Name + ": " + invokerRoot.Message + ").");
                    }
                }

                // Reflection is slower than the emitted delegate but perfectly adequate for a
                // few dozen lines a minute, and it does not depend on Harmony's dynamic
                // method support.
                updateChatFields.Invoke(chatUi, new object[] { line, 0UL });
                return true;
            }
            catch (Exception exception)
            {
                Exception root = Unwrap(exception);

                methodResolutionFailed = true;
                log("Twitch Chat: could not write to the chat window (" +
                    root.GetType().Name + ": " + root.Message + "). " +
                    "In-game Twitch chat is disabled until the next lobby.");
                return false;
            }
        }

        private static Exception Unwrap(Exception exception)
        {
            return exception is TargetInvocationException && exception.InnerException != null
                ? exception.InnerException
                : exception;
        }

        /// <summary>
        /// Resolves the private chat method once, preferring Harmony's emitted invoker and
        /// falling back to plain reflection when the invoker cannot be created.
        /// </summary>
        private bool ResolveChatMethod()
        {
            if (updateChatFields != null)
            {
                return true;
            }

            if (methodResolutionFailed)
            {
                return false;
            }

            try
            {
                updateChatFields = AccessTools.Method(
                    typeof(OnlineChatUI),
                    "UpdateChatFields",
                    new[] { typeof(string), typeof(ulong) });

                if (updateChatFields == null)
                {
                    methodResolutionFailed = true;
                    log("Twitch Chat: OnlineChatUI.UpdateChatFields(string, ulong) was not found in " +
                        "this game build; in-game Twitch chat is unavailable.");
                    return false;
                }

                try
                {
                    fastInvoker = MethodInvoker.GetHandler(updateChatFields);
                }
                catch (Exception exception)
                {
                    // Not fatal: reflection below does the same job.
                    fastInvoker = null;
                    log("Twitch Chat: using reflection for the chat window (" +
                        exception.GetType().Name + "); this is expected to be rare.");
                }

                return true;
            }
            catch (Exception exception)
            {
                methodResolutionFailed = true;
                log("Twitch Chat: could not bind to the chat window (" +
                    exception.GetType().Name + ": " + exception.Message + ").");
                return false;
            }
        }

        private static OnlineChatUI ResolveChatUi()
        {
            OnlineChatUI instance = Instance;
            if (instance != null)
            {
                return instance;
            }

            // The scene may have loaded before this plugin's patch was applied, so fall
            // back to a search: first live objects, then everything including inactive ones.
            OnlineChatUI found = UnityEngine.Object.FindObjectOfType<OnlineChatUI>();
            if (found == null)
            {
                OnlineChatUI[] all = Resources.FindObjectsOfTypeAll<OnlineChatUI>();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && all[i].gameObject.scene.IsValid())
                    {
                        found = all[i];
                        break;
                    }
                }
            }

            if (found != null)
            {
                Instance = found;
            }

            return found;
        }

        private void DropQueued(string reason)
        {
            int dropped = 0;
            while (pending.TryDequeue(out _))
            {
                dropped++;
            }

            if (dropped == 0)
            {
                return;
            }

            droppedSinceLog += dropped;
            lastDropReason = reason;

            float now = Time.realtimeSinceStartup;
            if (now - lastDropLogTime < 30f)
            {
                return;
            }

            lastDropLogTime = now;
            log("Twitch Chat: skipped " + droppedSinceLog + " message(s): " + lastDropReason +
                " (join an online lobby to see Twitch chat in game).");
            droppedSinceLog = 0;
        }
    }
}
