using System;
using System.Collections.Generic;

namespace ZeepTwitchChat.Chat
{
    /// <summary>
    /// Sliding-window rate limiter, so a fast Twitch chat cannot flood the in-game
    /// chat (which only holds twenty lines and shifts on every new message).
    ///
    /// Pure logic with an injectable clock, so it is unit tested without the game.
    /// </summary>
    public sealed class MessageRateLimiter
    {
        private readonly int maxMessagesPerWindow;
        private readonly double windowSeconds;
        private readonly double minimumSecondsBetween;
        private readonly Func<double> clock;

        private readonly Queue<double> recent = new Queue<double>();
        private double lastMessageTime = double.NegativeInfinity;

        /// <param name="maxMessagesPerWindow">Messages allowed per window; 0 or less disables the window.</param>
        /// <param name="windowSeconds">Window length in seconds (60 = per minute).</param>
        /// <param name="minimumSecondsBetween">Hard floor between two messages; 0 disables it.</param>
        /// <param name="clock">Monotonic time source in seconds; defaults to a process clock.</param>
        public MessageRateLimiter(
            int maxMessagesPerWindow,
            double windowSeconds = 60d,
            double minimumSecondsBetween = 0d,
            Func<double> clock = null)
        {
            this.maxMessagesPerWindow = maxMessagesPerWindow;
            this.windowSeconds = windowSeconds > 0 ? windowSeconds : 60d;
            this.minimumSecondsBetween = minimumSecondsBetween;
            this.clock = clock ?? DefaultClock;
        }

        private static double DefaultClock()
        {
            return Environment.TickCount / 1000d;
        }

        /// <summary>
        /// True when a message may be shown now. A successful call consumes a slot,
        /// so call this only when the message is actually going to be displayed.
        /// </summary>
        public bool TryAcquire()
        {
            double now = clock();

            if (minimumSecondsBetween > 0 && now - lastMessageTime < minimumSecondsBetween)
            {
                return false;
            }

            if (maxMessagesPerWindow > 0)
            {
                while (recent.Count > 0 && now - recent.Peek() >= windowSeconds)
                {
                    recent.Dequeue();
                }

                if (recent.Count >= maxMessagesPerWindow)
                {
                    return false;
                }
            }

            recent.Enqueue(now);
            lastMessageTime = now;
            return true;
        }

        /// <summary>Number of messages currently counted inside the window.</summary>
        public int CountInWindow()
        {
            double now = clock();
            while (recent.Count > 0 && now - recent.Peek() >= windowSeconds)
            {
                recent.Dequeue();
            }

            return recent.Count;
        }
    }
}
