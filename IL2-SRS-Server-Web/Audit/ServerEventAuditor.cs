using Ciribob.IL2.SimpleRadio.Standalone.Common.Network;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Admin;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Network;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Audit
{
    /// <summary>Records client connections, disconnections and refused connections in the event log.</summary>
    public sealed class ServerEventAuditor
    {
        private readonly AuditLog _audit;

        public ServerEventAuditor(ServerEvents events, AuditLog audit)
        {
            _audit = audit;
            events.ClientConnected += OnClientConnected;
            events.ClientDisconnected += OnClientDisconnected;
            events.ConnectionRejected += OnConnectionRejected;
        }

        private void OnClientConnected(SRClient client, string ipAddress)
        {
            _audit.Write(AuditCategory.Client, "system", $"{ServerAdminService.Describe(client)} connected from {ipAddress}");
        }

        private void OnClientDisconnected(SRClient client)
        {
            _audit.Write(AuditCategory.Client, "system", $"{ServerAdminService.Describe(client)} disconnected");
        }

        private void OnConnectionRejected(string ipAddress, string reason)
        {
            _audit.Write(AuditCategory.Client, "system", $"Refused connection from {ipAddress}: {reason}");
        }
    }
}
