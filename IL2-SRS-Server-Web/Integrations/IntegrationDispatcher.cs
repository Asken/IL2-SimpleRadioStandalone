using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Audit;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Events;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Network;
using Microsoft.Extensions.Hosting;
using NLog;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Integrations
{
    /// <summary>
    /// Matches published events against enabled integrations, queues a run for each match, and works
    /// through the queue: one run at a time per integration (so a Discord channel sees events in order),
    /// up to <see cref="MaxConcurrentRuns"/> integrations at once, with retries as configured.
    /// Queued runs survive restarts; runs interrupted by a shutdown are queued again at startup.
    /// </summary>
    public sealed class IntegrationDispatcher : BackgroundService
    {
        public const int MaxConcurrentRuns = 4;

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(1);

        private readonly IntegrationStore _store;
        private readonly AuditRetention _retention;
        private readonly Dictionary<string, IIntegrationAction> _actions;
        private readonly Channel<SrsEvent> _incoming = Channel.CreateBounded<SrsEvent>(
            new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        private readonly ConcurrentDictionary<long, Task> _running = new();

        private volatile IReadOnlyList<IntegrationDefinition> _integrations;
        private int _droppedEvents;

        public IntegrationDispatcher(IntegrationStore store, ServerEvents events, IEnumerable<IIntegrationAction> actions,
            AuditRetention retention)
        {
            _store = store;
            _retention = retention;
            _actions = actions.ToDictionary(a => a.Type, StringComparer.Ordinal);
            events.EventPublished += OnEventPublished;
            store.IntegrationsChanged += () => _integrations = null;
            store.ExecutionsChanged += Wake;
        }

        private void OnEventPublished(SrsEvent srsEvent)
        {
            if (!_incoming.Writer.TryWrite(srsEvent) && Interlocked.Increment(ref _droppedEvents) == 1)
            {
                Logger.Warn("Integration event queue is full; events are not being sent to integrations.");
            }
        }

        private void Wake() => _wake.Writer.TryWrite(true);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var requeued = _store.ResetRunning();
            if (requeued > 0)
            {
                Logger.Info($"Queued {requeued} integration run(s) interrupted by the last shutdown again");
            }

            await Task.WhenAll(MatchLoop(stoppingToken), DispatchLoop(stoppingToken));
        }

        /// <summary>Queues a run for every enabled integration whose trigger and conditions match the event.</summary>
        internal void Match(SrsEvent srsEvent)
        {
            var integrations = _integrations ??= _store.List().Where(i => i.Enabled).ToList();
            JsonElement? data = null;
            foreach (var integration in integrations)
            {
                if (!integration.IsTriggeredBy(srsEvent.Type))
                {
                    continue;
                }

                data ??= EventData.From(srsEvent);
                if (EventData.Matches(data.Value, integration.Conditions))
                {
                    _store.Enqueue(integration, srsEvent.Type, srsEvent.Message, data.Value);
                }
            }
        }

        private async Task MatchLoop(CancellationToken stoppingToken)
        {
            try
            {
                await foreach (var srsEvent in _incoming.Reader.ReadAllAsync(stoppingToken))
                {
                    Interlocked.Exchange(ref _droppedEvents, 0);
                    try
                    {
                        Match(srsEvent);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, $"Unable to queue integrations for {srsEvent.Type}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task DispatchLoop(CancellationToken stoppingToken)
        {
            var nextRetention = DateTime.UtcNow;
            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan wait;
                try
                {
                    StartDueRuns(stoppingToken);
                    if (DateTime.UtcNow >= nextRetention)
                    {
                        nextRetention = DateTime.UtcNow + RetentionInterval;
                        _store.ApplyRetention(DateTime.UtcNow, _retention.Days, _retention.MaxRows);
                    }

                    var nextDue = _store.NextDueAt();
                    wait = nextDue.HasValue ? nextDue.Value - DateTime.UtcNow : IdleWait;
                    wait = wait < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50)
                        : wait > IdleWait ? IdleWait : wait;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Integration dispatcher failed; trying again shortly");
                    wait = TimeSpan.FromSeconds(5);
                }

                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(wait);
                    await _wake.Reader.WaitToReadAsync(timeout.Token);
                    _wake.Reader.TryRead(out _);
                }
                catch (OperationCanceledException)
                {
                }
            }

            await Task.WhenAll(_running.Values.ToArray());
        }

        /// <summary>Starts the oldest due run of each integration that is not already running.</summary>
        private void StartDueRuns(CancellationToken stoppingToken)
        {
            if (_running.Count >= MaxConcurrentRuns)
            {
                return;
            }

            foreach (var execution in _store.Due(DateTime.UtcNow, 100))
            {
                if (_running.Count >= MaxConcurrentRuns)
                {
                    return;
                }

                if (_running.ContainsKey(execution.IntegrationId) || !_store.TryStart(execution.Id))
                {
                    continue;
                }

                var started = new TaskCompletionSource();
                _running[execution.IntegrationId] = started.Task;
                _ = RunTracked(execution, started, stoppingToken);
            }
        }

        private async Task RunTracked(IntegrationExecution execution, TaskCompletionSource done,
            CancellationToken stoppingToken)
        {
            try
            {
                await RunAsync(execution, stoppingToken);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Integration run {execution.Id} failed unexpectedly");
            }
            finally
            {
                _running.TryRemove(execution.IntegrationId, out _);
                done.TrySetResult();
                Wake();
            }
        }

        internal async Task RunAsync(IntegrationExecution execution, CancellationToken stoppingToken)
        {
            var attempt = execution.Attempts + 1;
            var integration = _store.GetForExecution(execution.IntegrationId);
            if (integration == null)
            {
                _store.Finish(execution.Id, ExecutionStatus.Skipped, "Integration deleted", null, null);
                return;
            }

            if (!integration.Enabled)
            {
                _store.Finish(execution.Id, ExecutionStatus.Skipped, "Integration disabled", null, null);
                return;
            }

            if (!_actions.TryGetValue(integration.ActionType, out var action))
            {
                _store.Finish(execution.Id, ExecutionStatus.Failed,
                    $"The action \"{integration.ActionType}\" is not available on this server.", null, null);
                return;
            }

            using var data = JsonDocument.Parse(execution.Data);
            var context = new IntegrationExecutionContext
            {
                ExecutionId = execution.Id,
                Integration = integration,
                Config = integration.ActionConfig,
                EventType = SrsEventTypes.Find(execution.EventType),
                Data = data.RootElement,
                Attempt = attempt,
                State = _store.State(integration.Id)
            };

            var result = await IntegrationService.RunAsync(action, context, stoppingToken);
            if (stoppingToken.IsCancellationRequested && !result.IsSuccess)
            {
                // Shutting down: leave it running so it is queued again at the next start.
                return;
            }

            if (result.IsSuccess)
            {
                _store.Finish(execution.Id, ExecutionStatus.Succeeded, null, result.Message, null);
                return;
            }

            if (!result.CountsAsAttempt)
            {
                var wait = result.RetryAfter ?? TimeSpan.FromSeconds(10);
                Logger.Info($"Integration \"{integration.Name}\" waits {wait.TotalSeconds:0} s: {result.Message}");
                _store.Defer(execution.Id, result.Message, DateTime.UtcNow + wait);
                return;
            }

            var retry = result.Status == ExecutionStatus.Queued && integration.RetryMode != RetryMode.None &&
                        attempt <= integration.MaxRetries;
            if (retry)
            {
                var delay = result.RetryAfter ?? integration.RetryDelay(attempt);
                Logger.Warn($"Integration \"{integration.Name}\" failed (attempt {attempt}), retrying in " +
                            $"{delay.TotalSeconds:0} s: {result.Message}");
                _store.Finish(execution.Id, ExecutionStatus.Queued, result.Message, null, DateTime.UtcNow + delay);
            }
            else
            {
                Logger.Warn($"Integration \"{integration.Name}\" failed: {result.Message}");
                _store.Finish(execution.Id, ExecutionStatus.Failed, result.Message, null, null);
            }
        }
    }
}
