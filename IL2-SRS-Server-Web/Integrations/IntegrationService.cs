using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Events;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Network;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Integrations
{
    /// <summary>The outcome of a test run from the integration page.</summary>
    public sealed record IntegrationTestResult(IntegrationResult Result, bool ConditionsMatchSample);

    /// <summary>
    /// What the admin UI and the API use to manage integrations: validation, saving (recorded in the event log)
    /// and test runs with example event data.
    /// </summary>
    public sealed class IntegrationService
    {
        public static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(30);

        private readonly ServerEvents _events;
        private readonly Dictionary<string, IIntegrationAction> _actions;

        public IntegrationService(IntegrationStore store, ServerEvents events, IEnumerable<IIntegrationAction> actions)
        {
            Store = store;
            _events = events;
            Actions = actions.OrderBy(a => a.Label, StringComparer.OrdinalIgnoreCase).ToList();
            _actions = Actions.ToDictionary(a => a.Type, StringComparer.Ordinal);
        }

        public IntegrationStore Store { get; }

        /// <summary>Registered action types, by label.</summary>
        public IReadOnlyList<IIntegrationAction> Actions { get; }

        public IIntegrationAction FindAction(string type) =>
            type != null && _actions.TryGetValue(type, out var action) ? action : null;

        /// <summary>Checks an integration before saving; returns an error message or null.</summary>
        public string Validate(IntegrationDefinition draft)
        {
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                return "Enter a name.";
            }

            if (draft.Name.Trim().Length > 100)
            {
                return "The name can be at most 100 characters.";
            }

            if (SrsEventTypes.Find(draft.TriggerType) == null)
            {
                return "Choose a trigger event.";
            }

            var action = FindAction(draft.ActionType);
            if (action == null)
            {
                return "Choose an action.";
            }

            foreach (var condition in draft.Conditions.Where(c => !string.IsNullOrWhiteSpace(c.Field)))
            {
                if (ConditionOperator.All.All(o => o.Value != condition.Operator))
                {
                    return $"Choose a valid comparison for {condition.Field}.";
                }
            }

            if (draft.RetryMode != RetryMode.None &&
                (draft.MaxRetries < 1 || draft.MaxRetries > IntegrationDefinition.MaxRetriesLimit))
            {
                return $"Max retries must be between 1 and {IntegrationDefinition.MaxRetriesLimit}.";
            }

            return action.Validate(WithStoredSecrets(draft));
        }

        /// <summary>Creates or updates an integration; returns an error message or null.</summary>
        public string Save(IntegrationDefinition draft, string actor, out IntegrationDefinition saved)
        {
            saved = null;
            var error = Validate(draft);
            if (error != null)
            {
                return error;
            }

            var created = draft.Id == 0;
            saved = Store.Save(draft);
            if (saved == null)
            {
                return "The integration no longer exists.";
            }

            Publish(created ? SrsEventTypes.IntegrationCreated : SrsEventTypes.IntegrationUpdated, actor,
                $"{(created ? "Created" : "Changed")} integration \"{saved.Name}\"", saved);
            return null;
        }

        public bool SetEnabled(long id, bool enabled, string actor)
        {
            if (!Store.SetEnabled(id, enabled))
            {
                return false;
            }

            var integration = Store.Get(id);
            Publish(SrsEventTypes.IntegrationUpdated, actor,
                $"{(enabled ? "Enabled" : "Disabled")} integration \"{integration.Name}\"", integration);
            return true;
        }

        public bool Delete(long id, string actor)
        {
            var integration = Store.Get(id);
            if (integration == null || !Store.Delete(id))
            {
                return false;
            }

            Publish(SrsEventTypes.IntegrationDeleted, actor, $"Deleted integration \"{integration.Name}\"", integration);
            return true;
        }

        /// <summary>Runs the action once with example data for the trigger event, without saving or queueing.</summary>
        public async Task<IntegrationTestResult> TestAsync(IntegrationDefinition draft, CancellationToken cancellationToken)
        {
            var error = Validate(draft);
            if (error != null)
            {
                return new IntegrationTestResult(IntegrationResult.Fail(error), false);
            }

            var trigger = SrsEventTypes.Find(draft.TriggerType);
            var data = EventData.Sample(trigger);
            var context = new IntegrationExecutionContext
            {
                Integration = draft,
                Config = WithStoredSecrets(draft),
                EventType = trigger,
                Data = data
            };

            var result = await RunAsync(FindAction(draft.ActionType), context, cancellationToken);
            return new IntegrationTestResult(result, EventData.Matches(data, draft.Conditions));
        }

        /// <summary>Runs an action with the timeout; exceptions become temporary failures.</summary>
        internal static async Task<IntegrationResult> RunAsync(IIntegrationAction action,
            IntegrationExecutionContext context, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ActionTimeout);
            try
            {
                return await action.ExecuteAsync(context, timeout.Token) ??
                       IntegrationResult.Fail("The action returned no result.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return IntegrationResult.Retry($"Timed out after {ActionTimeout.TotalSeconds:0} seconds.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return IntegrationResult.Retry(ex.Message);
            }
        }

        /// <summary>The draft's settings, with secrets left empty filled in from the stored integration.</summary>
        private JsonObject WithStoredSecrets(IntegrationDefinition draft)
        {
            var config = (JsonObject)(draft.ActionConfig?.DeepClone() ?? new JsonObject());
            var action = FindAction(draft.ActionType);
            if (draft.Id == 0 || action == null)
            {
                return config;
            }

            var stored = Store.GetForExecution(draft.Id);
            if (stored == null || stored.ActionType != draft.ActionType)
            {
                return config;
            }

            foreach (var field in action.SecretFields)
            {
                var empty = !config.TryGetPropertyValue(field, out var node) || node == null ||
                            (node is JsonValue value && value.TryGetValue<string>(out var text) &&
                             string.IsNullOrWhiteSpace(text));
                if (empty && stored.ActionConfig.TryGetPropertyValue(field, out var secret) && secret != null)
                {
                    config[field] = secret.DeepClone();
                }
            }

            return config;
        }

        private void Publish(SrsEventType type, string actor, string message, IntegrationDefinition integration)
        {
            _events.Publish(type, actor, message, new JsonObject
            {
                ["integration"] = new JsonObject
                {
                    ["id"] = integration.Id,
                    ["name"] = integration.Name,
                    ["trigger"] = integration.TriggerType,
                    ["action"] = integration.ActionType,
                    ["enabled"] = integration.Enabled
                }
            });
        }
    }
}
