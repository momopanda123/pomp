using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Pomp.Adapters
{
    /// <summary>
    /// The <see cref="BufferSnapshot.ContentHash"/> rule, shared by the host tools and the Visual Studio adapter: lower-case hex
    /// SHA-256 of the text with every "\r\n" and every lone "\r" replaced by "\n", encoded as UTF-8. An unpaired surrogate (possible
    /// in an editor buffer) is encoded as its own three-byte sequence (WTF-8) instead of U+FFFD, so it never hashes like a real
    /// U+FFFD; well-formed text hashes exactly as plain UTF-8.
    /// </summary>
    public static class ContentHash
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static string Compute(string text)
        {
            var normalized = text.IndexOf('\r') >= 0 ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encode(normalized));
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                {
                    builder.Append(b.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private static byte[] Encode(string text)
        {
            if (!HasUnpairedSurrogate(text))
            {
                return Utf8.GetBytes(text);
            }

            var bytes = new List<byte>(text.Length * 3);
            for (var i = 0; i < text.Length; i++)
            {
                int c = text[i];
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    var codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                    bytes.Add((byte)(0xF0 | (codePoint >> 18)));
                    bytes.Add((byte)(0x80 | ((codePoint >> 12) & 0x3F)));
                    bytes.Add((byte)(0x80 | ((codePoint >> 6) & 0x3F)));
                    bytes.Add((byte)(0x80 | (codePoint & 0x3F)));
                }
                else if (c < 0x80)
                {
                    bytes.Add((byte)c);
                }
                else if (c < 0x800)
                {
                    bytes.Add((byte)(0xC0 | (c >> 6)));
                    bytes.Add((byte)(0x80 | (c & 0x3F)));
                }
                else
                {
                    // Also every unpaired surrogate: its own code unit, never the U+FFFD replacement.
                    bytes.Add((byte)(0xE0 | (c >> 12)));
                    bytes.Add((byte)(0x80 | ((c >> 6) & 0x3F)));
                    bytes.Add((byte)(0x80 | (c & 0x3F)));
                }
            }

            return bytes.ToArray();
        }

        private static bool HasUnpairedSurrogate(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (!char.IsSurrogate(text[i]))
                {
                    continue;
                }

                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                    continue;
                }

                return true;
            }

            return false;
        }
    }
}
