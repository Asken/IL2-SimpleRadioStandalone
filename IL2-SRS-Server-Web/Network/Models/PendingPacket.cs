using System.Net;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Network
{
    public class PendingPacket
    {
        public IPEndPoint ReceivedFrom { get; set; }
        public byte[] RawBytes { get; set; }
    }
}