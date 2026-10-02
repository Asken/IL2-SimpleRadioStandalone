using System;
using System.Net.Http;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Hosting
{
    /// <summary>
    /// "IL2-SRS-Server --healthcheck": asks the running server's /healthz and exits 0 when healthy.
    /// Used by the Docker HEALTHCHECK, because the base image has no curl or wget.
    /// </summary>
    public static class HealthCheckCommand
    {
        public const string Argument = "--healthcheck";
        public const string UrlVariable = "SRS_HEALTHCHECK_URL";

        public static int Run()
        {
            var url = ResolveUrl(Environment.GetEnvironmentVariable(UrlVariable),
                Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"));
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                using var response = client.GetAsync(url).GetAwaiter().GetResult();
                Console.WriteLine($"{url}: {(int)response.StatusCode}");
                return response.IsSuccessStatusCode ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{url}: {ex.Message}");
                return 1;
            }
        }

        internal static string ResolveUrl(string configuredUrl, string httpPorts)
        {
            if (!string.IsNullOrWhiteSpace(configuredUrl))
            {
                return configuredUrl.Trim();
            }

            var port = (httpPorts ?? string.Empty).Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries |
                                                                             StringSplitOptions.TrimEntries);
            return $"http://127.0.0.1:{(port.Length > 0 ? port[0] : "8080")}/healthz";
        }
    }
}
