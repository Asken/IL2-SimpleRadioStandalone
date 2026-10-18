using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Api;
using Ciribob.IL2.SimpleRadio.Standalone.Common.Network;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Audit;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Events;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Network;
using Xunit;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Tests
{
    public class BanStoreTests
    {
        [Fact]
        public void PermanentBansBlockUntilRemoved()
        {
            using var temp = new TempDirectory();
            var bans = new BanStore(temp.CreateDatabase());

            var ban = bans.Add(IPAddress.Parse("203.0.113.7"), "Pilot", "Spam", "admin", null);

            Assert.True(bans.Contains(IPAddress.Parse("203.0.113.7")));
            Assert.True(bans.Contains(IPAddress.Parse("::ffff:203.0.113.7")));
            Assert.False(bans.Contains(IPAddress.Parse("203.0.113.8")));

            Assert.NotNull(bans.Remove(ban.Id));
            Assert.False(bans.Contains(IPAddress.Parse("203.0.113.7")));
            Assert.Null(bans.Remove(ban.Id));
        }

        [Fact]
        public void ExpiredBansNoLongerBlockButAreListed()
        {
            using var temp = new TempDirectory();
            var bans = new BanStore(temp.CreateDatabase());

            bans.Add(IPAddress.Parse("203.0.113.7"), null, null, "admin", DateTime.UtcNow.AddMinutes(-1));
            bans.Add(IPAddress.Parse("203.0.113.8"), null, null, "admin", DateTime.UtcNow.AddHours(1));

            Assert.False(bans.Contains(IPAddress.Parse("203.0.113.7")));
            Assert.True(bans.Contains(IPAddress.Parse("203.0.113.8")));
            Assert.Equal(1, bans.ActiveCount);
            Assert.Equal(2, bans.List().Count);
        }

        [Fact]
        public void BansSurviveARestart()
        {
            using var temp = new TempDirectory();
            new BanStore(temp.CreateDatabase()).Add(IPAddress.Parse("198.51.100.9"), "Pilot", "Reason", "api:bot", null);

            var reopened = new BanStore(temp.CreateDatabase());
            var ban = Assert.Single(reopened.List());

            Assert.True(reopened.Contains(IPAddress.Parse("198.51.100.9")));
            Assert.Equal("Pilot", ban.PlayerName);
            Assert.Equal("api:bot", ban.CreatedBy);
        }
    }

    public class ApiKeyStoreTests
    {
        [Fact]
        public void ValidatesCreatedKeysUntilRevoked()
        {
            using var temp = new TempDirectory();
            var keys = new ApiKeyStore(temp.CreateDatabase());

            var (record, key) = keys.Create("Discord bot", ApiScope.Write);

            Assert.StartsWith("srs_" + record.Prefix + "_", key);
            Assert.Equal(ApiScope.Write, keys.Validate(key)?.Scope);
            Assert.Null(keys.Validate(key + "x"));
            Assert.Null(keys.Validate("not-a-key"));

            Assert.True(keys.Revoke(record.Id));
            Assert.Null(keys.Validate(key));
            Assert.True(keys.List().Single().IsRevoked);
        }

        [Theory]
        [InlineData("", ApiScope.Read)]
        [InlineData("name", "admin")]
        public void RejectsInvalidNamesAndScopes(string name, string scope)
        {
            using var temp = new TempDirectory();
            var keys = new ApiKeyStore(temp.CreateDatabase());

            Assert.Throws<ArgumentException>(() => keys.Create(name, scope));
        }
    }

    public class AuditLogTests
    {
        [Fact]
        public async Task StoresPublishedEventsAndFiltersThem()
        {
            using var temp = new TempDirectory();
            var events = new ServerEvents();
            var audit = new AuditLog(temp.CreateDatabase(), new AuditRetention(), events);
            await audit.StartAsync(CancellationToken.None);

            var pilot = new SRClient { ClientGuid = "guid-1", Name = "Pilot One", Coalition = 2 };
            events.Publish(SrsEventTypes.ClientKicked, "admin", "Kicked Pilot One (Blue)", EventPayload.Client(pilot));
            events.Publish(SrsEventTypes.ClientConnected, "system", "Pilot Two (Red) connected from 203.0.113.7");
            events.Publish(SrsEventTypes.AdminLoginFailed, "anonymous", "Failed admin login from 203.0.113.9",
                EventPayload.Remote("203.0.113.9"));
            await audit.StopAsync(CancellationToken.None);

            Assert.Equal(3, audit.Count());
            var newest = audit.Query().First();
            Assert.Equal("Failed admin login from 203.0.113.9", newest.Message);
            Assert.Equal("auth.login_failed", newest.Type);
            Assert.Equal("{\"ip\":\"203.0.113.9\"}", newest.Payload);
            Assert.Single(audit.Query(category: EventCategory.Client));
            Assert.Single(audit.Query(search: "pilot one"));
            Assert.Empty(audit.Query(search: "100%_"));

            var kicked = Assert.Single(audit.Query(type: "admin.client_kicked"));
            Assert.Equal(EventCategory.Admin, kicked.Category);
            Assert.Equal("guid-1", kicked.ClientGuid);
            Assert.Equal(kicked.Id, Assert.Single(audit.Query(clientGuid: "guid-1")).Id);
        }

        [Fact]
        public async Task ClearingTheLogRecordsWhoClearedIt()
        {
            using var temp = new TempDirectory();
            var events = new ServerEvents();
            var audit = new AuditLog(temp.CreateDatabase(), new AuditRetention(), events);
            await audit.StartAsync(CancellationToken.None);
            events.Publish(SrsEventTypes.ServerStarted, "system", "Server started");
            await audit.StopAsync(CancellationToken.None);

            Assert.Equal(1, audit.Clear("admin"));
            await audit.StopAsync(CancellationToken.None);

            var cleared = Assert.Single(audit.Query());
            Assert.Equal("admin.event_log_cleared", cleared.Type);
            Assert.Equal("{\"deleted\":1}", cleared.Payload);
        }

        [Fact]
        public async Task RetentionKeepsOnlyRecentEventsWithinTheRowLimit()
        {
            using var temp = new TempDirectory();
            var events = new ServerEvents();
            var audit = new AuditLog(temp.CreateDatabase(), new AuditRetention { Days = 30, MaxRows = 3 }, events);
            await audit.StartAsync(CancellationToken.None);
            for (var i = 1; i <= 5; i++)
            {
                events.Publish(SrsEventTypes.ServerStarted, "system", "event " + i);
            }

            await audit.StopAsync(CancellationToken.None);

            audit.ApplyRetention(DateTime.UtcNow);
            Assert.Equal(new[] { "event 5", "event 4", "event 3" }, audit.Query().Select(e => e.Message));

            audit.ApplyRetention(DateTime.UtcNow.AddDays(31));
            Assert.Equal(0, audit.Count());
        }
    }
}
