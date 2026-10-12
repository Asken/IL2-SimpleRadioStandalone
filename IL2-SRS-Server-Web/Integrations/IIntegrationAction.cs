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

        /// <summary>Checks settings before they are saved; returns an error message or null.</summary>
        string Validate(JsonObject config);

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
    }
}
