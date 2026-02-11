using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Game.Security {
    /// <summary>
    /// Obfuscates JSON payloads and rejects tampered files via HMAC signature validation.
    /// Intended to prevent casual save editing, not to provide strong anti-cheat guarantees.
    /// </summary>
    public static class SecureJsonFile {
        public enum DecodeResult {
            Success,
            LegacyPlaintext,
            InvalidOrTampered
        }

        [Serializable]
        private sealed class ProtectedEnvelope {
            public int v = 1;
            public string salt;
            public string iv;
            public string payload;
            public string sig;
        }

        private const string Header = "HOPSEC1";
        private const string KeyFileName = "hop.savekey";
        private const string KeyPepper = "HOP_SAVE_PROTECTION_V1";

        public static string Encode(string logicalPath, string plainJson) {
            if (plainJson == null) {
                plainJson = string.Empty;
            }

            var masterSecret = GetOrCreateMasterSecret();
            var salt = CreateRandomBytes(16);
            var iv = CreateRandomBytes(16);
            var encryptionKey = DeriveEncryptionKey(masterSecret, salt, logicalPath);
            var signingKey = DeriveSigningKey(encryptionKey);

            var plainBytes = Encoding.UTF8.GetBytes(plainJson);
            var cipherBytes = EncryptAes(plainBytes, encryptionKey, iv);

            var envelope = new ProtectedEnvelope {
                v = 1,
                salt = Convert.ToBase64String(salt),
                iv = Convert.ToBase64String(iv),
                payload = Convert.ToBase64String(cipherBytes)
            };

            envelope.sig = ComputeSignatureBase64(signingKey, envelope);
            var envelopeJson = JsonUtility.ToJson(envelope, false);
            return $"{Header}:{envelopeJson}";
        }

        public static DecodeResult TryDecode(string logicalPath, string rawText, out string plainJson) {
            plainJson = null;
            if (string.IsNullOrWhiteSpace(rawText)) {
                return DecodeResult.InvalidOrTampered;
            }

            if (!rawText.StartsWith($"{Header}:", StringComparison.Ordinal)) {
                plainJson = rawText;
                return DecodeResult.LegacyPlaintext;
            }

            var envelopeJson = rawText[(Header.Length + 1)..];
            ProtectedEnvelope envelope;
            try {
                envelope = JsonUtility.FromJson<ProtectedEnvelope>(envelopeJson);
            } catch {
                return DecodeResult.InvalidOrTampered;
            }

            if (!TryValidateEnvelope(envelope)) {
                return DecodeResult.InvalidOrTampered;
            }

            try {
                var salt = Convert.FromBase64String(envelope.salt);
                var iv = Convert.FromBase64String(envelope.iv);
                var cipherBytes = Convert.FromBase64String(envelope.payload);
                var signature = Convert.FromBase64String(envelope.sig);

                var masterSecret = GetOrCreateMasterSecret();
                var encryptionKey = DeriveEncryptionKey(masterSecret, salt, logicalPath);
                var signingKey = DeriveSigningKey(encryptionKey);
                var expectedSignature = ComputeSignature(signingKey, envelope);

                if (!CryptographicOperations.FixedTimeEquals(signature, expectedSignature)) {
                    return DecodeResult.InvalidOrTampered;
                }

                var plainBytes = DecryptAes(cipherBytes, encryptionKey, iv);
                plainJson = Encoding.UTF8.GetString(plainBytes);
                return DecodeResult.Success;
            } catch {
                return DecodeResult.InvalidOrTampered;
            }
        }

        private static bool TryValidateEnvelope(ProtectedEnvelope envelope) {
            if (envelope == null) return false;
            if (envelope.v != 1) return false;
            if (string.IsNullOrWhiteSpace(envelope.salt)) return false;
            if (string.IsNullOrWhiteSpace(envelope.iv)) return false;
            if (string.IsNullOrWhiteSpace(envelope.payload)) return false;
            return !string.IsNullOrWhiteSpace(envelope.sig);
        }

        private static byte[] EncryptAes(byte[] plainBytes, byte[] key, byte[] iv) {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.KeySize = 256;
            aes.BlockSize = 128;
            aes.Key = key;
            aes.IV = iv;
            using var encryptor = aes.CreateEncryptor();
            return encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        }

        private static byte[] DecryptAes(byte[] cipherBytes, byte[] key, byte[] iv) {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.KeySize = 256;
            aes.BlockSize = 128;
            aes.Key = key;
            aes.IV = iv;
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        }

        private static string ComputeSignatureBase64(byte[] signingKey, ProtectedEnvelope envelope) {
            var sig = ComputeSignature(signingKey, envelope);
            return Convert.ToBase64String(sig);
        }

        private static byte[] ComputeSignature(byte[] signingKey, ProtectedEnvelope envelope) {
            var signingText = $"{envelope.v}|{envelope.salt}|{envelope.iv}|{envelope.payload}";
            var bytes = Encoding.UTF8.GetBytes(signingText);
            using var hmac = new HMACSHA256(signingKey);
            return hmac.ComputeHash(bytes);
        }

        private static byte[] DeriveEncryptionKey(byte[] masterSecret, byte[] salt, string logicalPath) {
            using var sha = SHA256.Create();
            var machineId = SystemInfo.deviceUniqueIdentifier ?? string.Empty;
            var context = $"{KeyPepper}|{Application.companyName}|{Application.productName}|{logicalPath}|{machineId}";
            var contextBytes = Encoding.UTF8.GetBytes(context);
            var input = Combine(masterSecret, salt, contextBytes);
            return sha.ComputeHash(input);
        }

        private static byte[] DeriveSigningKey(byte[] encryptionKey) {
            using var sha = SHA256.Create();
            var contextBytes = Encoding.UTF8.GetBytes("HOP_SAVE_HMAC_V1");
            return sha.ComputeHash(Combine(encryptionKey, contextBytes));
        }

        private static byte[] GetOrCreateMasterSecret() {
            var keyPath = Path.Combine(Application.persistentDataPath, KeyFileName);
            try {
                if (File.Exists(keyPath)) {
                    var stored = File.ReadAllText(keyPath).Trim();
                    var parsed = Convert.FromBase64String(stored);
                    if (parsed.Length >= 32) {
                        if (parsed.Length == 32) {
                            return parsed;
                        }

                        var key = new byte[32];
                        Buffer.BlockCopy(parsed, 0, key, 0, 32);
                        return key;
                    }
                }
            } catch {
            }

            var generated = CreateRandomBytes(32);
            try {
                var dir = Path.GetDirectoryName(keyPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(keyPath, Convert.ToBase64String(generated));
                TryHideFile(keyPath);
            } catch {
            }

            return generated;
        }

        private static void TryHideFile(string path) {
            try {
                var attributes = File.GetAttributes(path);
                File.SetAttributes(path, attributes | FileAttributes.Hidden);
            } catch {
                // ignored
            }
        }

        private static byte[] CreateRandomBytes(int size) {
            var bytes = new byte[size];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return bytes;
        }

        private static byte[] Combine(params byte[][] arrays) {
            var totalLength = 0;
            foreach(var t in arrays) {
                totalLength += t?.Length ?? 0;
            }

            var output = new byte[totalLength];
            var offset = 0;
            foreach(var source in arrays) {
                if (source == null || source.Length == 0) {
                    continue;
                }

                Buffer.BlockCopy(source, 0, output, offset, source.Length);
                offset += source.Length;
            }

            return output;
        }
    }
}
