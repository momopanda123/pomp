using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Pomp.Features.ModelShare
{
    /// <summary>What a registration with a sharing PC yields: the local model server to save (the token is a secret: never log or publish it).</summary>
    internal sealed class SharedModelConnection
    {
        public SharedModelConnection(string url, string token, string? serverName, string certificatePem)
        {
            Url = url; Token = token; ServerName = serverName; CertificatePem = certificatePem;
        }

        /// <summary><c>https://host:port/v1</c>.</summary>
        public string Url { get; }

        /// <summary>The per-client gateway token the sharing PC issued.</summary>
        public string Token { get; }

        /// <summary>The sharing PC's name as it reported it (unsanitized; null when it sent none).</summary>
        public string? ServerName { get; }

        /// <summary>The sharing PC's certificate (one PEM block), pinned by the connection code.</summary>
        public string CertificatePem { get; }
    }

    /// <summary>
    /// The project PC's side of model sharing: connects to the sharing PC's TLS front, checks that its certificate is the one the connection
    /// code pins before anything is sent, and registers with the code's secret (<c>POST /pomp/v1/register</c>) for a gateway token.
    /// </summary>
    internal static class SharedModelConnector
    {
        public const int DefaultPort = 47611;
        // The sharing PC answers once its gateway admits the new token (it re-reads its clients every 10 s, waits at most 15 s).
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
        private const string RegisterPath = "/pomp/v1/register";
        private const int MaxResponseBytes = 64 * 1024;
        private static readonly Regex Address = new Regex(
            "\\A(?<host>[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*)(?::(?<port>[0-9]{1,5}))?\\z",
            RegexOptions.CultureInvariant);
        private static readonly Regex Ipv4Like = new Regex("\\A[0-9.]+\\z", RegexOptions.CultureInvariant);
        private static readonly Regex TokenFormat = new Regex("\\A[A-Za-z0-9_-]{43}\\z", RegexOptions.CultureInvariant);

        /// <summary>
        /// Registers with the sharing PC at <paramref name="address"/> (<c>host[:port]</c>, host = IPv4 or DNS name) using the connection
        /// <paramref name="code"/>. Throws <see cref="ProviderSetupException"/> with a localized, secret-free text on every failure, or
        /// <see cref="OperationCanceledException"/> when <paramref name="token"/> is cancelled.
        /// </summary>
        public static Task<SharedModelConnection> ConnectAsync(string? address, string? code, CancellationToken token) =>
            ConnectAsync(address, code, DefaultTimeout, token);

        public static async Task<SharedModelConnection> ConnectAsync(string? address, string? code, TimeSpan timeout, CancellationToken token)
        {
            if (!TryParseAddress(address, out var host, out var port))
                throw new ProviderSetupException(SetupText.Get("주소 형식이 올바르지 않습니다. 예: 192.168.0.10:47611", "The address is invalid. Example: 192.168.0.10:47611"));
            if (!ModelShareCode.TryParse(code, out var secret, out var pin))
                throw new ProviderSetupException(SetupText.Get("연결 코드 형식이 올바르지 않습니다.", "The connection code is invalid."));

            try
            {
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutSource.CancelAfter(timeout);
                using var client = new TcpClient();
                // The socket APIs of netstandard2.0 take no token: closing the socket ends whatever is pending.
                using var registration = timeoutSource.Token.Register(() => client.Dispose());
                var mismatch = false;
                byte[]? certificateDer = null;
                try
                {
                    await client.ConnectAsync(host, port).ConfigureAwait(false);
                    timeoutSource.Token.ThrowIfCancellationRequested();
                    using var ssl = new SslStream(client.GetStream(), false, (sender, certificate, chain, errors) =>
                    {
                        // The code pins the certificate (self-signed by design): it alone decides, before the secret leaves this PC.
                        if (certificate == null) { mismatch = true; return false; }
                        var der = certificate.GetRawCertData();
                        if (!FixedTimeEquals(ModelShareCode.Pin(der), pin)) { mismatch = true; return false; }
                        certificateDer = der;
                        return true;
                    });
                    await ssl.AuthenticateAsClientAsync(host, null, SslProtocols.None, false).ConfigureAwait(false);
                    timeoutSource.Token.ThrowIfCancellationRequested();
                    if (certificateDer == null || (int)ssl.SslProtocol < (int)SslProtocols.Tls12) throw new AuthenticationException();

                    var request = BuildRequest(host, port, secret);
                    try { await ssl.WriteAsync(request, 0, request.Length, timeoutSource.Token).ConfigureAwait(false); }
                    finally { Array.Clear(request, 0, request.Length); }
                    await ssl.FlushAsync(timeoutSource.Token).ConfigureAwait(false);

                    var response = await ReadResponseAsync(ssl, timeoutSource.Token).ConfigureAwait(false);
                    return Interpret(response, "https://" + host + ":" + port.ToString(CultureInfo.InvariantCulture) + "/v1", certificateDer);
                }
                catch (ProviderSetupException) { throw; }
                catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
                catch (Exception) when (timeoutSource.IsCancellationRequested)
                {
                    throw new ProviderSetupException(SetupText.Get("공유 PC가 제때 응답하지 않았습니다.", "The sharing PC did not respond in time."));
                }
                catch (AuthenticationException) when (mismatch)
                {
                    throw new ProviderSetupException(SetupText.Get(
                        "공유 PC의 인증서가 연결 코드와 맞지 않습니다. 주소와 코드를 확인하세요.",
                        "The sharing PC's certificate does not match the connection code. Check the address and the code."));
                }
                catch (Exception ex) when (ex is SocketException || ex is IOException || ex is AuthenticationException || ex is ObjectDisposedException
                    || ex is InvalidOperationException)
                {
                    throw new ProviderSetupException(SetupText.Get(
                        "공유 PC에 연결하지 못했습니다. 주소와 포트, 그 PC에서 공유가 켜져 있는지 확인하세요.",
                        "Could not connect to the sharing PC. Check the address and port, and that sharing is on there."));
                }
            }
            finally
            {
                Array.Clear(secret, 0, secret.Length);
            }
        }

        /// <summary><c>host[:port]</c>: a dotted IPv4 address or a DNS name, and a port of 1–65535 (<see cref="DefaultPort"/> when omitted).</summary>
        internal static bool TryParseAddress(string? text, out string host, out int port)
        {
            host = string.Empty;
            port = DefaultPort;
            var value = text?.Trim();
            if (string.IsNullOrEmpty(value) || value!.Length > 260) return false;
            var match = Address.Match(value);
            if (!match.Success) return false;
            var name = match.Groups["host"].Value;
            if (name.Length > 253) return false;
            if (Ipv4Like.IsMatch(name))
            {
                // Digits and dots only: it has to be a full dotted IPv4 address (no "10.1" shorthand).
                if (name.Split('.').Length != 4 || !IPAddress.TryParse(name, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
                name = ip.ToString();
            }

            if (match.Groups["port"].Success)
            {
                if (!int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535) return false;
            }

            host = name.ToLowerInvariant();
            return true;
        }

        private static byte[] BuildRequest(string host, int port, byte[] secret)
        {
            var body = new JObject { ["secret"] = ModelShareCode.Base32(secret), ["name"] = Environment.MachineName }.ToString(Formatting.None);
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var head = Encoding.ASCII.GetBytes(
                "POST " + RegisterPath + " HTTP/1.1\r\n"
                + "Host: " + host + ":" + port.ToString(CultureInfo.InvariantCulture) + "\r\n"
                + "Content-Type: application/json\r\n"
                + "Content-Length: " + bodyBytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
                + "Connection: close\r\n\r\n");
            var request = new byte[head.Length + bodyBytes.Length];
            Buffer.BlockCopy(head, 0, request, 0, head.Length);
            Buffer.BlockCopy(bodyBytes, 0, request, head.Length, bodyBytes.Length);
            Array.Clear(bodyBytes, 0, bodyBytes.Length);
            return request;
        }

        private sealed class Response
        {
            public Response(int status, string body) { Status = status; Body = body; }
            public int Status { get; }
            public string Body { get; }
        }

        /// <summary>Reads one HTTP/1.1 response (at most <see cref="MaxResponseBytes"/>): until its Content-Length is in, or the front closes.</summary>
        private static async Task<Response> ReadResponseAsync(Stream stream, CancellationToken token)
        {
            var buffer = new byte[MaxResponseBytes];
            var length = 0;
            var headerEnd = -1;
            var contentLength = -1;
            while (true)
            {
                if (headerEnd >= 0 && contentLength >= 0 && length >= headerEnd + contentLength) break;
                if (length == buffer.Length) throw InvalidResponse();
                var read = await stream.ReadAsync(buffer, length, buffer.Length - length, token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
                if (headerEnd < 0)
                {
                    headerEnd = IndexOfHeaderEnd(buffer, length);
                    if (headerEnd >= 0) contentLength = ContentLength(Encoding.ASCII.GetString(buffer, 0, headerEnd));
                }
            }

            if (headerEnd < 0) throw InvalidResponse();
            var head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
            var lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
            var parts = (lineEnd >= 0 ? head.Substring(0, lineEnd) : head).Split(' ');
            if (parts.Length < 2 || !parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status)) throw InvalidResponse();
            var bodyLength = contentLength >= 0 ? Math.Min(contentLength, length - headerEnd) : length - headerEnd;
            string body;
            try { body = new UTF8Encoding(false, true).GetString(buffer, headerEnd, bodyLength); }
            catch (ArgumentException) { throw InvalidResponse(); }
            finally { Array.Clear(buffer, 0, buffer.Length); }
            return new Response(status, body);
        }

        /// <summary>The offset just after the blank line that ends the headers, or -1.</summary>
        private static int IndexOfHeaderEnd(byte[] buffer, int length)
        {
            for (var i = 3; i < length; i++)
            {
                if (buffer[i] == '\n' && buffer[i - 1] == '\r' && buffer[i - 2] == '\n' && buffer[i - 3] == '\r') return i + 1;
            }

            return -1;
        }

        private static int ContentLength(string head)
        {
            foreach (var line in head.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0 || !string.Equals(line.Substring(0, colon).Trim(), "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(line.Substring(colon + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > MaxResponseBytes)
                    throw InvalidResponse();
                return value;
            }

            return -1;
        }

        private static SharedModelConnection Interpret(Response response, string url, byte[] certificateDer)
        {
            switch (response.Status)
            {
                case 200:
                    break;
                case 403:
                    throw new ProviderSetupException(SetupText.Get(
                        "연결 코드가 올바르지 않습니다. 공유하는 PC에서 새 코드를 만들어 다시 입력하세요.",
                        "The connection code is not valid. Create a new code on the sharing PC and enter it again."));
                case 410:
                    throw new ProviderSetupException(SetupText.Get(
                        "연결 코드가 만료되었습니다. 공유하는 PC에서 새 코드를 만들어 다시 입력하세요.",
                        "The connection code has expired. Create a new code on the sharing PC and enter it again."));
                default:
                    throw new ProviderSetupException(SetupText.Get("공유 PC가 연결 요청을 받아들이지 않았습니다.", "The sharing PC did not accept the connection request."));
            }

            JObject data;
            try
            {
                using var text = new StringReader(response.Body);
                using var reader = new JsonTextReader(text) { MaxDepth = 4, DateParseHandling = DateParseHandling.None };
                data = JObject.Load(reader);
            }
            catch (JsonException) { throw InvalidResponse(); }

            var issued = data["token"]?.Type == JTokenType.String ? (string?)data["token"] : null;
            if (issued == null || !TokenFormat.IsMatch(issued)) throw InvalidResponse();
            var name = data["name"]?.Type == JTokenType.String ? (string?)data["name"] : null;
            return new SharedModelConnection(url, issued, name, ToPem(certificateDer));
        }

        private static string ToPem(byte[] der)
        {
            var base64 = Convert.ToBase64String(der);
            var builder = new StringBuilder("-----BEGIN CERTIFICATE-----\n", base64.Length + base64.Length / 64 + 64);
            for (var i = 0; i < base64.Length; i += 64) builder.Append(base64, i, Math.Min(64, base64.Length - i)).Append('\n');
            return builder.Append("-----END CERTIFICATE-----\n").ToString();
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            var difference = 0;
            for (var i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        private static ProviderSetupException InvalidResponse() =>
            new ProviderSetupException(SetupText.Get("공유 PC의 응답이 올바르지 않습니다.", "The sharing PC's response is invalid."));
    }
}
