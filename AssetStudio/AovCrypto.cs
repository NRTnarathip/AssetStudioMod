using System;
using System.Security.Cryptography;

namespace AssetStudio
{
    /// <summary>
    /// Crypto helpers for Arena of Valor (AOV) asset bundles.
    ///
    /// AOV keeps the stock UnityFS container layout but adds two layers of
    /// encryption on top of it:
    ///   * the 16 byte header (size / compressed size / uncompressed size) is
    ///     shuffled and then AES-128-CBC encrypted,
    ///   * the blocks info and (optionally) individual data blocks are
    ///     SM4-CBC encrypted before their normal LZ4 compression.
    ///
    /// Keys mirror the reference implementation (UABE_AOV / RovModKit).
    /// </summary>
    public static class AovCrypto
    {
        private static readonly byte[] HeaderAesKey = HexToBytes("e3056214d60a202536961b0774dc2402");
        private static readonly byte[] HeaderAesIv = HexToBytes("1d6eeb4c86a94544457212212b43252f");
        private static readonly byte[] BlockSm4Key = HexToBytes("797bcd5d7d7bb11143d00d713cdaa808");
        private static readonly byte[] BlockSm4Iv = HexToBytes("797bcd5d7d7bb11143d00d713cdaa808");

        /// <summary>Block flag that marks a payload as SM4 encrypted.</summary>
        public const uint BlockEncryptedFlag = 0x200;

