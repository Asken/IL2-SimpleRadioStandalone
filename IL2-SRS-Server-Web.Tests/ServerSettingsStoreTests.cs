using System.Collections.Generic;
using Ciribob.IL2.SimpleRadio.Standalone.Common.Setting;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Data;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Hosting;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Network;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Tests
{
    public class ServerSettingsStoreTests
    {
        private static readonly Dictionary<string, string> NoOverrides = new Dictionary<string, string>();

        [Fact]
        public void UsesDefaultsUntilASettingIsStored()
        {
            using var temp = new TempDirectory();
            var store = new ServerSettingsStore(temp.CreateDatabase(), NoOverrides);

            var port = store.Get(ServerSettingsKeys.SERVER_PORT);
            Assert.Equal(6002, port.IntValue);
            Assert.Equal(SettingSource.Default, port.Source);
            Assert.True(store.Get(ServerSettingsKeys.SHOW_TUNED_COUNT).BoolValue);
        }

        [Fact]
        public void StoredSettingsSurviveARestart()
        {
            using var temp = new TempDirectory();
            var store = new ServerSettingsStore(temp.CreateDatabase(), NoOverrides);

            Assert.Null(store.Set(ServerSettingsKeys.COALITION_AUDIO_SECURITY, "True"));
            Assert.Null(store.Set(ServerSettingsKeys.CHANNEL_LIMIT, "12"));

            var reopened = new ServerSettingsStore(temp.CreateDatabase(), NoOverrides);
            Assert.Equal("true", reopened.Get(ServerSettingsKeys.COALITION_AUDIO_SECURITY).StringValue);
            Assert.Equal(12, reopened.Get(ServerSettingsKeys.CHANNEL_LIMIT).IntValue);
            Assert.Equal(SettingSource.Database, reopened.Get(ServerSettingsKeys.CHANNEL_LIMIT).Source);
        }

        [Theory]
        [InlineData(ServerSettingsKeys.CHANNEL_LIMIT, "4")]
        [InlineData(ServerSettingsKeys.CHANNEL_LIMIT, "26")]
        [InlineData(ServerSettingsKeys.SERVER_PORT, "70000")]
        [InlineData(ServerSettingsKeys.IRL_RADIO_TX, "maybe")]
        [InlineData(ServerSettingsKeys.GLOBAL_LOBBY_FREQUENCIES, "248.22,abc")]
        [InlineData(ServerSettingsKeys.PRIORITY_TRANSMITTER_NAMES, "two\nlines")]
        public void RejectsInvalidValues(ServerSettingsKeys key, string value)
        {
            using var temp = new TempDirectory();
            var store = new ServerSettingsStore(temp.CreateDatabase(), NoOverrides);
            var before = store.Get(key).StringValue;

            Assert.NotNull(store.Set(key, value));
            Assert.Equal(before, store.Get(key).StringValue);
        }

        [Fact]
        public void NormalisesFrequencyLists()
        {
            using var temp = new TempDirectory();
            var store = new ServerSettingsStore(temp.CreateDatabase(), NoOverrides);

            Assert.Null(store.Set(ServerSettingsKeys.GLOBAL_LOBBY_FREQUENCIES, " 248.22 , 122.3,"));
            Assert.Equal("248.22,122.3", store.Get(ServerSettingsKeys.GLOBAL_LOBBY_FREQUENCIES).StringValue);
        }

        [Fact]
        public void OverridesWinAndCannotBeChanged()
        {
            using var temp = new TempDirectory();
            var database = temp.CreateDatabase();
            new ServerSettingsStore(database, NoOverrides).Set(ServerSettingsKeys.SERVER_PORT, "7000");

            var store = new ServerSettingsStore(database,
                new Dictionary<string, string> { ["SERVER_PORT"] = "6100" });

            Assert.Equal(6100, store.GetServerPort());
            Assert.True(store.Get(ServerSettingsKeys.SERVER_PORT).IsLocked);
            Assert.NotNull(store.Set(ServerSettingsKeys.SERVER_PORT, "6200"));
            Assert.Equal(6100, store.GetServerPort());
        }

        [Fact]
        public void InvalidOrUnknownOverridesStopStartup()
        {
            using var temp = new TempDirectory();
            var database = temp.CreateDatabase();

            Assert.Throws<ServerStartupException>(() => new ServerSettingsStore(database,
                new Dictionary<string, string> { ["CHANNEL_LIMIT"] = "99" }));
            Assert.Throws<ServerStartupException>(() => new ServerSettingsStore(database,
                new Dictionary<string, string> { ["NOT_A_SETTING"] = "1" }));
        }

        [Fact]
        public void ReadsOverridesForKnownSettingsOnly()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["SRS_SERVER_PORT"] = "6100",
                    ["SRS_ADMIN_PASSWORD"] = "not-a-setting",
                    ["SERVER_PORT"] = "1"
                })
                .Build();

            var overrides = ServerSettingsStore.ReadOverrides(configuration);

            Assert.Equal(new Dictionary<string, string> { ["SERVER_PORT"] = "6100" }, overrides);
        }

        [Fact]
        public void ClientSettingsExcludeServerOnlySettings()
        {
            using var temp = new TempDirectory();
            var settings = new ServerSettingsStore(temp.CreateDatabase(), NoOverrides).ToDictionary();

            Assert.False(settings.ContainsKey(ServerSettingsKeys.SERVER_PORT.ToString()));
            Assert.False(settings.ContainsKey(ServerSettingsKeys.SERVER_UI_THEME.ToString()));
            Assert.Equal("5", settings[ServerSettingsKeys.CHANNEL_LIMIT.ToString()]);
        }

        [Fact]
        public void ChannelNamesAreNormalisedAndReplacedAsAWhole()
        {
            using var temp = new TempDirectory();
            var store = new ServerSettingsStore(temp.CreateDatabase(), NoOverrides);

            store.SetChannelNames(new Dictionary<int, string>
            {
                [1] = "  Ops  ",
                [2] = "Strike\nPackage",
                [3] = "   ",
                [30] = "out of range"
            });
            store.SetChannelNames(new Dictionary<int, string>(store.GetChannelNames()) { [1] = "Ops" });

            Assert.Equal(new Dictionary<int, string> { [1] = "Ops", [2] = "Strike Package" }, store.GetChannelNames());
        }

        [Fact]
        public void CoalitionChannelNamesAreStoredSeparatelyAndSurviveARestart()
        {
            using var temp = new TempDirectory();
            var store = new ServerSettingsStore(temp.CreateDatabase(), NoOverrides);

            store.SetChannelNames(new Dictionary<int, string> { [1] = "Command", [2] = "Tower" });
            store.SetChannelNames(ChannelNameSettings.AlliesCoalition, new Dictionary<int, string> { [1] = "Kenway", [2] = "Dogrose" });
            store.SetChannelNames(ChannelNameSettings.AxisCoalition, new Dictionary<int, string> { [1] = " Freya ", [30] = "out of range" });

            // Replacing one coalition's names leaves the shared names and the other coalition alone.
            store.SetChannelNames(ChannelNameSettings.AlliesCoalition, new Dictionary<int, string> { [1] = "Kenway" });

            var reopened = new ServerSettingsStore(temp.CreateDatabase(), NoOverrides);
            Assert.Equal(new Dictionary<int, string> { [1] = "Command", [2] = "Tower" }, reopened.GetChannelNames());
            Assert.Equal(new Dictionary<int, string> { [1] = "Kenway" }, reopened.GetChannelNames(ChannelNameSettings.AlliesCoalition));
            Assert.Equal(new Dictionary<int, string> { [1] = "Freya" }, reopened.GetChannelNames(ChannelNameSettings.AxisCoalition));

            var synced = new Dictionary<string, string>();
            reopened.GetChannelNameSet().AddSyncedSettings(synced);
            Assert.Equal("Command", synced["CHANNEL_NAME_1"]);
            Assert.Equal("Kenway", synced["CHANNEL_NAME_ALLIES_1"]);
            Assert.Equal("Freya", synced["CHANNEL_NAME_AXIS_1"]);
            Assert.False(synced.ContainsKey("CHANNEL_NAME_ALLIES_2"));
        }

        [Fact]
        public void SavingAllChannelNamesReplacesEverySet()
        {
            using var temp = new TempDirectory();
            var store = new ServerSettingsStore(temp.CreateDatabase(), NoOverrides);
            store.SetChannelNames(ChannelNameSettings.AxisCoalition, new Dictionary<int, string> { [3] = "Schlageter" });

            store.SetChannelNames(new ChannelNameSet(
                new Dictionary<int, string> { [1] = "Command" },
                new Dictionary<int, string> { [1] = "Kenway" },
                new Dictionary<int, string>()));

            var reopened = new ServerSettingsStore(temp.CreateDatabase(), NoOverrides);
            Assert.Equal("Command", reopened.GetChannelNames()[1]);
            Assert.Equal("Kenway", reopened.GetChannelNames(ChannelNameSettings.AlliesCoalition)[1]);
            Assert.Empty(reopened.GetChannelNames(ChannelNameSettings.AxisCoalition));
        }

        [Fact]
        public void UpgradesAVersionOneDatabaseAndKeepsItsChannelNames()
        {
            using var temp = new TempDirectory();
            var path = temp.File(SrsDatabase.FileName);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL);
