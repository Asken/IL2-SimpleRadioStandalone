using System;
using System.Collections.Generic;
using System.Linq;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Events
{
    /// <summary>A field in an event payload, addressed by a dotted path such as <c>client.name</c>.</summary>
    public sealed record SrsEventField(string Path, string Type, string Description);

    /// <summary>
    /// One kind of event the server publishes: its type name (e.g. <c>client.connected</c>), the event log
    /// category it is stored under, and the payload fields integrations can filter on and use in templates.
    /// </summary>
    public sealed class SrsEventType
    {
        public SrsEventType(string type, string category, string label, string description,
            params IEnumerable<SrsEventField>[] fields)
        {
            Type = type;
            Category = category;
            Label = label;
            Description = description;
            Fields = fields.SelectMany(group => group).ToArray();
        }

        public string Type { get; }
        public string Category { get; }
        public string Label { get; }
        public string Description { get; }
        public IReadOnlyList<SrsEventField> Fields { get; }

        public override string ToString() => Type;
    }

    /// <summary>Every event type the server publishes.</summary>
    public static class SrsEventTypes
    {
        private static readonly SrsEventField[] ClientFields =
        {
            new("client.guid", "string", "SRS client id"),
            new("client.name", "string", "Player name"),
            new("client.coalition", "number", "Coalition: 0 spectators, 1 red, 2 blue"),
            new("client.coalitionName", "string", "Red, Blue or Spectators"),
            new("client.callsign", "string", "Assigned callsign, when the pilot roster provides one"),
            new("client.vehicle", "string", "Assigned aircraft, when the pilot roster provides one"),
            new("client.airfield", "string", "Assigned airfield, when the pilot roster provides one"),
            new("client.ip", "string", "Client IP address, when known")
        };

        private static readonly SrsEventField[] ServerFields =
        {
            new("server.port", "number", "SRS port"),
            new("server.clientCount", "number", "Connected clients")
        };

        private static readonly SrsEventField[] BanFields =
        {
            new("ban.id", "number", "Ban id"),
            new("ban.ip", "string", "Banned IP address"),
            new("ban.playerName", "string", "Player name recorded with the ban"),
            new("ban.reason", "string", "Reason given"),
            new("ban.expiresAt", "string", "Expiry time (UTC, ISO 8601); empty for permanent bans"),
            new("ban.permanent", "boolean", "True when the ban never expires")
        };

        private static readonly SrsEventField[] ApiKeyFields =
        {
            new("apiKey.name", "string", "Key name"),
            new("apiKey.scope", "string", "read or write"),
            new("apiKey.prefix", "string", "Key prefix shown in the admin UI")
        };

        private static readonly SrsEventField[] RemoteFields =
        {
            new("ip", "string", "Remote IP address")
        };

        public static readonly SrsEventType ServerStarted = new("server.started", EventCategory.Server,
            "Server started", "The SRS server started listening, at startup or from the admin UI or API.",
            ServerFields);

        public static readonly SrsEventType ServerStopped = new("server.stopped", EventCategory.Server,
            "Server stopped", "The SRS server stopped; connected clients were disconnected.", ServerFields);

        public static readonly SrsEventType ServerStartFailed = new("server.start_failed", EventCategory.Server,
            "Server failed to start", "Starting the SRS server failed, for example because the port is in use.",
            ServerFields, new SrsEventField[] { new("error", "string", "Error message") });

        public static readonly SrsEventType ClientConnected = new("client.connected", EventCategory.Client,
            "Client connected", "A client completed the SRS handshake.", ClientFields);

        public static readonly SrsEventType ClientDisconnected = new("client.disconnected", EventCategory.Client,
            "Client disconnected", "A connected client left, timed out or was disconnected.", ClientFields);

        public static readonly SrsEventType ClientRejected = new("client.rejected", EventCategory.Client,
            "Connection refused", "A connection was refused: banned address, unsupported version or not an SRS client.",
            RemoteFields, new SrsEventField[] { new("reason", "string", "Why the connection was refused") });

        public static readonly SrsEventType ClientNameChanged = new("client.name_changed", EventCategory.Client,
            "Client renamed", "A connected client changed player name.",
            ClientFields, new SrsEventField[] { new("previousName", "string", "Name before the change") });

        public static readonly SrsEventType ClientCoalitionChanged = new("client.coalition_changed",
            EventCategory.Client, "Coalition changed", "A connected client switched coalition.",
            ClientFields, new SrsEventField[]
            {
                new("previousCoalition", "number", "Coalition before the change"),
                new("previousCoalitionName", "string", "Red, Blue or Spectators")
            });

        public static readonly SrsEventType RadioFrequencyChanged = new("radio.frequency_changed",
            EventCategory.Radio, "Radio retuned",
            "A client tuned a radio to another channel or frequency, or turned it on or off. Reported once the radio " +
            "has settled for two seconds, so turning the dial produces one event.",
            ClientFields, new SrsEventField[]
            {
                new("radio.index", "number", "Radio number (1 or 2)"),
                new("radio.enabled", "boolean", "False when the radio was turned off"),
                new("radio.channel", "number", "Channel, when on an AM channel"),
                new("radio.channelName", "string", "Channel name from Channel Names, when set"),
                new("radio.frequencyMhz", "number", "Frequency in MHz; empty when turned off"),
                new("radio.modulation", "string", "AM or FM"),
                new("radio.previousChannel", "number", "Channel before the change"),
                new("radio.previousFrequencyMhz", "number", "Frequency before the change; empty when it was off")
            });

        public static readonly SrsEventType ClientKicked = new("admin.client_kicked", EventCategory.Admin,
            "Client kicked", "An admin kicked a client.", ClientFields);

        public static readonly SrsEventType ClientBanned = new("admin.client_banned", EventCategory.Admin,
            "Client banned", "An admin banned a connected client's IP address.", ClientFields, BanFields);

        public static readonly SrsEventType AddressBanned = new("admin.ip_banned", EventCategory.Admin,
            "IP address banned", "An admin banned an IP address directly.",
            BanFields, new SrsEventField[] { new("disconnectedClients", "number", "Clients disconnected by the ban") });

        public static readonly SrsEventType BanRemoved = new("admin.ban_removed", EventCategory.Admin,
            "Ban removed", "An admin removed a ban.", BanFields);

        public static readonly SrsEventType ClientMuted = new("admin.client_muted", EventCategory.Admin,
            "Client muted", "An admin muted a client.", ClientFields);

        public static readonly SrsEventType ClientUnmuted = new("admin.client_unmuted", EventCategory.Admin,
            "Client unmuted", "An admin unmuted a client.", ClientFields);

        public static readonly SrsEventType SettingChanged = new("admin.setting_changed", EventCategory.Admin,
            "Setting changed", "An admin changed a server setting. Values of secret settings are left out.",
            new SrsEventField[]
            {
                new("setting.key", "string", "Setting key, e.g. COALITION_AUDIO_SECURITY"),
                new("setting.label", "string", "Setting name shown in the admin UI"),
                new("setting.previousValue", "string", "Value before the change; empty for secrets"),
                new("setting.value", "string", "New value; empty for secrets"),
                new("setting.secret", "boolean", "True for secret settings such as passwords"),
                new("setting.requiresRestart", "boolean", "True when the change applies after a restart")
            });

        public static readonly SrsEventType ChannelNamesChanged = new("admin.channel_names_changed",
            EventCategory.Admin, "Channel names changed", "An admin updated the channel names.",
            new SrsEventField[] { new("namedChannels", "number", "Channels that have a name") });

        public static readonly SrsEventType ApiKeyCreated = new("admin.api_key_created", EventCategory.Admin,
            "API key created", "An admin created an API key.", ApiKeyFields);

        public static readonly SrsEventType ApiKeyRevoked = new("admin.api_key_revoked", EventCategory.Admin,
            "API key revoked", "An admin revoked an API key.", ApiKeyFields);

        public static readonly SrsEventType EventLogCleared = new("admin.event_log_cleared", EventCategory.Admin,
            "Event log cleared", "An admin deleted all stored events.",
            new SrsEventField[] { new("deleted", "number", "Events deleted") });

        public static readonly SrsEventType AdminLogin = new("auth.login", EventCategory.Auth,
            "Admin login", "Someone logged in to the admin UI.", RemoteFields);

        public static readonly SrsEventType AdminLoginFailed = new("auth.login_failed", EventCategory.Auth,
            "Failed admin login", "A login to the admin UI used the wrong password.", RemoteFields);

        public static readonly SrsEventType AdminLogout = new("auth.logout", EventCategory.Auth,
            "Admin logout", "Someone logged out of the admin UI.", RemoteFields);

        public static readonly IReadOnlyList<SrsEventType> All = new[]
        {
            ServerStarted, ServerStopped, ServerStartFailed,
            ClientConnected, ClientDisconnected, ClientRejected, ClientNameChanged, ClientCoalitionChanged,
            RadioFrequencyChanged,
            ClientKicked, ClientBanned, AddressBanned, BanRemoved, ClientMuted, ClientUnmuted, SettingChanged,
            ChannelNamesChanged, ApiKeyCreated, ApiKeyRevoked, EventLogCleared,
            AdminLogin, AdminLoginFailed, AdminLogout
        };

        private static readonly Dictionary<string, SrsEventType> ByType =
            All.ToDictionary(type => type.Type, StringComparer.Ordinal);

        /// <summary>The event type with this name, or null.</summary>
        public static SrsEventType Find(string type)
        {
            return type != null && ByType.TryGetValue(type, out var eventType) ? eventType : null;
        }
    }
}
