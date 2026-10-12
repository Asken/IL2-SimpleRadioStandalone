using System.Threading;
using System.Threading.Tasks;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Events;
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
        private readonly ServerEvents _events;

        public SrsServerHostedService(ServerState serverState, ServerEvents events, ClientEventRecorder recorder)
        {
            // The recorder is resolved here so client events are recorded from the first connection.
            _serverState = serverState;
            _events = events;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _serverState.Start();
            _events.Publish(SrsEventTypes.ServerStarted, "system", $"Server started on port {_serverState.Port}",
                EventPayload.Server(_serverState.Port, 0));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (_serverState.IsRunning)
            {
                var payload = EventPayload.Server(_serverState.Port, _serverState.GetClients().Count);
                _serverState.Stop();
                _events.Publish(SrsEventTypes.ServerStopped, "system", "Server shut down", payload);
            }

            return Task.CompletedTask;
        }
    }
}
