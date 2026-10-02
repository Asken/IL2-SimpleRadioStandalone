using System.Threading;
using System.Threading.Tasks;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Audit;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Network;
using Microsoft.Extensions.Hosting;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Hosting
{
    /// <summary>
    /// Starts the SRS server with the host. If the port cannot be bound the exception
    /// stops the host, so the process exits with an error instead of running half-up.
    /// </summary>
    public sealed class SrsServerHostedService : IHostedService
    {
        private readonly ServerState _serverState;
        private readonly AuditLog _audit;

        public SrsServerHostedService(ServerState serverState, AuditLog audit, ServerEventAuditor auditor)
        {
            // The auditor is resolved here so client events are recorded from the first connection.
            _serverState = serverState;
            _audit = audit;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _serverState.Start();
            _audit.Write(AuditCategory.Server, "system", $"Server started on port {_serverState.Port}");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (_serverState.IsRunning)
            {
                _serverState.Stop();
                _audit.Write(AuditCategory.Server, "system", "Server shut down");
            }

            return Task.CompletedTask;
        }
    }
}
