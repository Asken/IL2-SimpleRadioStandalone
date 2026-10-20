using System;
using System.Collections.Generic;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Integrations
{
    /// <summary>
    /// Keeps each webhook under a number of requests per minute (Discord allows about 30 per channel), so a burst
    /// of events waits instead of being rejected.
    /// </summary>
    public sealed class WebhookRateLimiter
    {
        public const int DefaultPerMinute = 30;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        private readonly Dictionary<string, Queue<DateTime>> _sent = new(StringComparer.Ordinal);
        private readonly Func<DateTime> _clock;
        private readonly int _perMinute;

        public WebhookRateLimiter() : this(DefaultPerMinute, () => DateTime.UtcNow)
        {
        }

        internal WebhookRateLimiter(int perMinute, Func<DateTime> clock)
        {
            _perMinute = perMinute;
            _clock = clock;
        }

        /// <summary>Takes a slot for the webhook; returns null when it may send now, otherwise how long to wait.</summary>
        public TimeSpan? TryAcquire(string webhook)
        {
            var now = _clock();
            lock (_sent)
            {
                if (!_sent.TryGetValue(webhook, out var sent))
                {
                    sent = new Queue<DateTime>();
                    _sent[webhook] = sent;
                }

                while (sent.Count > 0 && now - sent.Peek() >= Window)
                {
                    sent.Dequeue();
                }

                if (sent.Count >= _perMinute)
                {
                    return sent.Peek() + Window - now;
                }

                sent.Enqueue(now);
                return null;
            }
        }
    }
}
