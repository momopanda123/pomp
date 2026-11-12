using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Internal;

namespace Pomp.Features.ModelShare
{
    /// <summary>The answer to one registration request (status code and JSON body).</summary>
    internal sealed class ModelShareRegistrationReply
    {
        public ModelShareRegistrationReply(int status, JObject body)
        {
            Status = status;
            Body = body;
        }

        public int Status { get; }
        public JObject Body { get; }
    }

    /// <summary>
    /// The LAN side of model sharing: a TLS listener (TLS 1.2, the sharing certificate) on the chosen address and port. The first request of
    /// each connection is read (head ≤ 16 KB): <c>POST /pomp/v1/register</c> is answered here (body ≤ 4 KB, then the connection closes);
    /// anything else is forwarded byte for byte to the fixed loopback gateway (the bytes already read first, then both directions until
    /// either side closes). There is no other destination and no CONNECT. Request contents are never logged.
    /// </summary>
    internal sealed class ModelShareTlsFront : IDisposable
    {
        public const int MaxConnections = 64;
        public const string RegisterPath = "/pomp/v1/register";
        private const int MaxHeadBytes = 16 * 1024;
        private const int MaxRegisterBodyBytes = 4 * 1024;
        private static readonly TimeSpan HandshakeAndHeadTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan AdmitTimeout = TimeSpan.FromSeconds(15);
        private static readonly byte[] HeadEnd = { 13, 10, 13, 10 };

        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private readonly Func<string?, string?, ModelShareRegistrationReply> _register;
        private readonly Action<string> _fault;
        private readonly IPompLogger _logger;
        private readonly string _endpointText;
        private readonly SemaphoreSlim _slots = new SemaphoreSlim(MaxConnections, MaxConnections);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly object _lock = new object();
        private readonly HashSet<TcpClient> _open = new HashSet<TcpClient>();
        private int _gatewayPort;
        private int _disposed;

        private ModelShareTlsFront(TcpListener listener, X509Certificate2 certificate, string endpointText,
            Func<string?, string?, ModelShareRegistrationReply> register, Action<string> fault, IPompLogger logger)
        {
            _listener = listener;
            _certificate = certificate;
            _endpointText = endpointText;
            _register = register;
            _fault = fault;
            _logger = logger;
        }

        /// <summary>Binds the listener (connections wait in the backlog until <see cref="Start"/>).</summary>
        /// <param name="register">Called with the request's <c>secret</c> and <c>name</c> (either may be null when absent).</param>
        /// <param name="fault">Called once when the listener stops working on its own (localized text).</param>
        /// <exception cref="ProviderSetupException">The address/port cannot be used (localized, names the address and port).</exception>
        public static ModelShareTlsFront Bind(IPAddress address, int port, X509Certificate2 certificate,
            Func<string?, string?, ModelShareRegistrationReply> register, Action<string> fault, IPompLogger logger)
        {
            var endpoint = address + ":" + port.ToString(CultureInfo.InvariantCulture);
            var listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
            try
            {
                listener.Start(128);
            }
            catch (SocketException ex)
            {
                try { listener.Stop(); }
                catch (Exception) { /* Never started. */ }
                logger.Warn("Model sharing could not listen on " + endpoint + ": " + ex.SocketErrorCode);
                throw new ProviderSetupException(BindText(endpoint, ex.SocketErrorCode));
            }

            return new ModelShareTlsFront(listener, certificate, endpoint, register, fault, logger);
        }

        /// <summary>Starts accepting; requests other than registration go to <c>127.0.0.1:<paramref name="gatewayPort"/></c>.</summary>
        public void Start(int gatewayPort)
        {
            Volatile.Write(ref _gatewayPort, gatewayPort);
            Task.Run(AcceptLoopAsync).Forget(_logger, "model share listener");
        }

        private static string BindText(string endpoint, SocketError error)
        {
            switch (error)
            {
                case SocketError.AddressAlreadyInUse:
                    return SetupText.Get(
                        endpoint + " 포트를 이미 다른 프로그램이 쓰고 있습니다. 다른 포트를 고르세요.",
                        "Another program already uses " + endpoint + ". Choose another port.");
                case SocketError.AddressNotAvailable:
                    return SetupText.Get(
                        "이 PC에 " + endpoint + " 주소가 없습니다. 주소를 다시 고르세요.",
                        "This PC has no address " + endpoint + ". Choose the address again.");
                case SocketError.AccessDenied:
                    return SetupText.Get(
                        endpoint + "에서 연결을 받을 권한이 없습니다. 다른 포트를 고르세요.",
                        "Listening on " + endpoint + " is not allowed. Choose another port.");
                default:
                    return SetupText.Get(
                        endpoint + "에서 연결을 받을 수 없습니다.",
                        "Connections cannot be accepted on " + endpoint + ".");
            }
        }

