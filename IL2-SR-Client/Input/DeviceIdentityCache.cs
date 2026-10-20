using System;
using System.Runtime.CompilerServices;

namespace Ciribob.IL2.SimpleRadio.Standalone.Client.Settings
{
    /// <summary>A DirectInput device's product GUID and name.</summary>
    internal sealed class DeviceIdentity
    {
        public DeviceIdentity(Guid productGuid, string productName)
        {
            ProductGuid = productGuid;
            ProductName = productName;
        }

        public Guid ProductGuid { get; }

        public string ProductName { get; }
    }

    /// <summary>
    /// Reads each device's identity once and reuses it. Reading it asks DirectInput (GetDeviceInfo), which takes
    /// around 10 ms per call, and while a bound device is missing the PTT loop matches that binding against every
    /// device on every poll. A device object's identity never changes; rediscovery creates a new object, which is
    /// read again. A read that throws is not remembered, so it is tried again next time.
    /// </summary>
    internal sealed class DeviceIdentityCache<TDevice> where TDevice : class
    {
        private readonly ConditionalWeakTable<TDevice, DeviceIdentity> _identities =
            new ConditionalWeakTable<TDevice, DeviceIdentity>();

        private readonly ConditionalWeakTable<TDevice, DeviceIdentity>.CreateValueCallback _read;

        public DeviceIdentityCache(Func<TDevice, DeviceIdentity> read)
        {
            _read = device => read(device);
        }

        public DeviceIdentity Get(TDevice device)
        {
            return _identities.GetValue(device, _read);
        }
    }
}
