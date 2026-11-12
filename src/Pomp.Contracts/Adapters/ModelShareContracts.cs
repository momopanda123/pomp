using System;
using System.Collections.Generic;
using System.Net;

namespace Pomp.Adapters
{
    /// <summary>Creates the TLS identity of model sharing (the .NET Framework certificate builder lives in the Visual Studio layer).</summary>
    public interface IModelShareCertificateFactory
    {
        /// <summary>
        /// A self-signed RSA-2048/SHA-256 certificate for server authentication, exported as PFX bytes with its private key and protected
        /// with <paramref name="password"/>. Subject alternative names are <paramref name="ipAddresses"/> and <paramref name="dnsNames"/>; the
        /// certificate is its own CA (basic constraints CA:true) so a client can trust exactly it.
        /// </summary>
        byte[] CreatePfx(string commonName, IReadOnlyList<string> dnsNames, IReadOnlyList<IPAddress> ipAddresses, TimeSpan validity, string password);
    }
}
