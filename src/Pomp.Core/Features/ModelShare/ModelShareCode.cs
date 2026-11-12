using System;
using System.Security.Cryptography;
using System.Text;

namespace Pomp.Features.ModelShare
{
    /// <summary>
    /// The one-time connection code of model sharing: a 5-byte secret followed by the first 10 bytes of the SHA-256 of the sharing PC's
    /// certificate (the pin), written as RFC 4648 base32 (A–Z, 2–7, no padding) = 8 + 16 = 24 characters, grouped by four with '-'.
    /// </summary>
    public static class ModelShareCode
    {
        public const int SecretLength = 5;
        public const int PinLength = 10;
        public const int TextLength = 24;
        private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        /// <summary>The grouped code text (<c>ABCD-EFGH-IJKL-MNOP-QRST-UVWX</c>).</summary>
        public static string Format(byte[] secret5, byte[] pin10)
        {
            if (secret5 == null || secret5.Length != SecretLength) throw new ArgumentException("The secret must be 5 bytes.", nameof(secret5));
            if (pin10 == null || pin10.Length != PinLength) throw new ArgumentException("The pin must be 10 bytes.", nameof(pin10));
            var text = Base32(secret5) + Base32(pin10);
            var grouped = new StringBuilder(TextLength + TextLength / 4);
            for (var i = 0; i < text.Length; i++)
            {
                if (i > 0 && i % 4 == 0) grouped.Append('-');
                grouped.Append(text[i]);
            }

            return grouped.ToString();
        }

        /// <summary>Reads a typed code: case-insensitive, '-' and white space ignored.</summary>
        public static bool TryParse(string? text, out byte[] secret5, out byte[] pin10)
        {
            secret5 = Array.Empty<byte>();
            pin10 = Array.Empty<byte>();
            if (text == null || text.Length > 256) return false;
            var normalized = new StringBuilder(TextLength);
            foreach (var c in text)
            {
                if (c == '-' || char.IsWhiteSpace(c)) continue;
                normalized.Append(char.ToUpperInvariant(c));
            }

            if (normalized.Length != TextLength) return false;
            var value = normalized.ToString();
            if (!TryBase32(value.Substring(0, 8), out var secret) || secret.Length != SecretLength) return false;
            if (!TryBase32(value.Substring(8), out var pin) || pin.Length != PinLength) return false;
            secret5 = secret;
            pin10 = pin;
            return true;
        }

        /// <summary>The first 10 bytes of SHA-256 over the certificate's DER encoding.</summary>
        public static byte[] Pin(byte[] certificateDer)
        {
            if (certificateDer == null) throw new ArgumentNullException(nameof(certificateDer));
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(certificateDer);
                var pin = new byte[PinLength];
                Buffer.BlockCopy(hash, 0, pin, 0, PinLength);
                return pin;
            }
        }

        /// <summary>RFC 4648 base32 without padding.</summary>
        public static string Base32(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            var text = new StringBuilder((bytes.Length * 8 + 4) / 5);
            var buffer = 0;
            var bits = 0;
            foreach (var b in bytes)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    bits -= 5;
                    text.Append(Alphabet[(buffer >> bits) & 31]);
                }

                buffer &= (1 << bits) - 1;
            }

            if (bits > 0) text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
            return text.ToString();
        }

        /// <summary>Decodes unpadded RFC 4648 base32 (upper-case alphabet only); false for any other character, an impossible length or non-zero trailing bits.</summary>
        public static bool TryBase32(string text, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (text == null) return false;
            var remainder = text.Length % 8;
            if (remainder == 1 || remainder == 3 || remainder == 6) return false;
            var result = new byte[text.Length * 5 / 8];
            var buffer = 0;
            var bits = 0;
            var index = 0;
            foreach (var c in text)
            {
                var value = Alphabet.IndexOf(c);
                if (value < 0) return false;
                buffer = (buffer << 5) | value;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    result[index++] = (byte)(buffer >> bits);
                }

                buffer &= (1 << bits) - 1;
            }

            if (buffer != 0) return false;
            bytes = result;
            return true;
        }

        /// <summary>Compares two byte arrays in time that depends only on their lengths.</summary>
        internal static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            var difference = 0;
            for (var i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }
    }
}
