using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Ciribob.IL2.SimpleRadio.Standalone.Client.Settings;
using Ciribob.IL2.SimpleRadio.Standalone.Client.Singletons;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ciribob.IL2.SimpleRadio.Standalone.Common.Tests.Settings
{
    [TestClass]
    public class RciAccessCodeTests
    {
        // Each test run makes its own throwaway key pair; no real signing key is ever involved.
        private static readonly DateTime Now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        private ECDsa _signer;
        private Dictionary<string, RciAccessKey> _keys;

        [TestInitialize]
        public void CreateKeys()
        {
            _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKey = _signer.ExportParameters(false);
            _keys = new Dictionary<string, RciAccessKey> { ["test-1"] = new RciAccessKey(publicKey.Q.X, publicKey.Q.Y) };
        }

        [TestCleanup]
        public void DisposeKeys()
        {
            _signer.Dispose();
        }

        [TestMethod]
        public void ValidCodeGivesCodenameCoalitionAndExpiry()
        {
            var result = RciAccessCode.Validate(Code("MAGIC", 1), Now, _keys);

            Assert.AreEqual(RciAccessCodeStatus.Valid, result.Status);
            Assert.AreEqual("MAGIC", result.Code.Codename);
            Assert.AreEqual("MAGIC___RCI", result.Code.RadioName);
            Assert.AreEqual((short)1, result.Code.Coalition);
            Assert.AreEqual("123456789", result.Code.Subject);
            Assert.AreEqual(Now.AddDays(7), result.Code.ExpiresUtc);
        }

        [TestMethod]
        public void SurroundingWhitespaceFromPastingIsIgnored()
        {
            Assert.IsTrue(RciAccessCode.Validate("  " + Code("MAGIC", 2) + "\r\n", Now, _keys).IsValid);
        }

        [TestMethod]
        public void ChangingAnyPartOfTheCodeIsRejected()
        {
            var code = Code("MAGIC", 1);
            var parts = code.Split('.');

            var forgedPayload = parts[0] + "." + Payload("MAGIC", 2) + "." + parts[2];
            Assert.AreEqual(RciAccessCodeStatus.BadSignature, RciAccessCode.Validate(forgedPayload, Now, _keys).Status);

            var signature = Convert.FromBase64String(Pad(parts[2].Replace('-', '+').Replace('_', '/')));
            signature[10] ^= 0x01;
            var forgedSignature = parts[0] + "." + parts[1] + "." + RciAccessCode.EncodeBase64Url(signature);
            Assert.AreEqual(RciAccessCodeStatus.BadSignature, RciAccessCode.Validate(forgedSignature, Now, _keys).Status);
        }

        [TestMethod]
        public void CodesSignedWithAnotherKeyAreRejected()
        {
            using (var otherSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                var code = Code("MAGIC", 1, signer: otherSigner);
                Assert.AreEqual(RciAccessCodeStatus.BadSignature, RciAccessCode.Validate(code, Now, _keys).Status);
            }
        }

        [TestMethod]
        public void KeyIdMustMatchTheKeyThatSignedIt()
        {
            var code = Code("MAGIC", 1, keyId: "test-2");
            Assert.AreEqual(RciAccessCodeStatus.UnknownKey, RciAccessCode.Validate(code, Now, _keys).Status);
        }

        [TestMethod]
        public void MalformedCodesAreRejected()
        {
            var parts = Code("MAGIC", 1).Split('.');
            Assert.AreEqual(RciAccessCodeStatus.Missing, RciAccessCode.Validate("  ", Now, _keys).Status);
            Assert.AreEqual(RciAccessCodeStatus.Malformed, RciAccessCode.Validate("RCI2." + parts[1] + "." + parts[2], Now, _keys).Status);
            Assert.AreEqual(RciAccessCodeStatus.Malformed, RciAccessCode.Validate("RCI1." + parts[1], Now, _keys).Status);
            Assert.AreEqual(RciAccessCodeStatus.Malformed, RciAccessCode.Validate("RCI1." + parts[1] + "." + parts[2] + ".x", Now, _keys).Status);
            Assert.AreEqual(RciAccessCodeStatus.Malformed, RciAccessCode.Validate("RCI1.!!!." + parts[2], Now, _keys).Status);
        }

        [TestMethod]
        public void NoCodeIsAcceptedUntilAKeyIsBuiltIn()
        {
            Assert.AreEqual(RciAccessCodeStatus.NoKeysInBuild,
                RciAccessCode.Validate(Code("MAGIC", 1), Now, new Dictionary<string, RciAccessKey>()).Status);
        }

        [TestMethod]
        public void ExpiredCodesAreRejectedAfterAFewMinutesOfClockSkew()
        {
            var code = Code("MAGIC", 1);
            var expiry = Now.AddDays(7);

            Assert.IsTrue(RciAccessCode.Validate(code, expiry, _keys).IsValid);
            Assert.IsTrue(RciAccessCode.Validate(code, expiry.AddMinutes(4), _keys).IsValid);

            var expired = RciAccessCode.Validate(code, expiry.AddMinutes(6), _keys);
            Assert.AreEqual(RciAccessCodeStatus.Expired, expired.Status);
            Assert.AreEqual("MAGIC", expired.Code.Codename, "An expired code still shows whose it was.");
        }

        [TestMethod]
        public void OnlyAlliesOrAxisAndProperCodenamesAreAccepted()
        {
            Assert.AreEqual(RciAccessCodeStatus.InvalidContent, RciAccessCode.Validate(Code("MAGIC", 0), Now, _keys).Status);
            Assert.AreEqual(RciAccessCodeStatus.InvalidContent, RciAccessCode.Validate(Code("MAGIC", 3), Now, _keys).Status);
            Assert.AreEqual(RciAccessCodeStatus.InvalidContent, RciAccessCode.Validate(Code("magic", 1), Now, _keys).Status);
            Assert.AreEqual(RciAccessCodeStatus.InvalidContent, RciAccessCode.Validate(Code("MAGIC___RCI", 1), Now, _keys).Status);
            Assert.AreEqual(RciAccessCodeStatus.InvalidContent, RciAccessCode.Validate(Code(new string('A', 21), 1), Now, _keys).Status);
        }

        [TestMethod]
        public void ASignedPayloadThatIsNotJsonIsRejected()
        {
            var payload = RciAccessCode.EncodeBase64Url(Encoding.UTF8.GetBytes("not json"));
            Assert.AreEqual(RciAccessCodeStatus.InvalidContent, RciAccessCode.Validate(Sign(payload, _signer), Now, _keys).Status);
        }

        [TestMethod]
        public void AcceptedCodenamesAreRecognisedAsRciByPilotsClients()
        {
            var tryGetRciCallsign = typeof(ConnectedClientsSingleton).GetMethod("TryGetRciCallsign", BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var codename in new[] { "MAGIC", "SPYGLASS", "BIG EYE", "RED 2", "A" })
            {
                Assert.IsTrue(RciAccessCode.IsValidCodename(codename), codename);
                var args = new object[] { codename + "___RCI", null };
                Assert.IsTrue((bool)tryGetRciCallsign.Invoke(null, args), codename);
                Assert.AreEqual(codename, args[1], codename);
            }

            foreach (var codename in new[] { "", " MAGIC", "MAGIC ", "BIG  EYE", "MAGIC_RCI", "MAGÏC" })
            {
                Assert.IsFalse(RciAccessCode.IsValidCodename(codename), codename);
            }
        }

        [TestMethod]
        public void Base64UrlDecodesUnpaddedValues()
        {
            for (var length = 0; length < 8; length++)
            {
                var bytes = new byte[length];
                for (var i = 0; i < length; i++)
                {
                    bytes[i] = (byte)(250 - i * 37);
                }

                var encoded = RciAccessCode.EncodeBase64Url(bytes);
                Assert.IsFalse(encoded.Contains("=") || encoded.Contains("+") || encoded.Contains("/"), encoded);
                if (length == 0)
                {
                    continue;
                }

                Assert.IsTrue(RciAccessCode.TryDecodeBase64Url(encoded, out var decoded), encoded);
                CollectionAssert.AreEqual(bytes, decoded);
            }

            Assert.IsFalse(RciAccessCode.TryDecodeBase64Url("abc=", out _));
            Assert.IsFalse(RciAccessCode.TryDecodeBase64Url("a", out _));
        }

        private string Code(string codename, int coalition, string keyId = "test-1", ECDsa signer = null)
        {
            return Sign(Payload(codename, coalition, keyId), signer ?? _signer);
        }

        private static string Payload(string codename, int coalition, string keyId = "test-1")
        {
            var issued = new DateTimeOffset(Now).ToUnixTimeSeconds();
            var json = "{\"kid\":\"" + keyId + "\",\"sub\":\"123456789\",\"cs\":\"" + codename + "\",\"co\":" + coalition +
                       ",\"iat\":" + issued + ",\"exp\":" + (issued + 7 * 24 * 3600) + "}";
            return RciAccessCode.EncodeBase64Url(Encoding.UTF8.GetBytes(json));
        }

        private static string Sign(string payload, ECDsa signer)
        {
            var signature = signer.SignData(Encoding.ASCII.GetBytes(RciAccessCode.Prefix + payload), HashAlgorithmName.SHA256);
            return RciAccessCode.Prefix + payload + "." + RciAccessCode.EncodeBase64Url(signature);
        }

        private static string Pad(string base64)
        {
            return base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        }
    }
}
