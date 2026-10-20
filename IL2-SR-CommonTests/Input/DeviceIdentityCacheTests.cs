using System;
using Ciribob.IL2.SimpleRadio.Standalone.Client.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ciribob.IL2.SimpleRadio.Standalone.Common.Tests.Input
{
    [TestClass]
    public class DeviceIdentityCacheTests
    {
        private sealed class FakeDevice
        {
            public FakeDevice(string name)
            {
                Name = name;
                ProductGuid = Guid.NewGuid();
            }

            public string Name { get; }
            public Guid ProductGuid { get; }
            public bool Fail { get; set; }
            public int Reads { get; set; }
        }

        private static DeviceIdentityCache<FakeDevice> CreateCache()
        {
            return new DeviceIdentityCache<FakeDevice>(device =>
            {
                device.Reads++;
                if (device.Fail)
                {
                    throw new InvalidOperationException("device info unavailable");
                }

                return new DeviceIdentity(device.ProductGuid, device.Name);
            });
        }

        [TestMethod]
        public void ReadsEachDeviceOnceHoweverOftenItIsMatched()
        {
            var cache = CreateCache();
            var joystick = new FakeDevice("T.16000M");

            for (var poll = 0; poll < 100; poll++)
            {
                Assert.AreEqual("T.16000M", cache.Get(joystick).ProductName);
                Assert.AreEqual(joystick.ProductGuid, cache.Get(joystick).ProductGuid);
            }

            Assert.AreEqual(1, joystick.Reads,
                "A missing binding is matched against every device on every PTT poll, so its identity must not be read from DirectInput each time.");
        }

        [TestMethod]
        public void KeepsDevicesApart()
        {
            var cache = CreateCache();
            var joystick = new FakeDevice("T.16000M");
            var throttle = new FakeDevice("TWCS Throttle");

            Assert.AreEqual("T.16000M", cache.Get(joystick).ProductName);
            Assert.AreEqual("TWCS Throttle", cache.Get(throttle).ProductName);
            Assert.AreEqual(throttle.ProductGuid, cache.Get(throttle).ProductGuid);
        }

        [TestMethod]
        public void AFailedReadIsNotRememberedAndIsTriedAgain()
        {
            var cache = CreateCache();
            var joystick = new FakeDevice("T.16000M") { Fail = true };

            try
            {
                cache.Get(joystick);
                Assert.Fail("Expected the failed read to be reported.");
            }
            catch (InvalidOperationException)
            {
            }

            joystick.Fail = false;
            Assert.AreEqual("T.16000M", cache.Get(joystick).ProductName);
            Assert.AreEqual(2, joystick.Reads);
        }

        [TestMethod]
        public void AReconnectedDeviceObjectIsReadAgain()
        {
            var cache = CreateCache();
            var before = new FakeDevice("T.16000M");
            var after = new FakeDevice("T.16000M Joystick");

            Assert.AreEqual("T.16000M", cache.Get(before).ProductName);
            Assert.AreEqual("T.16000M Joystick", cache.Get(after).ProductName,
                "Rediscovery creates a new device object, which may report a different name after a driver change.");
        }
    }
}
