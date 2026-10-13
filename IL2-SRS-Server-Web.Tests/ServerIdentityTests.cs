using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ciribob.IL2.SimpleRadio.Standalone.Common.Network;
using Ciribob.IL2.SimpleRadio.Standalone.Common.Setting;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Admin;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Events;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Integrations;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Settings;
using Xunit;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Tests
{
    public class ServerIdentityTests
    {
        private static readonly Dictionary<string, string> NoOverrides = new Dictionary<string, string>();

        [Fact]
        public void NameFallsBackToTheHostnameAndVersionToTheServerVersion()
        {
            using var temp = new TempDirectory();
            var settings = new ServerSettingsStore(temp.CreateDatabase(), NoOverrides);

            var identity = new ServerIdentity(settings, "dev", "srs-1");
            Assert.Equal("srs-1", identity.Name);
            Assert.Equal(ReleaseMetadata.Version, identity.Version);
            Assert.Null(identity.AdminUrl);

            Assert.Null(settings.Set(ServerSettingsKeys.SERVER_NAME, " Combat Box "));
            Assert.Null(settings.Set(ServerSettingsKeys.PUBLIC_ADMIN_URL, "https://srs.example.com"));
            identity = new ServerIdentity(settings, "1.0.5.0-preview", "srs-1");
            Assert.Equal("Combat Box", identity.Name);
            Assert.Equal("1.0.5.0-preview", identity.Version);
            Assert.Equal("https://srs.example.com", identity.AdminUrl);
        }

        [Fact]
        public void ServerNameAndAdminUrlAreNotSentToClients()
        {
            using var temp = new TempDirectory();
            var settings = new ServerSettingsStore(temp.CreateDatabase(), NoOverrides);
            Assert.Null(settings.Set(ServerSettingsKeys.SERVER_NAME, "Combat Box"));
            Assert.Null(settings.Set(ServerSettingsKeys.PUBLIC_ADMIN_URL, "https://srs.example.com"));

            var synced = settings.ToDictionary();
            Assert.False(synced.ContainsKey(nameof(ServerSettingsKeys.SERVER_NAME)));
            Assert.False(synced.ContainsKey(nameof(ServerSettingsKeys.PUBLIC_ADMIN_URL)));
        }

        [Theory]
        [InlineData("", true)]
        [InlineData("https://srs.example.com/admin", true)]
        [InlineData("http://10.0.0.5:8081", true)]
        [InlineData("srs.example.com", false)]
        [InlineData("ftp://srs.example.com", false)]
        public void PublicAdminUrlMustBeAnHttpAddress(string value, bool valid)
        {
            var error = SettingDefinition.For(ServerSettingsKeys.PUBLIC_ADMIN_URL).Validate(value);
            Assert.Equal(valid, error == null);
        }

        [Fact]
        public void EventDataHasTheServerIdentityWithoutReplacingTheEventsOwnServerFields()
        {
            var identity = new JsonObject
            {
                ["name"] = "Combat Box", ["hostname"] = "srs-1", ["version"] = "1.0.5.0", ["adminUrl"] = null
            };
            var started = SrsEvent.Create(SrsEventTypes.ServerStopped, "admin", "stopped",
                EventPayload.Server(6002, 3, DateTime.UtcNow.AddMinutes(-90)));
            var data = EventData.From(started, identity);

            Assert.Equal("Combat Box", EventData.AsText(EventData.Resolve(data, "server.name")));
            Assert.Equal("srs-1", EventData.AsText(EventData.Resolve(data, "server.hostname")));
            Assert.Equal("6002", EventData.AsText(EventData.Resolve(data, "server.port")));
            Assert.InRange(EventData.Resolve(data, "server.uptime")!.Value.GetInt64(), 5390, 5410);

            var connected = EventData.From(SrsEvent.Create(SrsEventTypes.ClientConnected, "system", "joined"), identity);
            Assert.Equal("Combat Box", EventData.AsText(EventData.Resolve(connected, "server.name")));
        }

        [Fact]
        public void EveryEventListsTheServerIdentityFields()
        {
            var paths = EventData.Fields(SrsEventTypes.ClientConnected).Select(f => f.Path).ToList();
            Assert.Contains("server.name", paths);
            Assert.Contains("server.adminUrl", paths);
            Assert.DoesNotContain("server.uptime", paths);
            Assert.Contains("server.uptime", EventData.Fields(SrsEventTypes.ServerStopped).Select(f => f.Path));
        }

        [Fact]
        public void SamplesUseTheRealServerIdentity()
        {
            var sample = EventData.Sample(SrsEventTypes.ServerStarted, new JsonObject { ["name"] = "Combat Box" });

            Assert.Equal("Combat Box", EventData.AsText(EventData.Resolve(sample, "server.name")));
            Assert.Equal("srs-1", EventData.AsText(EventData.Resolve(EventData.Sample(SrsEventTypes.ServerStarted),
                "server.hostname")));
            Assert.Equal("6002", EventData.AsText(EventData.Resolve(sample, "server.port")));
        }

        [Fact]
        public void TemplatesCanShowUptimeAsATime()
        {
            var data = EventData.Sample(SrsEventTypes.ServerStopped);

            Assert.Equal("03:12:40", MessageTemplate.Render("{{ timespan.from_seconds server.uptime }}", data,
                TemplateEscape.None));
        }
    }
}
