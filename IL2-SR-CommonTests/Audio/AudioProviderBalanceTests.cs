using System;
using Ciribob.IL2.SimpleRadio.Standalone.Client.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ciribob.IL2.SimpleRadio.Standalone.Common.Tests.Audio
{
    [TestClass]
    public class AudioProviderBalanceTests
    {
        private const short Amplitude = 10000;

        [TestMethod]
        public void BalanceKeepsCentreAndEndPoints()
        {
            AssertGains(0f, 0.5, 0.5);
            AssertGains(-1f, 1.0, 0.0);
            AssertGains(1f, 0.0, 1.0);
        }

        [TestMethod]
        public void MovingTheSliderRightShiftsAudioRightAcrossTheWholeRange()
        {
            var previousRight = -1.0;
            for (var step = -10; step <= 10; step++)
            {
                var (_, right) = Gains(step / 10f);
                Assert.IsTrue(right > previousRight,
                    $"Right gain must rise as the slider moves right; at {step / 10f} it was {right}, previously {previousRight}.");
                previousRight = right;
            }
        }

        [TestMethod]
        public void LeftAndRightBalanceAreMirrorImages()
        {
            for (var step = 1; step <= 10; step++)
            {
                var (leftOfLeftPan, rightOfLeftPan) = Gains(-step / 10f);
                var (leftOfRightPan, rightOfRightPan) = Gains(step / 10f);

                Assert.AreEqual(leftOfLeftPan, rightOfRightPan, 0.001, $"Mirror mismatch at {step / 10f}");
                Assert.AreEqual(rightOfLeftPan, leftOfRightPan, 0.001, $"Mirror mismatch at {step / 10f}");
                Assert.IsTrue(leftOfLeftPan > rightOfLeftPan, $"A left balance of {-step / 10f} must favour the left channel.");
            }
        }

        private static void AssertGains(float balance, double expectedLeft, double expectedRight)
        {
            var (left, right) = Gains(balance);
            Assert.AreEqual(expectedLeft, left, 0.001, $"Left gain at {balance}");
            Assert.AreEqual(expectedRight, right, 0.001, $"Right gain at {balance}");
        }

        private static (double left, double right) Gains(float balance)
        {
            var mono = new byte[4];
            for (var i = 0; i < 2; i++)
            {
                mono[i * 2] = (byte)(Amplitude & 0xFF);
                mono[i * 2 + 1] = (byte)(Amplitude >> 8);
            }

            var stereo = AudioProvider.CreateBalancedMix(mono, balance);
            return (BitConverter.ToInt16(stereo, 0) / (double)Amplitude,
                BitConverter.ToInt16(stereo, 2) / (double)Amplitude);
        }
    }
}
