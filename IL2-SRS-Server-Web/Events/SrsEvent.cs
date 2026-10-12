using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ciribob.IL2.SimpleRadio.Standalone.Common.Network;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Admin;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Network;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Events
{
    /// <summary>Event log categories; every event type belongs to one.</summary>
    public static class EventCategory
    {
        public const string Server = "server";
        public const string Client = "client";
        public const string Radio = "radio";
        public const string Admin = "admin";
        public const string Auth = "auth";

        public static readonly IReadOnlyList<string> All = new[] { Server, Client, Radio, Admin, Auth };
    }

    /// <summary>
    /// Something that happened on the server: stored in the event log and handed to integrations.
    /// Immutable, so subscribers on any thread can read it.
    /// </summary>
    public sealed class SrsEvent
    {
        private static readonly JsonElement EmptyPayload = JsonSerializer.SerializeToElement(new JsonObject());

        private SrsEvent(SrsEventType type, DateTime occurredAtUtc, string actor, string message, JsonElement payload)
        {
            EventType = type;
            OccurredAtUtc = occurredAtUtc;
            Actor = string.IsNullOrWhiteSpace(actor) ? "system" : actor;
            Message = message;
            Payload = payload;
        }

        public SrsEventType EventType { get; }
        public string Type => EventType.Type;
        public string Category => EventType.Category;
        public DateTime OccurredAtUtc { get; }

        /// <summary>Who caused it: "system", "admin", "api:&lt;key name&gt;" or "anonymous".</summary>
        public string Actor { get; }

        /// <summary>One-line description for the event log.</summary>
        public string Message { get; }

        /// <summary>Structured data described by <see cref="SrsEventType.Fields"/>; always a JSON object.</summary>
        public JsonElement Payload { get; }

        /// <summary>The client the event is about, if any.</summary>
        public string ClientGuid =>
            Payload.TryGetProperty("client", out var client) && client.ValueKind == JsonValueKind.Object &&
            client.TryGetProperty("guid", out var guid) && guid.ValueKind == JsonValueKind.String
                ? guid.GetString()
                : null;

        public static SrsEvent Create(SrsEventType type, string actor, string message, JsonObject payload = null,
            DateTime? occurredAtUtc = null)
        {
            ArgumentNullException.ThrowIfNull(type);
            return new SrsEvent(type, occurredAtUtc ?? DateTime.UtcNow, actor, message,
                payload == null ? EmptyPayload : JsonSerializer.SerializeToElement(payload));
        }
    }

    /// <summary>Builds the payload sections shared by several event types.</summary>
    public static class EventPayload
    {
        public static JsonObject Client(SRClient client, string ipAddress = null)
        {
            return new JsonObject
            {
                ["client"] = new JsonObject
                {
                    ["guid"] = client.ClientGuid,
                    ["name"] = client.Name,
                    ["coalition"] = client.Coalition,
                    ["coalitionName"] = ClientAdminPresentation.GetCoalitionName(client.Coalition),
                    ["callsign"] = Blank(client.AssignedCallsign),
                    ["vehicle"] = Blank(client.AssignedVehicle),
                    ["airfield"] = Blank(client.AssignedAirfield),
                    ["ip"] = ipAddress
                }
            };
        }

        public static JsonObject Server(int port, int clientCount)
        {
            return new JsonObject
            {
                ["server"] = new JsonObject { ["port"] = port, ["clientCount"] = clientCount }
            };
        }

        public static JsonObject Ban(BanRecord ban)
        {
            return new JsonObject
            {
                ["ban"] = new JsonObject
                {
                    ["id"] = ban.Id,
                    ["ip"] = ban.IpAddress,
                    ["playerName"] = ban.PlayerName,
                    ["reason"] = ban.Reason,
                    ["expiresAt"] = ban.ExpiresAtUtc?.ToString("O", CultureInfo.InvariantCulture),
                    ["permanent"] = ban.ExpiresAtUtc == null
                }
            };
        }

        public static JsonObject Remote(string ipAddress)
        {
            return new JsonObject { ["ip"] = ipAddress };
        }

        /// <summary>Adds the properties of <paramref name="extra"/> to <paramref name="payload"/>.</summary>
        public static JsonObject With(this JsonObject payload, JsonObject extra)
        {
            foreach (var property in new List<KeyValuePair<string, JsonNode>>(extra))
            {
                extra.Remove(property.Key);
                payload[property.Key] = property.Value;
            }

            return payload;
        }

        /// <summary>Adds one property to <paramref name="payload"/>.</summary>
        public static JsonObject With(this JsonObject payload, string name, JsonNode value)
        {
            payload[name] = value;
            return payload;
        }

        private static string Blank(string value)
        {
            return string.IsNullOrWhiteSpace(value) || value == "---" ? null : value;
        }
    }
}
