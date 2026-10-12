using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Events;
using Microsoft.AspNetCore.Components;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Integrations
{
    /// <summary>
    /// Something an integration can do when its trigger event happens, such as posting to a Discord webhook.
    /// Register implementations with <see cref="ServerExtensions.AddIntegrationAction{T}"/>; they are singletons.
    /// </summary>
    public interface IIntegrationAction
    {
        /// <summary>Stable id stored with each integration, e.g. "discord-webhook".</summary>
        string Type { get; }

        string Label { get; }
        string Description { get; }

        /// <summary>
        /// Component that edits the action's settings; must derive from <see cref="IntegrationActionEditor"/>.
        /// </summary>
        Type ConfigEditor { get; }

        /// <summary>
        /// Top-level settings that are secrets (webhook URLs, API keys). They are encrypted at rest, never sent
        /// back to the browser or API, and masked in the event log.
        /// </summary>
        IReadOnlyCollection<string> SecretFields { get; }

        /// <summary>
        /// Checks settings before they are saved or tested: an error message per settings field (the key in
        /// <paramref name="config"/>), empty when they are valid. The integration page shows each under its field.
        /// </summary>
        IReadOnlyDictionary<string, string> Validate(JsonObject config);

        /// <summary>
        /// Runs the action. Throwing counts as a temporary failure (retried if the integration allows it);
        /// return <see cref="IntegrationResult.Fail"/> for failures retrying will not fix.
        /// </summary>
        Task<IntegrationResult> ExecuteAsync(IntegrationExecutionContext context, CancellationToken cancellationToken);
    }

    /// <summary>Base for an action's settings editor shown on the integration page.</summary>
    public abstract class IntegrationActionEditor : ComponentBase
    {
        /// <summary>The settings being edited; change values in place and call <see cref="NotifyChanged"/>.</summary>
        [Parameter, EditorRequired] public JsonObject Config { get; set; }

        /// <summary>Hints for stored secrets ("ends in 0Jdl"), keyed by field; their values in Config are null.</summary>
        [Parameter] public IReadOnlyDictionary<string, string> SecretHints { get; set; } = new Dictionary<string, string>();

        /// <summary>The selected trigger event, for field pickers and previews; may be null.</summary>
        [Parameter] public SrsEventType Trigger { get; set; }

        [Parameter] public EventCallback ConfigChanged { get; set; }

        /// <summary>Errors to show now, keyed by settings field; the page decides when a field's error shows.</summary>
        [Parameter] public IReadOnlyDictionary<string, string> Errors { get; set; } = new Dictionary<string, string>();

        /// <summary>Tell the page a field was left, so its error can show (call from onblur).</summary>
        [Parameter] public EventCallback<string> FieldTouched { get; set; }

        protected string GetString(string field) =>
            Config.TryGetPropertyValue(field, out var node) && node is JsonValue value &&
            value.TryGetValue<string>(out var text)
                ? text
                : null;

        protected Task SetString(string field, string value)
        {
            Config[field] = string.IsNullOrEmpty(value) ? null : value;
            return NotifyChanged();
        }

        protected string SecretPlaceholder(string field, string placeholder) =>
            SecretHints.TryGetValue(field, out var hint)
                ? $"Stored — {hint}. Enter a new value to replace it."
                : placeholder;

        protected Task NotifyChanged() => ConfigChanged.InvokeAsync();

        /// <summary>The error to show for a settings field, or null.</summary>
        protected string ErrorFor(string field) => Errors.TryGetValue(field, out var error) ? error : null;

        /// <summary>The id of a field's error element, for aria-describedby.</summary>
        protected static string ErrorId(string field) => "config-" + field + "-error";

        /// <summary>aria-invalid for a settings field.</summary>
        protected string Invalid(string field) => ErrorFor(field) == null ? null : "true";

        /// <summary>aria-describedby for a settings field.</summary>
        protected string DescribedBy(string field) => ErrorFor(field) == null ? null : ErrorId(field);

        protected Task Touch(string field) => FieldTouched.InvokeAsync(field);
    }

    public static class ActionConfigExtensions
    {
        /// <summary>A text setting, or null when it is missing or blank.</summary>
        public static string GetText(this JsonObject config, string field)
        {
            return config != null && config.TryGetPropertyValue(field, out var node) && node is JsonValue value &&
                   value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                ? text
                : null;
        }
    }
}