        private async Task AcceptLoopAsync()
        {
            var failures = 0;
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    failures = 0;
                }
                catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException || ex is InvalidOperationException)
                {
                    if (_stop.IsCancellationRequested) return;
                    if (ex is SocketException && ++failures < 20)
                    {
                        // An aborted connection while it waited in the backlog: keep accepting.
                        await Task.Delay(50).ConfigureAwait(false);
                        continue;
                    }

                    _logger.Warn("The model share listener on " + _endpointText + " stopped: " + ex.GetType().Name);
                    _fault(SetupText.Get(
                        _endpointText + "에서 연결을 더 받을 수 없습니다.",
                        "Connections are no longer accepted on " + _endpointText + "."));
                    return;
                }

                if (!_slots.Wait(0))
                {
                    // Bounded: a flood of connections cannot exhaust this process.
                    Close(client);
                    continue;
                }

                Task.Run(() => ServeAsync(client)).Forget(_logger, "model share connection");
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            lock (_lock)
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    Close(client);
                    _slots.Release();
                    return;
                }

                _open.Add(client);
            }

            TcpClient? upstream = null;
            try
            {
                client.NoDelay = true;
                using (var tls = new SslStream(client.GetStream(), false))
                {
                    byte[] buffer;
                    int total;
                    int headLength;
                    string? method;
                    string? target;
                    Dictionary<string, string> headers;
                    using (var timeout = new CancellationTokenSource(HandshakeAndHeadTimeout))
                    using (timeout.Token.Register(() => Close(client)))
                    {
                        await tls.AuthenticateAsServerAsync(_certificate, false, SslProtocols.Tls12, false).ConfigureAwait(false);
                        buffer = new byte[MaxHeadBytes + MaxRegisterBodyBytes];
                        total = 0;
                        headLength = -1;
                        while (headLength < 0)
                        {
                            if (total >= MaxHeadBytes)
                            {
                                await ReplyAsync(tls, 431, new JObject { ["error"] = "bad_request" }).ConfigureAwait(false);
                                return;
                            }

                            var read = await tls.ReadAsync(buffer, total, MaxHeadBytes - total).ConfigureAwait(false);
                            if (read == 0) return;
                            var from = Math.Max(0, total - 3);
                            total += read;
                            headLength = IndexOf(buffer, from, total, HeadEnd);
                            if (headLength >= 0) headLength += HeadEnd.Length;
                        }

                        ParseHead(buffer, headLength, out method, out target, out headers);
                        if (method == "POST" && target == RegisterPath)
                        {
                            await RegisterAsync(tls, buffer, headLength, total, headers).ConfigureAwait(false);
                            return;
                        }
                    }

                    if (method == null)
                    {
                        await ReplyAsync(tls, 400, new JObject { ["error"] = "bad_request" }).ConfigureAwait(false);
                        return;
                    }

                    upstream = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                    lock (_lock)
                    {
                        if (Volatile.Read(ref _disposed) == 1) return;
                        _open.Add(upstream);
                    }

                    await upstream.ConnectAsync(IPAddress.Loopback, Volatile.Read(ref _gatewayPort)).ConfigureAwait(false);
                    var gateway = upstream.GetStream();
                    await gateway.WriteAsync(buffer, 0, total).ConfigureAwait(false);
                    Array.Clear(buffer, 0, total);
                    var toGateway = PumpAsync(tls, gateway);
                    var toClient = PumpAsync(gateway, tls);
                    await Task.WhenAny(toGateway, toClient).ConfigureAwait(false);
                    Close(upstream);
                    Close(client);
                    await Task.WhenAll(toGateway, toClient).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // Handshake failures, resets, timeouts: never the request contents.
                _logger.Debug("A model share connection ended: " + ex.GetType().Name);
            }
            finally
            {
                lock (_lock)
                {
                    _open.Remove(client);
                    if (upstream != null) _open.Remove(upstream);
                }

                if (upstream != null) Close(upstream);
                Close(client);
                _slots.Release();
            }
        }

        private async Task RegisterAsync(SslStream tls, byte[] buffer, int headLength, int total, Dictionary<string, string> headers)
        {
            if (headers.ContainsKey("transfer-encoding")
                || !headers.TryGetValue("content-length", out var lengthText)
                || !int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || length > MaxRegisterBodyBytes)
            {
                await ReplyAsync(tls, 400, new JObject { ["error"] = "bad_request" }).ConfigureAwait(false);
                return;
            }

            var end = headLength + length;
            while (total < end)
            {
                var read = await tls.ReadAsync(buffer, total, end - total).ConfigureAwait(false);
                if (read == 0) return;
                total += read;
            }

            string? secret = null;
            string? name = null;
            var valid = true;
            try
            {
                var text = new UTF8Encoding(false, true).GetString(buffer, headLength, length);
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, MaxDepth = 8 })
                {
                    if (JToken.Load(reader) is JObject body)
                    {
                        secret = body["secret"]?.Type == JTokenType.String ? (string?)body["secret"] : null;
                        name = body["name"]?.Type == JTokenType.String ? (string?)body["name"] : null;
                    }
                    else valid = false;
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is DecoderFallbackException) { valid = false; }
            finally { Array.Clear(buffer, 0, buffer.Length); }

            var reply = valid ? _register(secret, name) : new ModelShareRegistrationReply(400, new JObject { ["error"] = "bad_request" });
            if (reply.Status == 200 && reply.Body["token"]?.Type == JTokenType.String) await WaitUntilAdmittedAsync((string)reply.Body["token"]!).ConfigureAwait(false);
            await ReplyAsync(tls, reply.Status, reply.Body).ConfigureAwait(false);
        }

        /// <summary>
        /// The gateway re-reads its clients file every 10 s (omp's <c>CLIENTS_RELOAD_INTERVAL_MS</c>): the answer waits until it admits the
        /// new token, so the engine the project PC restarts right after connecting finds the models at once. After
        /// <see cref="AdmitTimeout"/> the answer goes anyway (the token is saved and starts working at the next reload).
        /// </summary>
        private async Task WaitUntilAdmittedAsync(string token)
        {
            var deadline = DateTime.UtcNow + AdmitTimeout;
            while (!_stop.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                if (await AdmitsAsync(token).ConfigureAwait(false)) return;
                try { await Task.Delay(500, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }

            if (!_stop.IsCancellationRequested) _logger.Warn("Model sharing: the gateway has not admitted a new PC yet; it will at its next clients reload.");
        }

        /// <summary><c>GET /v1/models</c> on the loopback gateway with the new token: true once it answers 200.</summary>
        private async Task<bool> AdmitsAsync(string token)
        {
            using (var tcp = new TcpClient())
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using (timeout.Token.Register(() => tcp.Close()))
            {
                var request = Encoding.ASCII.GetBytes("GET /v1/models HTTP/1.1\r\nHost: 127.0.0.1\r\nAuthorization: Bearer " + token + "\r\nConnection: close\r\n\r\n");
                try
                {
                    await tcp.ConnectAsync(IPAddress.Loopback, Volatile.Read(ref _gatewayPort)).ConfigureAwait(false);
                    var stream = tcp.GetStream();
                    await stream.WriteAsync(request, 0, request.Length).ConfigureAwait(false);
                    var status = new byte[12];
                    var read = 0;
                    while (read < status.Length)
                    {
                        var count = await stream.ReadAsync(status, read, status.Length - read).ConfigureAwait(false);
                        if (count == 0) break;
                        read += count;
                    }

                    return read == status.Length && Encoding.ASCII.GetString(status, 9, 3) == "200";
                }
                catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is InvalidOperationException)
                {
                    return false;
                }
                finally
                {
                    Array.Clear(request, 0, request.Length);
                }
            }
        }

        private static async Task ReplyAsync(SslStream tls, int status, JObject body)
        {
            var content = new UTF8Encoding(false).GetBytes(body.ToString(Formatting.None));
            var head = "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " " + Reason(status) + "\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + "Content-Length: " + content.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
                + "Cache-Control: no-store\r\n"
                + "Connection: close\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(head);
            await tls.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await tls.WriteAsync(content, 0, content.Length).ConfigureAwait(false);
            await tls.FlushAsync().ConfigureAwait(false);
            Array.Clear(content, 0, content.Length);
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 410: return "Gone";
                case 431: return "Request Header Fields Too Large";
                case 503: return "Service Unavailable";
                default: return "Error";
            }
        }

        /// <summary>Request line and headers (names lower-cased; a repeated header makes the request malformed). <paramref name="method"/> is null when malformed.</summary>
        private static void ParseHead(byte[] buffer, int headLength, out string? method, out string? target, out Dictionary<string, string> headers)
        {
            method = null;
            target = null;
            headers = new Dictionary<string, string>(StringComparer.Ordinal);
            var lines = Encoding.ASCII.GetString(buffer, 0, headLength - 4).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var parts = lines[0].Split(' ');
            if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return;
            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon <= 0) return;
                var name = lines[i].Substring(0, colon).Trim().ToLowerInvariant();
                if (headers.ContainsKey(name))
                {
                    if (name == "content-length" || name == "transfer-encoding") return;
                    continue;
                }

                headers[name] = lines[i].Substring(colon + 1).Trim();
            }

            method = parts[0];
            target = parts[1];
        }

        private static int IndexOf(byte[] buffer, int from, int end, byte[] pattern)
        {
            for (var i = from; i + pattern.Length <= end; i++)
            {
                var match = true;
                for (var j = 0; j < pattern.Length; j++)
                {
                    if (buffer[i + j] != pattern[j]) { match = false; break; }
                }

                if (match) return i;
            }

            return -1;
        }

        private static async Task PumpAsync(Stream from, Stream to)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (true)
                {
                    var read = await from.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read == 0) return;
                    await to.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                    await to.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                // Either side closed: the caller closes both.
            }
            finally { Array.Clear(buffer, 0, buffer.Length); }
        }

        private static void Close(TcpClient client)
        {
            try { client.Close(); }
            catch (Exception) { /* Already closed. */ }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _stop.Cancel();
            try { _listener.Stop(); }
            catch (Exception) { /* Shutdown is best effort. */ }
            TcpClient[] open;
            lock (_lock)
            {
                open = new TcpClient[_open.Count];
                _open.CopyTo(open);
                _open.Clear();
            }

            foreach (var client in open) Close(client);
        }
    }
}
