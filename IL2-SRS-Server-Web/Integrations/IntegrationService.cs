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
    /// What is wrong with an integration, keyed by field in the order of the form: <c>name</c>, <c>trigger</c>,
    /// <c>conditions[0].value</c>, <c>action</c>, <c>config.webhookUrl</c>, <c>maxRetries</c>.
    /// </summary>
    public sealed class IntegrationValidation
    {
        public const string Name = "name";
        public const string Trigger = "trigger";
        public const string Action = "action";
        public const string MaxRetries = "maxRetries";

        public IntegrationValidation(IReadOnlyDictionary<string, string> errors)
        {
            Errors = errors;
        }

        public IReadOnlyDictionary<string, string> Errors { get; }
        public bool IsValid => Errors.Count == 0;

        /// <summary>The first error, where only one message is shown (the API's <c>error</c>).</summary>
        public string FirstError => Errors.Values.FirstOrDefault();

        public static string Condition(int index, string part) => $"conditions[{index}].{part}";

        public static string Config(string field) => "config." + field;
    }

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

        /// <summary>Checks an integration before saving or testing it.</summary>
        public IntegrationValidation Validate(IntegrationDefinition draft)
        {
            var errors = new Dictionary<string, string>(StringComparer.Ordinal);

            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                errors[IntegrationValidation.Name] = "Enter a name.";
            }
            else if (draft.Name.Trim().Length > 100)
            {
                errors[IntegrationValidation.Name] = "The name can be at most 100 characters.";
            }

            if (SrsEventTypes.Find(draft.TriggerType) == null)
            {
                errors[IntegrationValidation.Trigger] = "Choose the event that runs this integration.";
            }

            for (var i = 0; i < draft.Conditions.Count; i++)
            {
                var condition = draft.Conditions[i];
                if (string.IsNullOrWhiteSpace(condition.Field))
                {
                    continue;
                }

                if (ConditionOperator.All.All(o => o.Value != condition.Operator))
                {
                    errors[IntegrationValidation.Condition(i, "operator")] = "Choose a comparison.";
                }
                else if (ConditionError(condition) is { } valueError)
                {
                    errors[IntegrationValidation.Condition(i, "value")] = valueError;
                }
            }

            var action = FindAction(draft.ActionType);
            if (action == null)
            {
                errors[IntegrationValidation.Action] = "Choose what to do when the event happens.";
            }
            else
            {
                foreach (var (field, error) in action.Validate(WithStoredSecrets(draft)))
                {
                    errors[IntegrationValidation.Config(field)] = error;
                }
            }

            if (draft.RetryMode != RetryMode.None &&
                (draft.MaxRetries < 1 || draft.MaxRetries > IntegrationDefinition.MaxRetriesLimit))
            {
                errors[IntegrationValidation.MaxRetries] =
                    $"Enter a number from 1 to {IntegrationDefinition.MaxRetriesLimit}.";
            }

            return new IntegrationValidation(errors);
        }

        /// <summary>A condition's value that can never match as intended, e.g. "greater than abc".</summary>
        private static string ConditionError(IntegrationCondition condition)
        {
            var value = condition.Value?.Trim();
            switch (condition.Operator)
            {
                case ConditionOperator.GreaterThan:
                case ConditionOperator.LessThan:
                    return double.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out _)
                        ? null
                        : "Enter a number.";
                case ConditionOperator.Contains:
                case ConditionOperator.NotContains:
                case ConditionOperator.In:
                    return string.IsNullOrEmpty(value) ? "Enter a value." : null;
                default:
                    return null;
            }
        }

        /// <summary>Creates or updates an integration; returns an error message or null.</summary>
        public string Save(IntegrationDefinition draft, string actor, out IntegrationDefinition saved)
        {
            saved = null;
            var validation = Validate(draft);
            if (!validation.IsValid)
            {
                return validation.FirstError;
            }

            var created = draft.Id == 0;
            saved = Store.Save(new IntegrationDefinition
            {
                Id = draft.Id,
                Name = draft.Name.Trim(),
                Description = draft.Description,
                Enabled = draft.Enabled,
                TriggerType = draft.TriggerType,
                // Rows without a field are unfinished rows on the form.
                Conditions = draft.Conditions.Where(c => !string.IsNullOrWhiteSpace(c.Field)).ToList(),
                ActionType = draft.ActionType,
                ActionConfig = draft.ActionConfig,
                RetryMode = draft.RetryMode,
                MaxRetries = draft.MaxRetries
            });
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
            var validation = Validate(draft);
            if (!validation.IsValid)
            {
                return new IntegrationTestResult(IntegrationResult.Fail(validation.FirstError), false);
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
