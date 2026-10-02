using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Api;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Audit;
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
        public async Task StoresQueuedEventsAndFiltersThem()
        {
            using var temp = new TempDirectory();
            var audit = new AuditLog(temp.CreateDatabase(), new AuditRetention());
            await audit.StartAsync(CancellationToken.None);

            audit.Write(AuditCategory.Admin, "admin", "Kicked Pilot One (Blue)");
            audit.Write(AuditCategory.Client, "system", "Pilot Two (Red) connected from 203.0.113.7");
            audit.Write(AuditCategory.Auth, "anonymous", "Failed admin login from 203.0.113.9");
            await audit.StopAsync(CancellationToken.None);

            Assert.Equal(3, audit.Count());
            Assert.Equal("Failed admin login from 203.0.113.9", audit.Query().First().Message);
            Assert.Single(audit.Query(category: AuditCategory.Client));
            Assert.Single(audit.Query(search: "pilot one"));
            Assert.Empty(audit.Query(search: "100%_"));
        }

        [Fact]
        public async Task RetentionKeepsOnlyRecentEventsWithinTheRowLimit()
        {
            using var temp = new TempDirectory();
            var audit = new AuditLog(temp.CreateDatabase(), new AuditRetention { Days = 30, MaxRows = 3 });
            await audit.StartAsync(CancellationToken.None);
            for (var i = 1; i <= 5; i++)
            {
                audit.Write(AuditCategory.Server, "system", "event " + i);
            }

            await audit.StopAsync(CancellationToken.None);

            audit.ApplyRetention(DateTime.UtcNow);
            Assert.Equal(new[] { "event 5", "event 4", "event 3" }, audit.Query().Select(e => e.Message));

            audit.ApplyRetention(DateTime.UtcNow.AddDays(31));
            Assert.Equal(0, audit.Count());
        }
    }
}
