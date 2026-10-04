using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Api;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Tests
{
    /// <summary>
    /// Starts the real server (admin web host, SRS TCP and UDP listeners, SQLite) on free ports
    /// and talks to it the way an SRS client and an API consumer would.
    /// </summary>
    public class ServerEndToEndTests : IAsyncLifetime
    {
        private const string ClientGuid = "EndToEndClientGuid0001";

        private readonly TempDirectory _data = new TempDirectory();
        private WebApplication _app;
        private HttpClient _http;
        private int _srsPort;

        public async ValueTask InitializeAsync()
        {
            _srsPort = FindFreePort();
            ServerPaths.UseDataDirectory(_data.Path);
            File.WriteAllText(_data.File("server.cfg"), "[General Settings]\nCHANNEL_LIMIT=9\n[Channel Names]\n1=Ops\n");

            _app = Program.BuildApp(StartupArguments.Parse(new[]
            {
                "--urls=http://127.0.0.1:0",
                "--SRS_ADMIN_PASSWORD=correct-horse",
                "--SRS_SERVER_PORT=" + _srsPort
            }));
            await _app.StartAsync(TestContext.Current.CancellationToken);

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
        public async Task ApiRequiresAKeyWithTheRightScope()
        {
            var keys = _app.Services.GetRequiredService<ApiKeyStore>();
            var (_, readKey) = keys.Create("reader", ApiScope.Read);
            var token = TestContext.Current.CancellationToken;

            using (var anonymous = await _http.GetAsync("/api/v1/status", token))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            }

            using var request = Authorized(HttpMethod.Get, "/api/v1/status", readKey);
            using var status = await _http.SendAsync(request, token);
            status.EnsureSuccessStatusCode();
            var body = await status.Content.ReadFromJsonAsync<JsonElement>(token);
            Assert.Equal("RUNNING", body.GetProperty("status").GetString());
            Assert.Equal(_srsPort, body.GetProperty("port").GetInt32());

            using var write = Authorized(HttpMethod.Put, "/api/v1/settings/CHANNEL_LIMIT", readKey);
            write.Content = JsonContent.Create(new { value = "10" });
            using var forbidden = await _http.SendAsync(write, token);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        [Fact]
        public async Task SrsClientSeesImportedSettingsAndApiChanges()
        {
            var token = TestContext.Current.CancellationToken;
            var (_, writeKey) = _app.Services.GetRequiredService<ApiKeyStore>().Create("writer", ApiScope.Write);

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, _srsPort, token);
            var stream = tcp.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8);

            await SendAsync(stream, "{\"ServerType\":\"IL2-SRS\",\"Version\":\"1.0.4.12\",\"MsgType\":2," +
                                    "\"Client\":{\"ClientGuid\":\"" + ClientGuid + "\",\"Name\":\"Test Pilot\",\"Coalition\":1}}", token);
            var sync = await ReadMessageAsync(reader, token);
            Assert.Equal(2, sync.GetProperty("MsgType").GetInt32());
            Assert.Equal("9", sync.GetProperty("ServerSettings").GetProperty("CHANNEL_LIMIT").GetString());
            Assert.Equal("Ops", sync.GetProperty("ServerSettings").GetProperty("CHANNEL_NAME_1").GetString());

            using (var udp = new UdpClient(AddressFamily.InterNetwork))
            {
                var ping = Encoding.ASCII.GetBytes(ClientGuid);
                await udp.SendAsync(ping, new IPEndPoint(IPAddress.Loopback, _srsPort), token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var echo = await udp.ReceiveAsync(timeout.Token);
                Assert.Equal(ClientGuid, Encoding.ASCII.GetString(echo.Buffer));
            }

            using (var clients = Authorized(HttpMethod.Get, "/api/v1/clients", writeKey))
            using (var response = await _http.SendAsync(clients, token))
            {
                var list = await response.Content.ReadFromJsonAsync<JsonElement>(token);
                var client = Assert.Single(list.EnumerateArray());
                Assert.Equal("Test Pilot", client.GetProperty("name").GetString());
                Assert.Equal("Red", client.GetProperty("coalition").GetString());
                Assert.True(client.GetProperty("voiceLink").GetBoolean());
            }

            // A settings change through the API is pushed to connected clients.
            using (var change = Authorized(HttpMethod.Put, "/api/v1/settings/CHANNEL_LIMIT", writeKey))
            {
                change.Content = JsonContent.Create(new { value = "15" });
                using var response = await _http.SendAsync(change, token);
                response.EnsureSuccessStatusCode();
            }

            // Skip the UPDATE broadcast that follows a SYNC; the next SERVER_SETTINGS (4) carries the change.
            JsonElement pushed;
            do
            {
                pushed = await ReadMessageAsync(reader, token);
            } while (pushed.GetProperty("MsgType").GetInt32() != 4);

            Assert.Equal("15", pushed.GetProperty("ServerSettings").GetProperty("CHANNEL_LIMIT").GetString());

            // Banning through the API disconnects the client and blocks the address.
            using (var ban = Authorized(HttpMethod.Post, $"/api/v1/clients/{ClientGuid}/ban", writeKey))
            {
                ban.Content = JsonContent.Create(new { reason = "test", durationMinutes = 5 });
                using var response = await _http.SendAsync(ban, token);
                var record = await response.Content.ReadFromJsonAsync<JsonElement>(token);
                Assert.Equal("127.0.0.1", record.GetProperty("ipAddress").GetString());
                Assert.True(record.GetProperty("active").GetBoolean());
            }

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                Assert.Null(await reader.ReadLineAsync(timeout.Token));
            }

            await Task.Delay(1500, token);
            using var events = Authorized(HttpMethod.Get, "/api/v1/events?category=admin", writeKey);
            using var eventResponse = await _http.SendAsync(events, token);
            var eventList = await eventResponse.Content.ReadFromJsonAsync<JsonElement>(token);
            Assert.Contains(eventList.EnumerateArray(), e =>
                e.GetProperty("actor").GetString() == "api:writer" &&
                e.GetProperty("message").GetString().StartsWith("Banned Test Pilot (Red)", StringComparison.Ordinal));
        }

        private static HttpRequestMessage Authorized(HttpMethod method, string path, string key)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return request;
        }

        private static async Task SendAsync(NetworkStream stream, string json, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            await stream.WriteAsync(bytes, token);
        }

        private static async Task<JsonElement> ReadMessageAsync(StreamReader reader, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var line = await reader.ReadLineAsync(timeout.Token);
            Assert.NotNull(line);
            return JsonDocument.Parse(line).RootElement.Clone();
        }

        private static int FindFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