CREATE TABLE channel_names (channel INTEGER PRIMARY KEY, name TEXT NOT NULL);
INSERT INTO channel_names (channel, name) VALUES (1, 'Ops');
PRAGMA user_version = 1;";
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();
            var database = new SrsDatabase(path);
            Assert.False(database.WasCreated);

            var store = new ServerSettingsStore(database, NoOverrides);
            Assert.Equal("Ops", store.GetChannelNames()[1]);
            Assert.Empty(store.GetChannelNames(ChannelNameSettings.AlliesCoalition));

            store.SetChannelNames(ChannelNameSettings.AlliesCoalition, new Dictionary<int, string> { [1] = "Kenway" });
            Assert.Equal("Kenway", new ServerSettingsStore(database, NoOverrides).GetChannelNames(ChannelNameSettings.AlliesCoalition)[1]);

            using var check = database.Open();
            using var version = check.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.Equal(2L, (long)version.ExecuteScalar());
        }

        [Theory]
        [InlineData("allies", ChannelNameSettings.AlliesCoalition)]
        [InlineData("Axis", ChannelNameSettings.AxisCoalition)]
        [InlineData("AXIS", ChannelNameSettings.AxisCoalition)]
        public void ApiAcceptsCoalitionNames(string value, int expected)
        {
            Assert.True(Api.ApiEndpoints.TryParseCoalition(value, out var coalition));
            Assert.Equal(expected, coalition);
        }

        [Theory]
        [InlineData("shared")]
        [InlineData("1")]
        [InlineData("")]
        public void ApiRejectsOtherCoalitionNames(string value)
        {
            Assert.False(Api.ApiEndpoints.TryParseCoalition(value, out _));
        }

        [Fact]
        public void ImportsWpfServerConfigAndBans()
        {
            using var temp = new TempDirectory();
            System.IO.File.WriteAllText(temp.File("server.cfg"),
                "[General Settings]\nCOALITION_AUDIO_SECURITY=True\nCHANNEL_LIMIT=12\nCHANNEL_LIMIT_TYPO=1\n" +
                "[Server Settings]\nport=6005\nSERVER_UI_THEME=Dark\n" +
                "[Channel Names]\n1=Ops\n2=Strike\n");
            System.IO.File.WriteAllText(temp.File("banned.txt"), "203.0.113.7\r\nnot an ip\r\n198.51.100.9\r\n");

            var database = temp.CreateDatabase();
            Assert.True(database.WasCreated);
            var store = new ServerSettingsStore(database, NoOverrides);
            var bans = new BanStore(database);

            LegacyFileImporter.ImportServerConfig(temp.File("server.cfg"), store);
            LegacyFileImporter.ImportBannedIps(temp.File("banned.txt"), bans);

            Assert.True(store.Get(ServerSettingsKeys.COALITION_AUDIO_SECURITY).BoolValue);
            Assert.Equal(12, store.Get(ServerSettingsKeys.CHANNEL_LIMIT).IntValue);
            Assert.Equal(6005, store.GetServerPort());
            Assert.Equal("Ops", store.GetChannelNames()[1]);
            Assert.Equal(2, bans.ActiveCount);
            Assert.True(bans.Contains(System.Net.IPAddress.Parse("198.51.100.9")));
            Assert.False(temp.CreateDatabase().WasCreated);
        }
    }
}
