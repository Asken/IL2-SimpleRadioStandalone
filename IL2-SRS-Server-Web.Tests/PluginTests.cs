using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Api;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Data;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Hosting;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Integrations;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Tests
{
    /// <summary>Tests that start a server share ServerPaths, so they run one at a time.</summary>
    [CollectionDefinition(Name)]
    public class ServerCollection
    {
        public const string Name = "Server";
    }

    /// <summary>
    /// Installs the integrations plugin the way an admin would (a folder under plugins/ in the data directory)
    /// next to a server database from before the plugin, and starts the real server.
    /// </summary>
    [Collection(ServerCollection.Name)]
    public class PluginLoadingTests : IAsyncLifetime
    {
        private const string PluginName = "IL2-SRS-Plugin-Integrations";

        private readonly TempDirectory _data = new TempDirectory();
        private WebApplication _app;
        private HttpClient _http;
        private string _key;

        public async ValueTask InitializeAsync()
        {
            ServerPaths.UseDataDirectory(_data.Path);
            CreateServerDatabaseWithIntegrations(_data.File(SrsDatabase.FileName));

            // The plugin's build output: its assembly and wwwroot (Scriban and the SDK come from the server).
            var built = Path.GetDirectoryName(typeof(IntegrationsPlugin).Assembly.Location)!;
            var folder = Directory.CreateDirectory(Path.Combine(_data.Path, ServerPlugins.FolderName, PluginName)).FullName;
            File.Copy(Path.Combine(built, PluginName + ".dll"), Path.Combine(folder, PluginName + ".dll"));
            Directory.CreateDirectory(Path.Combine(folder, "wwwroot"));
            File.Copy(Path.Combine(built, "wwwroot", "integrations.css"), Path.Combine(folder, "wwwroot", "integrations.css"));
            Directory.CreateDirectory(Path.Combine(_data.Path, ServerPlugins.FolderName, "Broken"));

            _app = Program.BuildApp(StartupArguments.Parse(new[]
            {
                "--urls=http://127.0.0.1:0",
                "--SRS_ADMIN_PASSWORD=correct-horse",
                "--SRS_SERVER_PORT=" + ServerEndToEndTests.FindFreePort()
            }));
            await _app.StartAsync(TestContext.Current.CancellationToken);

            _key = _app.Services.GetRequiredService<ApiKeyStore>().Create("reader", ApiScope.Read).Key;
            var address = _app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>().Addresses.First();
            _http = new HttpClient { BaseAddress = new Uri(address) };
        }

        public async ValueTask DisposeAsync()
        {
            _http?.Dispose();
            if (_app != null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }

            _data.Dispose();
        }

        [Fact]
        public async Task LoadsPluginFoldersAndReportsTheOnesItCannot()
        {
            var token = TestContext.Current.CancellationToken;
            using var request = ServerEndToEndTests.Authorized(HttpMethod.Get, "/api/v1/plugins", _key);
            using var response = await _http.SendAsync(request, token);
            var plugins = await response.Content.ReadFromJsonAsync<JsonElement>(token);

            var loaded = Assert.Single(plugins.GetProperty("loaded").EnumerateArray());
            Assert.Equal("integrations", loaded.GetProperty("id").GetString());
            Assert.Equal("1.0.0", loaded.GetProperty("version").GetString());
            var failed = Assert.Single(plugins.GetProperty("failed").EnumerateArray());
            Assert.EndsWith("Broken", failed.GetProperty("source").GetString());
            Assert.Equal("No Broken.dll in the plugin folder.", failed.GetProperty("error").GetString());

            // Its pages, stylesheet, event types and API come with it.
            using var css = await _http.GetAsync("/plugins/integrations/integrations.css", token);
            Assert.Equal(HttpStatusCode.OK, css.StatusCode);
            using var types = ServerEndToEndTests.Authorized(HttpMethod.Get, "/api/v1/event-types", _key);
            using var typesResponse = await _http.SendAsync(types, token);
            var typeList = await typesResponse.Content.ReadFromJsonAsync<JsonElement>(token);
            Assert.Contains(typeList.EnumerateArray(), t => t.GetProperty("type").GetString() == "admin.integration_created");
            using var page = await _http.GetAsync("/integrations", token);
            Assert.Equal("/login", page.RequestMessage!.RequestUri!.AbsolutePath);
        }

        [Fact]
        public async Task MovesIntegrationsFromTheServerDatabase()
        {
            var token = TestContext.Current.CancellationToken;
            using var request = ServerEndToEndTests.Authorized(HttpMethod.Get, "/api/v1/integrations", _key);
            using var response = await _http.SendAsync(request, token);
            var integration = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>(token)).EnumerateArray());
            Assert.Equal(7, integration.GetProperty("id").GetInt64());
            Assert.Equal("Post joins", integration.GetProperty("name").GetString());

            Assert.True(File.Exists(Path.Combine(_data.Path, ServerPlugins.DataFolderName, IntegrationsPlugin.PluginId,
                IntegrationDatabase.FileName)));
            using var connection = _app.Services.GetRequiredService<SrsDatabase>().Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'integration%';";
            Assert.Equal(0L, (long)command.ExecuteScalar());
        }

        /// <summary>A srs.db as this fork's servers left it before the plugin: version 5, integrations included.</summary>
        private static void CreateServerDatabaseWithIntegrations(string path)
        {
            using (var connection = new SqliteConnection("Data Source=" + path))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
CREATE TABLE integrations (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, description TEXT,
    enabled INTEGER NOT NULL, trigger_type TEXT NOT NULL, conditions TEXT NOT NULL, action_type TEXT NOT NULL,
    action_config TEXT NOT NULL, retry_mode TEXT NOT NULL, max_retries INTEGER NOT NULL, created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL);
CREATE TABLE integration_executions (id INTEGER PRIMARY KEY AUTOINCREMENT, integration_id INTEGER NOT NULL,
    integration_name TEXT NOT NULL, action_type TEXT NOT NULL, event_type TEXT NOT NULL, event_message TEXT,
    data TEXT NOT NULL, status TEXT NOT NULL, attempts INTEGER NOT NULL, created_at TEXT NOT NULL,
    next_attempt_at TEXT, finished_at TEXT, last_error TEXT, result TEXT);
CREATE TABLE integration_state (integration_id INTEGER NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL,
    updated_at TEXT NOT NULL, PRIMARY KEY (integration_id, key));
INSERT INTO integrations VALUES (7, 'Post joins', NULL, 1, 'client.connected', '[]', 'http-webhook',
    '{""url"":null}', 'none', 0, '2026-10-12T18:00:00.0000000Z', '2026-10-12T18:00:00.0000000Z');
PRAGMA user_version = 5;";
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();
        }
    }
}
