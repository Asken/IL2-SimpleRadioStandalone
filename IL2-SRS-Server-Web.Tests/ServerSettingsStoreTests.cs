using System.Collections.Generic;
using Ciribob.IL2.SimpleRadio.Standalone.Common.Setting;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Hosting;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Network;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Settings;
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
