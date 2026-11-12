using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Pomp.Internal
{
    /// <summary>Windows DPAPI for the current user, with a purpose text as the entropy so one kind of secret never decrypts as another.</summary>
    internal static class Dpapi
    {
        private const uint UiForbidden = 1;

        /// <summary>Encrypts or decrypts <paramref name="input"/>; <paramref name="purpose"/> must be the same text both ways.</summary>
        public static byte[] Transform(byte[] input, bool decrypt, string purpose)
        {
            var source = new DataBlob();
            var entropy = new DataBlob();
            var output = new DataBlob();
            try
            {
                source = Allocate(input);
                entropy = Allocate(Encoding.UTF8.GetBytes(purpose));
                var success = decrypt
                    ? CryptUnprotectData(ref source, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                    : CryptProtectData(ref source, null, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!success) throw new InvalidOperationException("Windows credential encryption failed.");
                if (output.Size <= 0 || output.Size > 32768) throw new InvalidOperationException("Windows credential encryption returned an invalid blob.");
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                Clear(source); if (source.Data != IntPtr.Zero) Marshal.FreeHGlobal(source.Data);
                if (entropy.Data != IntPtr.Zero) Marshal.FreeHGlobal(entropy.Data);
                Clear(output); if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }

        private static DataBlob Allocate(byte[] bytes)
        {
            var blob = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
            Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
            return blob;
        }

        private static void Clear(DataBlob blob)
        {
            for (var i = 0; i < blob.Size && blob.Data != IntPtr.Zero; i++) Marshal.WriteByte(blob.Data, i, 0);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob { public int Size; public IntPtr Data; }

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
