using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Ciribob.IL2.SimpleRadio.Standalone.Client.Settings
{
    /// <summary>
    /// Personal access codes for RCI Controller mode, issued by the Combat Box RCI dashboard (rci-web).
    ///
    /// Format: RCI1.&lt;payload&gt;.&lt;signature&gt;
    ///   payload   = base64url (no padding) of UTF-8 JSON:
    ///               { "kid": key id, "sub": Discord id, "cs": codename, "co": 1 Allies / 2 Axis,
    ///                 "iat": issued (Unix seconds), "exp": expires (Unix seconds) }
    ///   signature = base64url of ECDSA P-256 / SHA-256 over the ASCII bytes of "RCI1." + payload,
    ///               in IEEE P1363 form (r || s, 64 bytes).
    ///
    /// The client holds only public keys, so this repository and the exe contain nothing that can create a
    /// code. rci-web holds the private key.
    /// </summary>
    public sealed class RciAccessCode
    {
        public const string Prefix = "RCI1.";
        public const int MaximumCodenameLength = 20;

        // Codes may be checked on a PC whose clock is slightly behind rci-web's.
        private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
        private static readonly Regex CodenamePattern = new Regex("^[A-Z0-9]+( [A-Z0-9]+)*$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Public keys trusted for access codes, by key id. Each value is the P-256 public point (X, Y) as base64.
        /// Empty until rci-web's signing key is created; until then no code can be accepted.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, RciAccessKey> TrustedKeys = CreateTrustedKeys();

        private static Dictionary<string, RciAccessKey> CreateTrustedKeys()
        {
            var keys = new Dictionary<string, RciAccessKey>();
#if DEBUG
            // Debug builds only: IL2_SRS_RCI_DEV_KEY = "<kid>:<x base64>:<y base64>" trusts a test key, so the
            // mode can be tried before rci-web issues real codes. Release builds never read it.
            var devKey = Environment.GetEnvironmentVariable("IL2_SRS_RCI_DEV_KEY");
            var parts = devKey?.Split(':');
            if (parts != null && parts.Length == 3)
            {
                keys[parts[0]] = RciAccessKey.FromBase64(parts[1], parts[2]);
            }
#endif
            return keys;
        }

        private RciAccessCode(string keyId, string subject, string codename, short coalition, DateTime issuedUtc, DateTime expiresUtc)
        {
            KeyId = keyId;
            Subject = subject;
            Codename = codename;
            Coalition = coalition;
            IssuedUtc = issuedUtc;
            ExpiresUtc = expiresUtc;
        }

        public string KeyId { get; }
        public string Subject { get; }
        public string Codename { get; }
        public short Coalition { get; }
        public DateTime IssuedUtc { get; }
        public DateTime ExpiresUtc { get; }

        /// <summary>The name pilots see: the codename followed by ___RCI, which their clients recognise.</summary>
        public string RadioName => Codename + "___RCI";

        public static RciAccessCodeResult Validate(string code, DateTime utcNow)
        {
            return Validate(code, utcNow, TrustedKeys);
        }

        internal static RciAccessCodeResult Validate(string code, DateTime utcNow, IReadOnlyDictionary<string, RciAccessKey> trustedKeys)
        {
            code = (code ?? string.Empty).Trim();
            if (code.Length == 0)
            {
                return RciAccessCodeResult.Fail(RciAccessCodeStatus.Missing);
            }

            if (trustedKeys == null || trustedKeys.Count == 0)
            {
                return RciAccessCodeResult.Fail(RciAccessCodeStatus.NoKeysInBuild);
            }

            var parts = code.Split('.');
            if (!code.StartsWith(Prefix, StringComparison.Ordinal) || parts.Length != 3)
            {
                return RciAccessCodeResult.Fail(RciAccessCodeStatus.Malformed);
            }

            byte[] payloadBytes;
            byte[] signature;
            if (!TryDecodeBase64Url(parts[1], out payloadBytes) || !TryDecodeBase64Url(parts[2], out signature))
            {
                return RciAccessCodeResult.Fail(RciAccessCodeStatus.Malformed);
            }

            // Verify before reading anything from the payload. There are only one or two trusted keys (two during
            // a key change), so try each rather than trusting a key id from unverified data.
            var signedBytes = Encoding.ASCII.GetBytes(Prefix + parts[1]);
            string verifyingKeyId = null;
            foreach (var trusted in trustedKeys)
            {
                if (trusted.Value.Verify(signedBytes, signature))
                {
                    verifyingKeyId = trusted.Key;
                    break;
                }
            }

            if (verifyingKeyId == null)
            {
                return RciAccessCodeResult.Fail(RciAccessCodeStatus.BadSignature);
            }

            JObject payload;
            try
            {
                payload = JObject.Parse(Encoding.UTF8.GetString(payloadBytes));
            }
            catch (Exception)
            {
                return RciAccessCodeResult.Fail(RciAccessCodeStatus.InvalidContent);
            }

            var keyId = (string)payload["kid"];
            if (!string.Equals(keyId, verifyingKeyId, StringComparison.Ordinal))
            {
                return RciAccessCodeResult.Fail(RciAccessCodeStatus.UnknownKey);
            }

            string subject;
            string codename;
            short coalition;
            DateTime issued;
            DateTime expires;
            try
            {
                subject = (string)payload["sub"];
                codename = (string)payload["cs"];
                coalition = (short)payload["co"];
                issued = DateTimeOffset.FromUnixTimeSeconds((long)payload["iat"]).UtcDateTime;
                expires = DateTimeOffset.FromUnixTimeSeconds((long)payload["exp"]).UtcDateTime;
            }
            catch (Exception)
            {
                return RciAccessCodeResult.Fail(RciAccessCodeStatus.InvalidContent);
            }

            if (!IsValidCodename(codename) || (coalition != 1 && coalition != 2))
            {
                return RciAccessCodeResult.Fail(RciAccessCodeStatus.InvalidContent);
            }

            var accessCode = new RciAccessCode(keyId, subject, codename, coalition, issued, expires);
            return utcNow > expires + ClockSkew
                ? RciAccessCodeResult.Fail(RciAccessCodeStatus.Expired, accessCode)
                : RciAccessCodeResult.Success(accessCode);
        }

        /// <summary>Upper-case letters and digits, single spaces between words, 1-20 characters.</summary>
        public static bool IsValidCodename(string codename)
        {
            return !string.IsNullOrEmpty(codename) &&
                   codename.Length <= MaximumCodenameLength &&
                   CodenamePattern.IsMatch(codename);
        }

        internal static bool TryDecodeBase64Url(string value, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrEmpty(value) || value.IndexOf('=') >= 0)
            {
                return false;
            }

            var base64 = value.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2:
                    base64 += "==";
                    break;
                case 3:
                    base64 += "=";
                    break;
                case 1:
                    return false;
            }

            try
            {
                bytes = Convert.FromBase64String(base64);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        internal static string EncodeBase64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }

    /// <summary>A P-256 public key that access codes can be verified against.</summary>
    public sealed class RciAccessKey
    {
        private readonly ECParameters _parameters;

        public RciAccessKey(byte[] x, byte[] y)
        {
            _parameters = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = x, Y = y }
            };
        }

        public static RciAccessKey FromBase64(string x, string y)
        {
            return new RciAccessKey(Convert.FromBase64String(x), Convert.FromBase64String(y));
        }

        internal bool Verify(byte[] data, byte[] signature)
        {
            if (signature == null || signature.Length != 64)
            {
                return false;
            }

            try
            {
                using (var ecdsa = ECDsa.Create(_parameters))
                {
                    return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
                }
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }

    public enum RciAccessCodeStatus
    {
        Valid,
        Missing,
        NoKeysInBuild,
        Malformed,
        UnknownKey,
        BadSignature,
        InvalidContent,
        Expired
    }

    public sealed class RciAccessCodeResult
    {
        private RciAccessCodeResult(RciAccessCodeStatus status, RciAccessCode code)
        {
            Status = status;
            Code = code;
        }

        public RciAccessCodeStatus Status { get; }

        /// <summary>The decoded code when its signature was valid (also set when it has expired).</summary>
        public RciAccessCode Code { get; }

        public bool IsValid => Status == RciAccessCodeStatus.Valid;

        internal static RciAccessCodeResult Success(RciAccessCode code) => new RciAccessCodeResult(RciAccessCodeStatus.Valid, code);

        internal static RciAccessCodeResult Fail(RciAccessCodeStatus status, RciAccessCode code = null) => new RciAccessCodeResult(status, code);
    }
}
