using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Components.Integrations;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Integrations
{
    /// <summary>
    /// Posts a message to a Discord channel through a webhook. Event values are Markdown-escaped and the message
    /// never pings anyone (<c>allowed_mentions</c> is empty), whatever the pilots call themselves.
    /// </summary>
    public sealed class DiscordWebhookAction : IIntegrationAction
    {
        public const string ActionType = "discord-webhook";
        public const string WebhookUrlField = "webhookUrl";
        public const string StyleField = "style";
        public const string ContentField = "content";
        public const string TitleField = "title";
        public const string ColorField = "color";
        public const string UsernameField = "username";
        public const string MessageModeField = "messageMode";

        public const string StyleText = "text";
        public const string StyleEmbed = "embed";

        /// <summary>Each event posts a new message.</summary>
        public const string ModePost = "post";

        /// <summary>Each event edits the same message (e.g. a server status), posting a new one if it is gone.</summary>
        public const string ModeUpdate = "update";

        /// <summary>Integration state key prefix for the message ID, followed by a hash of the webhook URL.</summary>
        private const string MessageStatePrefix = "discord.message.";
        public const string DefaultContent = "{{ event.message }}";
        public const string DefaultTitle = "{{ event.label }}";
        public const string DefaultColor = "#5865f2";

        private const int MaxContentLength = 2000;
        private const int MaxDescriptionLength = 4096;
        private const int MaxTitleLength = 256;
        private const int MaxUsernameLength = 80;

        private static readonly string[] WebhookHosts =
            { "discord.com", "discordapp.com", "ptb.discord.com", "canary.discord.com" };

        private readonly IHttpClientFactory _http;
        private readonly WebhookRateLimiter _rateLimiter;

        public DiscordWebhookAction(IHttpClientFactory http, WebhookRateLimiter rateLimiter)
        {
            _http = http;
            _rateLimiter = rateLimiter;
        }

        public string Type => ActionType;
        public string Label => "Post to Discord";

        public string Description =>
            "Posts a message to a Discord channel through a webhook (channel settings → Integrations → Webhooks). " +
            "Text from the event is escaped and the message never mentions anyone.";

        public Type ConfigEditor => typeof(DiscordWebhookEditor);
        public IReadOnlyCollection<string> SecretFields { get; } = new[] { WebhookUrlField };

        public IReadOnlyDictionary<string, string> Validate(JsonObject config)
        {
            var errors = new Dictionary<string, string>(StringComparer.Ordinal);
            var url = config.GetText(WebhookUrlField);
            if (url == null)
            {
                errors[WebhookUrlField] = "Enter the Discord webhook URL.";
            }
            else if (!IsWebhookUrl(url))
            {
                errors[WebhookUrlField] = "Enter a Discord webhook URL, https://discord.com/api/webhooks/….";
            }

            var style = config.GetText(StyleField) ?? StyleText;
            if (style != StyleText && style != StyleEmbed)
            {
                errors[StyleField] = "Choose a message style.";
            }

            var mode = config.GetText(MessageModeField) ?? ModePost;
            if (mode != ModePost && mode != ModeUpdate)
            {
                errors[MessageModeField] = "Choose whether to post a new message or keep one message updated.";
            }

            if ((config.GetText(UsernameField)?.Length ?? 0) > MaxUsernameLength)
            {
                errors[UsernameField] = $"The bot name can be at most {MaxUsernameLength} characters.";
            }

            if (MessageTemplate.Check(config.GetText(TitleField)) is { } titleError)
            {
                errors[TitleField] = titleError;
            }

            if (config.GetText(ColorField) is { } color && !TryParseColor(color, out _))
            {
                errors[ColorField] = "Enter a hex colour such as #5865f2.";
            }

            if (MessageTemplate.Check(config.GetText(ContentField)) is { } contentError)
            {
                errors[ContentField] = contentError;
            }

            return errors;
        }

        public async Task<IntegrationResult> ExecuteAsync(IntegrationExecutionContext context,
            CancellationToken cancellationToken)
        {
            JsonObject message;
            try
            {
                message = BuildMessage(context.Config, context.Data);
            }
            catch (TemplateException ex)
            {
                return IntegrationResult.Fail(ex.Message);
            }

            var url = context.Config.GetText(WebhookUrlField);
            var wait = _rateLimiter.TryAcquire(url);
            if (wait.HasValue)
            {
                return IntegrationResult.Defer(
                    $"Waiting for Discord's rate limit ({WebhookRateLimiter.DefaultPerMinute} messages a minute).",
                    wait.Value);
            }

            var client = _http.CreateClient(WebhookHttp.ClientName);
            // Test runs always post: they must not edit (or replace) the message real runs keep updated.
            var keepUpdated = context.Config.GetText(MessageModeField) == ModeUpdate && !context.IsTest;
            var stateKey = MessageStatePrefix + Hash(url);
            if (keepUpdated && context.State.Get(stateKey) is { } messageId)
            {
                // Webhook message edits can't change the bot name.
                var edit = (JsonObject)message.DeepClone();
                edit.Remove(UsernameField);
                using var request = new HttpRequestMessage(HttpMethod.Patch, MessageUrl(url, messageId))
                {
                    Content = JsonContent.Create(edit)
                };
                using var edited = await client.SendAsync(request, cancellationToken);
                if (edited.IsSuccessStatusCode)
                {
                    return IntegrationResult.Success("Updated the Discord message.");
                }

                if (edited.StatusCode != HttpStatusCode.NotFound)
                {
                    return await FailureAsync(edited, cancellationToken);
                }

                // The message was deleted in Discord: post a new one below and keep that updated instead.
                context.State.Remove(stateKey);
            }

            using var response = await client.PostAsJsonAsync(WithWait(url), message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return await FailureAsync(response, cancellationToken);
            }

            if (!keepUpdated)
            {
                return IntegrationResult.Success(context.IsTest && context.Config.GetText(MessageModeField) == ModeUpdate
                    ? "Discord accepted the test message. Tests post a new message and leave the updated one alone."
                    : "Discord accepted the message.");
            }

            if (await MessageIdAsync(response, cancellationToken) is not { } postedId)
            {
                return IntegrationResult.Success("Discord accepted the message but didn't return its ID, " +
                                                  "so the next event posts a new one.");
            }

            context.State.Set(stateKey, postedId);
            return IntegrationResult.Success("Posted a new Discord message; the next events update it.");
        }

        private static async Task<IntegrationResult> FailureAsync(HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return IntegrationResult.Defer("Discord asked to slow down.",
                    await WebhookHttp.RetryAfterAsync(response, cancellationToken));
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return IntegrationResult.Fail(
                    $"Discord rejected the webhook URL ({WebhookHttp.Describe(response.StatusCode)}). " +
                    "The webhook may have been deleted.");
            }

            var error = $"Discord answered {WebhookHttp.Describe(response.StatusCode)}: " +
                        await DiscordErrorAsync(response, cancellationToken);
            return WebhookHttp.IsTemporary(response.StatusCode)
                ? IntegrationResult.Retry(error)
                : IntegrationResult.Fail(error);
        }

        /// <summary>The webhook request body for the event.</summary>
        internal static JsonObject BuildMessage(JsonObject config, JsonElement data)
        {
            var text = MessageTemplate.Render(Template(config, ContentField, DefaultContent), data,
                TemplateEscape.DiscordMarkdown).Trim();
            var message = new JsonObject();

            if ((config.GetText(StyleField) ?? StyleText) == StyleEmbed)
            {
                var title = MessageTemplate.Render(Template(config, TitleField, DefaultTitle), data,
                    TemplateEscape.DiscordMarkdown).Trim();
                if (text.Length == 0 && title.Length == 0)
                {
                    throw new TemplateException("The message is empty for this event.");
                }

                TryParseColor(config.GetText(ColorField) ?? DefaultColor, out var color);
                var embed = new JsonObject { ["color"] = color };
                if (title.Length > 0)
                {
                    embed["title"] = MessageTemplate.Truncate(title, MaxTitleLength);
                }

                if (text.Length > 0)
                {
                    embed["description"] = MessageTemplate.Truncate(text, MaxDescriptionLength);
                }

                if (EventData.AsText(EventData.Resolve(data, "event.occurredAt")) is { } occurredAt)
                {
                    embed["timestamp"] = occurredAt;
                }

                message["embeds"] = new JsonArray(embed);
            }
            else if (text.Length > 0)
            {
                message["content"] = MessageTemplate.Truncate(text, MaxContentLength);
            }
            else
            {
                throw new TemplateException("The message is empty for this event.");
            }

            if (config.GetText(UsernameField) is { } username)
            {
                message["username"] = username;
            }

            message["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() };
            return message;
        }

        internal static bool IsWebhookUrl(string url)
        {
            return Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
                   Array.IndexOf(WebhookHosts, uri.Host.ToLowerInvariant()) >= 0 &&
                   uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);
        }

        internal static bool TryParseColor(string text, out int color)
        {
            color = 0;
            var hex = text?.Trim().TrimStart('#');
            return hex is { Length: 6 } &&
                   int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out color);
        }

        private static string WithWait(string url)
        {
            var trimmed = url.Trim();
            return trimmed + (trimmed.Contains('?') ? "&" : "?") + "wait=true";
        }

        /// <summary>The webhook's URL for one of its messages, keeping a query such as ?thread_id=.</summary>
        internal static string MessageUrl(string url, string messageId)
        {
            var builder = new UriBuilder(url.Trim());
            builder.Path = builder.Path.TrimEnd('/') + "/messages/" + Uri.EscapeDataString(messageId);
            return builder.Uri.ToString();
        }

        private static async Task<string> MessageIdAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                return json.RootElement.ValueKind == JsonValueKind.Object &&
                       json.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Identifies the webhook in stored state without storing its secret URL.</summary>
        private static string Hash(string url)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url.Trim()));
            return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
        }

        private static async Task<string> DiscordErrorAsync(HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            var body = await WebhookHttp.BodyExcerptAsync(response, cancellationToken);
            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("message", out var message))
                {
                    return message.GetString();
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
            }

            return body;
        }

        private static string Template(JsonObject config, string field, string fallback)
        {
            var template = config.GetText(field);
            return string.IsNullOrWhiteSpace(template) ? fallback : template;
        }
    }
}
