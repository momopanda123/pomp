using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pomp.Adapters;

namespace Pomp.VisualStudio.Composition
{
    /// <summary>
    /// Builds the model-sharing TLS identity with .NET Framework 4.7.2's <see cref="CertificateRequest"/>: a self-signed RSA-2048 /
    /// SHA-256 certificate that is its own CA, valid for server authentication on the given IP addresses and DNS names. The CNG key is
    /// created exportable so the PFX carries it; nothing is written to a certificate store.
    /// </summary>
    internal sealed class VsModelShareCertificateFactory : IModelShareCertificateFactory
    {
        private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

        public byte[] CreatePfx(string commonName, IReadOnlyList<string> dnsNames, IReadOnlyList<IPAddress> ipAddresses, TimeSpan validity, string password)
        {
            if (string.IsNullOrWhiteSpace(commonName)) throw new ArgumentException("A common name is required.", nameof(commonName));
            if (dnsNames == null) throw new ArgumentNullException(nameof(dnsNames));
            if (ipAddresses == null) throw new ArgumentNullException(nameof(ipAddresses));
            if (validity <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(validity));
            if (password == null) throw new ArgumentNullException(nameof(password));

            var parameters = new CngKeyCreationParameters
            {
                ExportPolicy = CngExportPolicies.AllowExport | CngExportPolicies.AllowPlaintextExport,
                KeyUsage = CngKeyUsages.AllUsages,
            };
            parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
            using (var key = CngKey.Create(CngAlgorithm.Rsa, null, parameters))
            using (var rsa = new RSACng(key))
            {
                var subject = new X500DistinguishedName("CN=" + commonName.Replace(",", " ").Replace("=", " ").Replace("+", " ").Replace("\"", " "));
                var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                var names = new SubjectAlternativeNameBuilder();
                foreach (var address in ipAddresses) names.AddIpAddress(address);
                foreach (var name in dnsNames)
                {
                    if (!string.IsNullOrWhiteSpace(name)) names.AddDnsName(name);
                }

                request.CertificateExtensions.Add(names.Build());
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyCertSign, true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(ServerAuthentication) }, false));
                request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

                // A day of slack before "now": the client PC's clock may be behind this one.
                var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
                using (var certificate = request.CreateSelfSigned(notBefore, DateTimeOffset.UtcNow.Add(validity)))
                {
                    return certificate.Export(X509ContentType.Pfx, password);
                }
            }
        }
    }
}
