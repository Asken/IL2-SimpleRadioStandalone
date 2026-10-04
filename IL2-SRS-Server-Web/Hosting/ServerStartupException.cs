using System;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Hosting
{
    /// <summary>
    /// A problem the operator can fix (missing password, invalid setting, port in use).
    /// Logged as a single line without a stack trace.
    /// </summary>
    public sealed class ServerStartupException : Exception
    {
        public ServerStartupException(string message, Exception innerException = null) : base(message, innerException)
        {
        }
    }
}