        private static byte[] HexToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        /// <summary>
        /// Decrypt the 16 byte UnityFS header. Returns false when the data does
        /// not look like a valid AOV header (so callers can fall back to the
        /// stock reader).
        /// </summary>
        public static bool TryDecryptHeader(byte[] encrypted, long streamLength,
            out long bundleSize, out uint compressedSize, out uint uncompressedSize)
        {
            bundleSize = 0;
            compressedSize = 0;
            uncompressedSize = 0;

            if (encrypted == null || encrypted.Length < 16)
                return false;

            byte[] plain;
            try
            {
                // AOV shuffles the header before encrypting it.
                var shuffled = new byte[16];
                for (var i = 0; i < 8; i++)
                    shuffled[i] = encrypted[7 - i];
                for (var i = 0; i < 4; i++)
                    shuffled[8 + i] = encrypted[11 - i];
                for (var i = 0; i < 4; i++)
                    shuffled[12 + i] = encrypted[15 - i];

                using (var aes = Aes.Create())
                {
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.None;
                    aes.Key = HeaderAesKey;
                    aes.IV = HeaderAesIv;
                    using (var decryptor = aes.CreateDecryptor())
                    {
                        plain = decryptor.TransformFinalBlock(shuffled, 0, 16);
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }

            if (plain == null || plain.Length != 16)
                return false;

            var size = BitConverter.ToInt64(plain, 0);
            var compressed = BitConverter.ToUInt32(plain, 8);
            var uncompressed = BitConverter.ToUInt32(plain, 12);

            // Validate the decrypted values against the actual stream so that a
            // random (non AOV) header is not misdetected as encrypted.
            if (size <= 0 || size > streamLength)
                return false;
            if (compressed == 0 || compressed > streamLength)
                return false;
            if (uncompressed == 0 || uncompressed < compressed)
                return false;

            bundleSize = size;
            compressedSize = compressed;
            uncompressedSize = uncompressed;
            return true;
        }

        /// <summary>SM4-CBC decrypt whole 16 byte blocks, leaving a partial tail untouched.</summary>
        public static byte[] DecryptSm4Cbc(byte[] data)
        {
            if (data == null || data.Length < 16)
                return data;

            var blockCount = data.Length / 16;
            var result = (byte[])data.Clone();
            var key = new Sm4(BlockSm4Key);
            var previous = (byte[])BlockSm4Iv.Clone();
            var block = new byte[16];
            var plain = new byte[16];

            for (var b = 0; b < blockCount; b++)
            {
                var offset = b * 16;
                Buffer.BlockCopy(data, offset, block, 0, 16);
                key.DecryptBlock(block, plain);
                for (var i = 0; i < 16; i++)
                    result[offset + i] = (byte)(plain[i] ^ previous[i]);
                Buffer.BlockCopy(block, 0, previous, 0, 16);
            }

            return result;
        }

        /// <summary>Minimal SM4 block cipher (encrypt/decrypt one block).</summary>
        private sealed class Sm4
        {
            private static readonly byte[] SBox =
            {
                0xd6,0x90,0xe9,0xfe,0xcc,0xe1,0x3d,0xb7,0x16,0xb6,0x14,0xc2,0x28,0xfb,0x2c,0x05,
                0x2b,0x67,0x9a,0x76,0x2a,0xbe,0x04,0xc3,0xaa,0x44,0x13,0x26,0x49,0x86,0x06,0x99,
                0x9c,0x42,0x50,0xf4,0x91,0xef,0x98,0x7a,0x33,0x54,0x0b,0x43,0xed,0xcf,0xac,0x62,
                0xe4,0xb3,0x1c,0xa9,0xc9,0x08,0xe8,0x95,0x80,0xdf,0x94,0xfa,0x75,0x8f,0x3f,0xa6,
                0x47,0x07,0xa7,0xfc,0xf3,0x73,0x17,0xba,0x83,0x59,0x3c,0x19,0xe6,0x85,0x4f,0xa8,
                0x68,0x6b,0x81,0xb2,0x71,0x64,0xda,0x8b,0xf8,0xeb,0x0f,0x4b,0x70,0x56,0x9d,0x35,
                0x1e,0x24,0x0e,0x5e,0x63,0x58,0xd1,0xa2,0x25,0x22,0x7c,0x3b,0x01,0x21,0x78,0x87,
                0xd4,0x00,0x46,0x57,0x9f,0xd3,0x27,0x52,0x4c,0x36,0x02,0xe7,0xa0,0xc4,0xc8,0x9e,
                0xea,0xbf,0x8a,0xd2,0x40,0xc7,0x38,0xb5,0xa3,0xf7,0xf2,0xce,0xf9,0x61,0x15,0xa1,
                0xe0,0xae,0x5d,0xa4,0x9b,0x34,0x1a,0x55,0xad,0x93,0x32,0x30,0xf5,0x8c,0xb1,0xe3,
                0x1d,0xf6,0xe2,0x2e,0x82,0x66,0xca,0x60,0xc0,0x29,0x23,0xab,0x0d,0x53,0x4e,0x6f,
                0xd5,0xdb,0x37,0x45,0xde,0xfd,0x8e,0x2f,0x03,0xff,0x6a,0x72,0x6d,0x6c,0x5b,0x51,
                0x8d,0x1b,0xaf,0x92,0xbb,0xdd,0xbc,0x7f,0x11,0xd9,0x5c,0x41,0x1f,0x10,0x5a,0xd8,
                0x0a,0xc1,0x31,0x88,0xa5,0xcd,0x7b,0xbd,0x2d,0x74,0xd0,0x12,0xb8,0xe5,0xb4,0xb0,
                0x89,0x69,0x97,0x4a,0x0c,0x96,0x77,0x7e,0x65,0xb9,0xf1,0x09,0xc5,0x6e,0xc6,0x84,
                0x18,0xf0,0x7d,0xec,0x3a,0xdc,0x4d,0x20,0x79,0xee,0x5f,0x3e,0xd7,0xcb,0x39,0x48,
            };

            private static readonly uint[] FK =
            {
                0xa3b1bac6, 0x56aa3350, 0x677d9197, 0xb27022dc
            };

            private static readonly uint[] CK = BuildCk();
            private readonly uint[] _encryptKeys;
            private readonly uint[] _decryptKeys;

            public Sm4(byte[] key)
            {
                if (key == null || key.Length != 16)
                    throw new ArgumentException("SM4 key must be 16 bytes", nameof(key));

                _encryptKeys = ExpandKey(key);
                _decryptKeys = new uint[32];
                for (var i = 0; i < 32; i++)
                    _decryptKeys[i] = _encryptKeys[31 - i];
            }

            private static uint[] BuildCk()
            {
                var ck = new uint[32];
                for (var i = 0; i < 32; i++)
                {
                    uint value = 0;
                    for (var j = 0; j < 4; j++)
                        value = (value << 8) | (byte)(((4 * i + j) * 7) % 256);
                    ck[i] = value;
                }
                return ck;
            }

            private static uint ReadBigEndian(byte[] data, int offset)
            {
                return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
                       ((uint)data[offset + 2] << 8) | data[offset + 3];
            }

            private static void WriteBigEndian(uint value, byte[] data, int offset)
            {
                data[offset] = (byte)(value >> 24);
                data[offset + 1] = (byte)(value >> 16);
                data[offset + 2] = (byte)(value >> 8);
                data[offset + 3] = (byte)value;
            }

            private static uint RotateLeft(uint value, int count)
            {
                return (value << count) | (value >> (32 - count));
            }

            private static uint Substitute(uint value)
            {
                return ((uint)SBox[(value >> 24) & 0xff] << 24) |
                       ((uint)SBox[(value >> 16) & 0xff] << 16) |
                       ((uint)SBox[(value >> 8) & 0xff] << 8) |
                       SBox[value & 0xff];
            }

            private static uint Transform(uint value)
            {
                var b = Substitute(value);
                return b ^ RotateLeft(b, 2) ^ RotateLeft(b, 10) ^ RotateLeft(b, 18) ^ RotateLeft(b, 24);
            }

            private static uint KeyTransform(uint value)
            {
                var b = Substitute(value);
                return b ^ RotateLeft(b, 13) ^ RotateLeft(b, 23);
            }

            private static uint[] ExpandKey(byte[] key)
            {
                var k = new uint[36];
                for (var i = 0; i < 4; i++)
                    k[i] = ReadBigEndian(key, i * 4) ^ FK[i];

                var keys = new uint[32];
                for (var i = 0; i < 32; i++)
                {
                    var t = k[i + 1] ^ k[i + 2] ^ k[i + 3] ^ CK[i];
                    k[i + 4] = k[i] ^ KeyTransform(t);
                    keys[i] = k[i + 4];
                }
                return keys;
            }

            private static void Crypt(uint[] keys, byte[] input, int inputOffset, byte[] output, int outputOffset)
            {
                var x0 = ReadBigEndian(input, inputOffset);
                var x1 = ReadBigEndian(input, inputOffset + 4);
                var x2 = ReadBigEndian(input, inputOffset + 8);
                var x3 = ReadBigEndian(input, inputOffset + 12);

                for (var i = 0; i < 32; i++)
                {
                    var x4 = x0 ^ Transform(x1 ^ x2 ^ x3 ^ keys[i]);
                    x0 = x1;
                    x1 = x2;
                    x2 = x3;
                    x3 = x4;
                }

                WriteBigEndian(x3, output, outputOffset);
                WriteBigEndian(x2, output, outputOffset + 4);
                WriteBigEndian(x1, output, outputOffset + 8);
                WriteBigEndian(x0, output, outputOffset + 12);
            }

            public void EncryptBlock(byte[] input, int inputOffset, byte[] output, int outputOffset)
            {
                Crypt(_encryptKeys, input, inputOffset, output, outputOffset);
            }

            public void DecryptBlock(byte[] input, byte[] output)
            {
                Crypt(_decryptKeys, input, 0, output, 0);
            }
        }
    }
}
