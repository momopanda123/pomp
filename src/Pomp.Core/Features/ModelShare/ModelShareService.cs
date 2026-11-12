using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Pomp.Internal;

namespace Pomp.Features.ModelShare
{
    /// <summary>
    /// The external PC's side of model sharing: omp's <c>auth-broker serve</c> and <c>auth-gateway serve</c> on loopback (this PC's omp
    /// sign-ins), a TLS front on the chosen LAN address that registers project PCs with a one-time code and forwards everything else to the
    /// gateway, and the state the Accounts tab shows. Data lives in <c>&lt;data&gt;\model-share</c>. Start and stop are serialized; the
    /// slow part of a start runs in the background (the state says <c>starting</c>). Tokens, the code and the private key are never logged.
    /// </summary>
    public sealed class ModelShareService : IDisposable
    {
        private const string IdentityPurpose = "POMP model share identity v1";
        // The PFX is protected by DPAPI on disk; its own password only satisfies the PFX format.
        private const string PfxPassword = "POMP model share";
        private const int MaxCodeFailures = 5;
        private const long MaxIdentityBytes = 64 * 1024;
        private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan BrokerReadyTimeout = TimeSpan.FromSeconds(60);
        // The gateway listens only after its first catalog rebuild, which asks the providers online for their models.
        private static readonly TimeSpan GatewayReadyTimeout = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan IdentityValidity = TimeSpan.FromDays(3650);
        private static readonly TimeSpan IdentityRenewal = TimeSpan.FromDays(30);
        private static readonly TimeSpan AddressCacheTime = TimeSpan.FromSeconds(5);

        private readonly IPompHostEnvironment _environment;
        private readonly IPompSettings _settings;
        private readonly IOmpProcessLauncher _launcher;
        private readonly IModelShareCertificateFactory _certificates;
        private readonly IPompLogger _logger;
        private readonly OmpExecutableResolver _resolver = new OmpExecutableResolver();
        private readonly string _folder;
        private readonly string _settingsPath;
        private readonly string _identityPath;
        private readonly string _gatewayClientsPath;
        private readonly object _lock = new object();
        private readonly SemaphoreSlim _transition = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();
        private readonly Timer _expiry;
        private ModelShareSettings _share;
        private string _state = "off";
        private string? _error;
        private int? _models;
        private ActiveCode? _code;
        private Run? _run;
        private int _generation;
        private bool _disposed;
        private IReadOnlyList<ShareAddress>? _addresses;
        private DateTime _addressesReadUtc;

        public ModelShareService(IPompHostEnvironment environment, IPompSettings settings, IOmpProcessLauncher launcher,
            IModelShareCertificateFactory certificates, IPompLogger logger)
        {
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
            _certificates = certificates ?? throw new ArgumentNullException(nameof(certificates));
            _logger = logger ?? NullPompLogger.Instance;
            _folder = Path.Combine(Path.GetFullPath(environment.DataDirectory), "model-share");
            _settingsPath = Path.Combine(_folder, "settings.json");
            _identityPath = Path.Combine(_folder, "identity.pfx.dpapi");
            _gatewayClientsPath = Path.Combine(_folder, "gateway-clients.json");
            _share = ModelShareSettings.Read(_settingsPath, _logger);
            _expiry = new Timer(_ => OnCodeTimer(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Raised after any state change, on any thread, never under the service's lock.</summary>
        public event EventHandler? Changed;

        /// <summary>The <c>modelShare</c> object of <c>setup/state</c>.</summary>
        public JObject State()
        {
            var addresses = Addresses();
            string state;
            string? error;
            int? models;
            ActiveCode? code;
            ModelShareSettings share;
            lock (_lock)
            {
                state = _state;
                error = _error;
                models = _models;
                code = _code;
                share = _share;
            }

            var now = DateTimeOffset.UtcNow;
            var list = new JArray();
            foreach (var address in addresses) list.Add(new JObject { ["address"] = address.Address.ToString(), ["name"] = address.Name });
            var clients = new JArray();
            foreach (var client in share.Clients) clients.Add(new JObject { ["name"] = client.Name, ["createdUtc"] = client.CreatedUtc });
            return new JObject
            {
                ["state"] = state,
                ["error"] = error,
                ["address"] = share.Address,
                ["port"] = share.Port,
                ["addresses"] = list,
                ["models"] = models,
                ["code"] = code != null && code.ExpiresUtc > now
                    ? new JObject { ["code"] = code.Text, ["expiresUtc"] = code.ExpiresUtc.ToString("O", CultureInfo.InvariantCulture) }
                    : null,
                ["clients"] = clients,
            };
        }

        /// <summary>Starts sharing in the background when it was left on (composition calls this once); never throws.</summary>
        public void StartIfEnabled()
        {
            try
            {
                int generation;
                lock (_lock)
                {
                    if (_disposed || !_share.Enabled) return;
                    generation = ++_generation;
                    SetStateLocked("starting", null);
                }

                RaiseChanged();
                Task.Run(() => StartAsync(generation)).Forget(_logger, "model share start");
            }
            catch (Exception ex)
            {
                _logger.Warn("Model sharing could not be started: " + ex.GetType().Name);
            }
        }

        /// <summary>Saves the switch; on starts sharing in the background (state <c>starting</c>), off stops it now.</summary>
        public async Task SetEnabledAsync(bool enabled, CancellationToken token)
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                if (enabled == _share.Enabled && (enabled ? _state == "on" || _state == "starting" : _state == "off")) return;
            }

            await ApplyAsync(share => share.WithEnabled(enabled), token).ConfigureAwait(false);
        }

        /// <summary>Saves the LAN address (IPv4 of this PC) and port; restarts sharing when it is on.</summary>
        public async Task ConfigureAsync(string address, int port, CancellationToken token)
        {
            var normalized = ModelShareSettings.NormalizeAddress(address);
            if (normalized == null) throw new ProviderSetupException(SetupText.Get("이 PC의 IPv4 주소를 고르세요.", "Choose an IPv4 address of this PC."));
            if (!ModelShareSettings.ValidPort(port)) throw new ProviderSetupException(SetupText.Get("포트는 1024부터 65535 사이여야 합니다.", "The port must be between 1024 and 65535."));
            await ApplyAsync(share => share.WithEndpoint(normalized, port), token).ConfigureAwait(false);
        }

        /// <summary>Replaces the connection code with a new one (valid 10 minutes, single use). Requires sharing to be on.</summary>
        public void NewCode()
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                var pin = _run?.Pin;
                if (_state != "on" || pin == null)
                    throw new ProviderSetupException(SetupText.Get("모델 공유가 켜져 있을 때만 연결 코드를 만들 수 있습니다.", "A connection code can only be created while model sharing is on."));
                _code = ActiveCode.Create(pin);
            }

            ScheduleExpiry();
            RaiseChanged();
        }

        /// <summary>Removes a connected PC: its token stops working once the gateway re-reads its clients file.</summary>
        public void Revoke(string clientName)
        {
            int remaining;
            lock (_lock)
            {
                ThrowIfDisposed();
                var client = _share.Clients.FirstOrDefault(item => string.Equals(item.Name, clientName, StringComparison.Ordinal));
                if (client == null) throw new ProviderSetupException(SetupText.Get("연결된 PC 목록에 그 이름이 없습니다.", "No connected PC has that name."));
                var next = _share.WithClients(_share.Clients.Where(item => !ReferenceEquals(item, client)).ToList());
                SaveLocked(next);
                remaining = next.Clients.Count;
            }

            _logger.Info("Model sharing: a connected PC was removed (" + remaining.ToString(CultureInfo.InvariantCulture) + " left).");
            RaiseChanged();
        }

        // ---- transitions ------------------------------------------------------------------------------------------------

        private async Task ApplyAsync(Func<ModelShareSettings, ModelShareSettings> change, CancellationToken token)
        {
            Run? starting;
            lock (_lock)
            {
                ThrowIfDisposed();
                // A newer request supersedes a start still in progress (and any queued one).
                _generation++;
                starting = _run != null && !_run.Started ? _run : null;
            }

            starting?.CancelStart();
            await _transition.WaitAsync(token).ConfigureAwait(false);
            int generation;
            bool start;
            Run? stop;
            try
            {
                lock (_lock)
                {
                    ThrowIfDisposed();
                    var next = change(_share);
                    SaveLocked(next);
                    stop = _run;
                    _run = null;
                    generation = ++_generation;
                    start = next.Enabled;
                    SetStateLocked(start ? "starting" : "off", null);
                }

                stop?.Stop();
            }
            finally { _transition.Release(); }

            if (stop != null) _logger.Info("Model sharing stopped.");
            RaiseChanged();
            if (start) Task.Run(() => StartAsync(generation)).Forget(_logger, "model share start");
        }

        private async Task StartAsync(int generation)
        {
            try { await _transition.WaitAsync(_disposeCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            Run? run = null;
            try
            {
                lock (_lock)
                {
                    if (_disposed || generation != _generation || !_share.Enabled) return;
                    run = new Run();
                    _run = run;
                }

                int? models;
                string? failure = null;
                try { models = await StartRunAsync(run).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    models = null;
                    failure = ex is ProviderSetupException
                        ? ex.Message
                        : ex is OperationCanceledException
                            ? SetupText.Get("모델 공유 시작이 취소되었습니다.", "Starting model sharing was cancelled.")
                            : SetupText.Get("모델 공유를 시작하지 못했습니다.", "Model sharing could not be started.");
                    if (!(ex is ProviderSetupException) && !(ex is OperationCanceledException))
                        _logger.Warn("Model sharing could not be started: " + ex.GetType().Name);
                }

                var autoCode = false;
                lock (_lock)
                {
                    if (_run != run) failure ??= string.Empty;
                    else
                    {
                        // A server that died or a listener that failed while the start finished counts as a failed start.
                        failure ??= run.Fault ?? run.ExitedProcessText();
                        if (failure == null)
                        {
                            run.Started = true;
                            SetStateLocked("on", null);
                            _models = models;
                            autoCode = _share.Clients.Count == 0 && run.Pin != null;
                            if (autoCode) _code = ActiveCode.Create(run.Pin!);
                        }
                        else
                        {
                            _run = null;
                            // A newer request (switch, address) superseded this start: it sets the state itself.
                            if (generation == _generation) SetStateLocked("failed", failure);
                            else failure = string.Empty;
                        }
                    }
                }

                if (failure != null)
                {
                    run.Stop();
                    if (failure.Length > 0) _logger.Warn("Model sharing failed to start.");
                    return;
                }

                if (autoCode) ScheduleExpiry();
                _logger.Info("Model sharing is on at " + run.Endpoint + " (" + (models?.ToString(CultureInfo.InvariantCulture) ?? "?") + " models).");
            }
            finally
            {
                _transition.Release();
                if (run != null) RaiseChanged();
            }
        }

        /// <summary>Called when a server exits or the listener fails on its own: a running share becomes <c>failed</c>; a start sees it itself.</summary>
        private void OnRunFault(Run run, string message)
        {
            lock (_lock)
            {
                run.Fault ??= message;
                if (!run.Started) return;
            }

            Task.Run(async () =>
            {
                try { await _transition.WaitAsync(_disposeCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                var failed = false;
                try
                {
                    lock (_lock)
                    {
                        if (_run == run)
                        {
                            _run = null;
                            SetStateLocked("failed", run.Fault ?? message);
                            failed = true;
                        }
                    }

                    if (failed)
                    {
                        run.Stop();
                        _logger.Warn("Model sharing stopped: a server exited or the listener failed.");
                    }
                }
                finally { _transition.Release(); }

                if (failed) RaiseChanged();
            }).Forget(_logger, "model share failure");
        }

        /// <summary>Everything a start does (background thread); returns the served model count when the gateway said it.</summary>
        private async Task<int?> StartRunAsync(Run run)
        {
            var token = run.Token;
            try { Directory.CreateDirectory(_folder); }
            catch (Exception ex)
            {
                _logger.Warn("The model share folder could not be created: " + ex.GetType().Name);
                throw new ProviderSetupException(SetupText.Get("POMP 데이터 폴더를 준비하지 못했습니다.", "The POMP data folder could not be prepared."));
            }

            var addresses = Addresses(true);
            ModelShareSettings share;
            lock (_lock) share = _share;
            var addressText = share.Address ?? addresses.FirstOrDefault(item => !IPAddress.IsLoopback(item.Address))?.Address.ToString();
            if (addressText == null)
                throw new ProviderSetupException(SetupText.Get(
                    "공유할 네트워크 주소가 없습니다. 네트워크 연결을 확인하세요.",
                    "There is no network address to share on. Check the network connection."));
            if (share.Address == null)
            {
                lock (_lock)
                {
                    if (_share.Address == null)
                    {
                        try { SaveLocked(_share.WithEndpoint(addressText, _share.Port)); }
                        catch (ProviderSetupException) { /* Used for this start anyway; the next save writes it. */ }
                    }
                }
            }

            var address = IPAddress.Parse(addressText);
            run.Endpoint = addressText + ":" + share.Port.ToString(CultureInfo.InvariantCulture);
            var certificate = run.Attach(LoadIdentity(address, addresses, share.Clients.Count));
            run.Pin = ModelShareCode.Pin(certificate.RawData);
            lock (_lock)
            {
                try { _share.WriteGatewayClients(_gatewayClientsPath); }
                catch (Exception ex)
                {
                    _logger.Warn("The model share clients file could not be written: " + ex.GetType().Name);
                    throw new ProviderSetupException(SaveFailedText());
                }
            }

            var front = run.Attach(ModelShareTlsFront.Bind(address, share.Port, certificate, Register, message => OnRunFault(run, message), _logger));
            token.ThrowIfCancellationRequested();

            var engine = ResolveEngine();
            // Each command is a cold omp start: run both at once.
            var brokerTokenRun = ModelShareTokenCommand.RunAsync(_launcher, engine.Launch("auth-broker", "token", "--json"), "auth-broker", _logger, token);
            var gatewayTokenRun = ModelShareTokenCommand.RunAsync(_launcher, engine.Launch("auth-gateway", "token", "--json"), "auth-gateway", _logger, token);
            await Task.WhenAll(brokerTokenRun, gatewayTokenRun).ConfigureAwait(false);
            var brokerToken = brokerTokenRun.Result;
            var gatewayToken = gatewayTokenRun.Result;
            var secrets = new[] { brokerToken, gatewayToken };

            var brokerPort = FreeLoopbackPort(0);
            var brokerUrl = "http://127.0.0.1:" + brokerPort.ToString(CultureInfo.InvariantCulture);
            var broker = run.Attach(ModelShareProcess.Start(_launcher,
                engine.Launch("auth-broker", "serve", "--bind=127.0.0.1:" + brokerPort.ToString(CultureInfo.InvariantCulture)),
                "auth-broker", secrets, _logger, process => OnRunFault(run, ExitedText(process))));
            run.Processes.Add(broker);
            await WaitReadyAsync(run, broker, brokerUrl + "/v1/healthz", brokerToken, false, BrokerReadyTimeout, token).ConfigureAwait(false);

            var gatewayPort = FreeLoopbackPort(brokerPort);
            var gatewayLaunch = engine.Launch("auth-gateway", "serve", "--bind=127.0.0.1:" + gatewayPort.ToString(CultureInfo.InvariantCulture), "--clients=" + _gatewayClientsPath);
            gatewayLaunch.Environment["OMP_AUTH_BROKER_URL"] = brokerUrl;
            gatewayLaunch.Environment["OMP_AUTH_BROKER_TOKEN"] = brokerToken;
            var gateway = run.Attach(ModelShareProcess.Start(_launcher, gatewayLaunch, "auth-gateway", secrets, _logger, process => OnRunFault(run, ExitedText(process))));
            run.Processes.Add(gateway);
            var models = await WaitReadyAsync(run, gateway, "http://127.0.0.1:" + gatewayPort.ToString(CultureInfo.InvariantCulture) + "/v1/models", gatewayToken, true, GatewayReadyTimeout, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            front.Start(gatewayPort);
            return models;
        }

        /// <summary>Polls <paramref name="url"/> until it answers 200 (at most <paramref name="timeout"/>); with <paramref name="countModels"/> returns <c>data.length</c>.</summary>
        private async Task<int?> WaitReadyAsync(Run run, ModelShareProcess process, string url, string bearer, bool countModels, TimeSpan timeout, CancellationToken token)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new ProviderSetupException(ExitedText(process));
                try
                {
                    using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(token))
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        attempt.CancelAfter(ProbeTimeout);
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                        using (var response = await run.Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, attempt.Token).ConfigureAwait(false))
                        {
                            if (response.StatusCode == HttpStatusCode.OK)
                            {
                                if (!countModels) return null;
                                return CountModels(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                            }
                        }
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested && (ex is HttpRequestException || ex is OperationCanceledException || ex is IOException))
                {
                    // Not listening yet.
                }

                if (DateTime.UtcNow >= deadline)
                {
                    var tail = process.Tail();
                    _logger.Warn("Model sharing: " + process.Name + " did not become ready in time." + (tail == null ? "" : " Output: " + tail));
                    throw new ProviderSetupException(SetupText.Get(
                        process.Name + "이(가) " + timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "초 안에 준비되지 않았습니다.",
                        process.Name + " was not ready within " + timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s."));
                }

                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }

        private static int? CountModels(string text)
        {
            try
            {
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                {
                    return (JToken.Load(reader) as JObject)?["data"] is JArray data ? data.Count : (int?)null;
                }
            }
            catch (JsonException) { return null; }
        }

        private static string ExitedText(ModelShareProcess process)
        {
            var code = process.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?";
            var tail = process.Tail();
            var korean = process.Name + "이(가) 종료되었습니다(코드 " + code + ").";
            var english = process.Name + " exited (code " + code + ").";
            return SetupText.Get(tail == null ? korean : korean + " " + tail, tail == null ? english : english + " " + tail);
        }

        private static int FreeLoopbackPort(int except)
        {
            for (var attempt = 0; ; attempt++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port;
                try { port = ((IPEndPoint)listener.LocalEndpoint).Port; }
                finally { listener.Stop(); }
                if (port != except || attempt >= 8) return port;
            }
        }

        // ---- registration (TLS front threads) ---------------------------------------------------------------------------

        private ModelShareRegistrationReply Register(string? secret, string? name)
        {
            ModelShareRegistrationReply reply;
            var changed = false;
            var registered = 0;
            try
            {
                lock (_lock)
                {
                    var code = _code;
                    if (_disposed || _run == null || code == null) reply = Error(403, "code_invalid");
                    else if (DateTimeOffset.UtcNow >= code.ExpiresUtc)
                    {
                        _code = null;
                        changed = true;
                        reply = Error(410, "code_expired");
                    }
                    else if (secret == null || secret.Length != 8 || !ModelShareCode.TryBase32(secret.ToUpperInvariant(), out var bytes)
                        || !ModelShareCode.FixedTimeEquals(bytes, code.Secret))
                    {
                        if (++code.Failures >= MaxCodeFailures)
                        {
                            _code = null;
                            changed = true;
                        }

                        reply = Error(403, "code_invalid");
                    }
                    else
                    {
                        var token = NewClientToken();
                        var client = new ModelShareClient(_share.UniqueClientName(name), Sha256Hex(token), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                        var next = _share.WithClients(_share.Clients.Concat(new[] { client }).ToList());
                        SaveLocked(next);
                        _code = null;
                        changed = true;
                        registered = next.Clients.Count;
                        reply = new ModelShareRegistrationReply(200, new JObject { ["token"] = token, ["name"] = System.Environment.MachineName });
                    }
                }
            }
            catch (ProviderSetupException)
            {
                reply = Error(503, "unavailable");
            }

            if (registered > 0) _logger.Info("Model sharing: a PC connected (" + registered.ToString(CultureInfo.InvariantCulture) + " connected).");
            if (changed) RaiseChanged();
            return reply;
        }

        private static ModelShareRegistrationReply Error(int status, string error) => new ModelShareRegistrationReply(status, new JObject { ["error"] = error });

        private static string NewClientToken()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Array.Clear(bytes, 0, bytes.Length);
            return token;
        }

        private static string Sha256Hex(string text)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(new UTF8Encoding(false).GetBytes(text));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        // ---- code expiry ------------------------------------------------------------------------------------------------

        private void ScheduleExpiry()
        {
            try { _expiry.Change(CodeLifetime + TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* Disposed. */ }
        }

        private void OnCodeTimer()
        {
            var changed = false;
            lock (_lock)
            {
                if (_code != null && DateTimeOffset.UtcNow >= _code.ExpiresUtc)
                {
                    _code = null;
                    changed = true;
                }
            }

            if (changed) RaiseChanged();
        }

        // ---- files ------------------------------------------------------------------------------------------------------

        /// <summary>Writes settings and the gateway clients file and adopts <paramref name="next"/>; caller holds <see cref="_lock"/>.</summary>
        private void SaveLocked(ModelShareSettings next)
        {
            try
            {
                next.Write(_settingsPath);
                next.WriteGatewayClients(_gatewayClientsPath);
            }
            catch (Exception ex)
            {
                _logger.Warn("The model share settings could not be saved: " + ex.GetType().Name);
                // Keep the gateway on the last good list.
                try { _share.WriteGatewayClients(_gatewayClientsPath); }
                catch (Exception) { /* The gateway keeps what it read last. */ }
                throw new ProviderSetupException(SaveFailedText());
            }

            _share = next;
        }

        private static string SaveFailedText() => SetupText.Get("모델 공유 설정을 저장하지 못했습니다.", "The model sharing settings could not be saved.");

        /// <summary>The TLS identity: the saved one when it covers <paramref name="address"/> and is not about to expire, else a new one.</summary>
        private X509Certificate2 LoadIdentity(IPAddress address, IReadOnlyList<ShareAddress> addresses, int clients)
        {
            var existing = TryLoadIdentity();
            if (existing != null)
            {
                if (CoversAddress(existing, address) && existing.NotAfter.ToUniversalTime() > DateTime.UtcNow + IdentityRenewal) return existing;
                existing.Dispose();
                if (clients > 0) _logger.Info("Model sharing: the certificate does not cover the chosen address; a new one is created and connected PCs must connect again.");
            }

            var ips = new List<IPAddress> { address };
            foreach (var item in addresses)
            {
                if (!ips.Contains(item.Address)) ips.Add(item.Address);
            }

            byte[]? pfx = null;
            try
            {
                pfx = _certificates.CreatePfx("POMP model share " + System.Environment.MachineName, DnsNames(), ips, IdentityValidity, PfxPassword);
                var protectedBytes = Dpapi.Transform(pfx, false, IdentityPurpose);
                WriteBytesAtomically(_identityPath, protectedBytes);
                return LoadPfx(pfx);
            }
            catch (Exception ex)
            {
                _logger.Warn("The model share certificate could not be created: " + ex.GetType().Name);
                throw new ProviderSetupException(SetupText.Get("공유용 인증서를 준비하지 못했습니다.", "The sharing certificate could not be prepared."));
            }
            finally
            {
                if (pfx != null) Array.Clear(pfx, 0, pfx.Length);
            }
        }

        private X509Certificate2? TryLoadIdentity()
        {
            byte[]? pfx = null;
            try
            {
                var info = new FileInfo(_identityPath);
                if (!info.Exists) return null;
                if (info.Length > MaxIdentityBytes) throw new InvalidDataException();
                pfx = Dpapi.Transform(File.ReadAllBytes(_identityPath), true, IdentityPurpose);
                var certificate = LoadPfx(pfx);
                if (certificate.HasPrivateKey) return certificate;
                certificate.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                _logger.Warn("The saved model share certificate could not be read (" + ex.GetType().Name + "); a new one is created.");
                return null;
            }
            finally
            {
                if (pfx != null) Array.Clear(pfx, 0, pfx.Length);
            }
        }

        // A user key set Schannel can use for the server handshake; not persisted, so disposing the certificate removes the key container.
        private static X509Certificate2 LoadPfx(byte[] pfx) => new X509Certificate2(pfx, PfxPassword, X509KeyStorageFlags.UserKeySet);

        private static IReadOnlyList<string> DnsNames()
        {
            var names = new List<string>();
            void Add(string? name)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                var value = name!.Trim().TrimEnd('.').ToLowerInvariant();
                if (value.Length > 0 && value.Length <= 253 && !names.Contains(value)) names.Add(value);
            }

            Add(System.Environment.MachineName);
            try
            {
                var properties = IPGlobalProperties.GetIPGlobalProperties();
                Add(properties.HostName);
                if (!string.IsNullOrWhiteSpace(properties.DomainName)) Add(properties.HostName + "." + properties.DomainName);
            }
            catch (Exception) { /* The machine name is enough. */ }

            Add("localhost");
            return names;
        }

        /// <summary>True when the certificate's subject alternative names list <paramref name="address"/> as an IP address.</summary>
        internal static bool CoversAddress(X509Certificate2 certificate, IPAddress address)
        {
            var wanted = address.GetAddressBytes();
            foreach (var extension in certificate.Extensions)
            {
                if (extension.Oid?.Value != "2.5.29.17") continue;
                var der = extension.RawData;
                var i = 0;
                if (der.Length < 2 || der[i++] != 0x30 || !ReadLength(der, ref i, out var length) || i + length > der.Length) return false;
                var end = i + length;
                while (i < end)
                {
                    var tag = der[i++];
                    if (!ReadLength(der, ref i, out var size) || i + size > end) return false;
                    if (tag == 0x87 && size == wanted.Length)
                    {
                        var same = true;
                        for (var k = 0; k < size; k++) same &= der[i + k] == wanted[k];
                        if (same) return true;
                    }

                    i += size;
                }
            }

            return false;
        }

        private static bool ReadLength(byte[] der, ref int index, out int length)
        {
            length = 0;
            if (index >= der.Length) return false;
            int first = der[index++];
            if (first < 0x80)
            {
                length = first;
                return true;
            }

            var count = first & 0x7f;
            if (count == 0 || count > 3) return false;
            for (var k = 0; k < count; k++)
            {
                if (index >= der.Length) return false;
                length = (length << 8) | der[index++];
            }

            return true;
        }

        private static void WriteBytesAtomically(string path, byte[] bytes)
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception) { /* A stray temp file is harmless (DPAPI-protected). */ }
            }
        }

        // ---- engine -----------------------------------------------------------------------------------------------------

        /// <summary>omp resolved like the chat engine (same policy as <c>ProviderSetup.NewAuxiliaryLaunch</c>), started in <c>&lt;data&gt;\workspace</c>.</summary>
        private Engine ResolveEngine()
        {
            var resolution = _resolver.Resolve(_settings.OmpExecutablePath, _environment.ExtensionDirectory);
            // The same engine policy as the chat engine (docs/architecture.md "엔진 버전"): a damaged bundled engine starts nothing.
            var refusal = BundledEngine.Refusal(resolution, _environment.ExtensionDirectory, out _);
            if (refusal != null) throw new ProviderSetupException(refusal);
            if (!resolution.Found) throw new ProviderSetupException(SetupText.Get("POMP 에이전트 실행 파일을 찾을 수 없습니다.", "The POMP agent executable was not found."));
            if (resolution.ConfiguredPathProblem != null)
                _logger.Warn(resolution.ConfiguredPathProblem + " Using " + resolution.Path + " (" + resolution.Source + ") instead.");
            string workspace;
            try
            {
                workspace = Path.Combine(Path.GetFullPath(_environment.DataDirectory), "workspace");
                Directory.CreateDirectory(workspace);
            }
            catch (Exception)
            {
                throw new ProviderSetupException(SetupText.Get("POMP 데이터 폴더를 준비하지 못했습니다.", "The POMP data folder could not be prepared."));
            }

            return new Engine(resolution.Path!, workspace, _environment.DataDirectory);
        }

        private sealed class Engine
        {
            private readonly string _path;
            private readonly string _workspace;
            private readonly string _dataDirectory;

            public Engine(string path, string workspace, string dataDirectory)
            {
                _path = path;
                _workspace = workspace;
                _dataDirectory = dataDirectory;
            }

            public OmpLaunchOptions Launch(params string[] arguments)
            {
                var launch = new OmpLaunchOptions { ExecutablePath = _path, WorkingDirectory = _workspace };
                BundledEngine.UseOwnNatives(launch, _dataDirectory);
                foreach (var argument in arguments) launch.Arguments.Add(argument);
                return launch;
            }
        }

        // ---- addresses --------------------------------------------------------------------------------------------------

        private IReadOnlyList<ShareAddress> Addresses(bool fresh = false)
        {
            lock (_lock)
            {
                if (!fresh && _addresses != null && DateTime.UtcNow - _addressesReadUtc < AddressCacheTime) return _addresses;
            }

            var list = ListAddresses();
            lock (_lock)
            {
                _addresses = list;
                _addressesReadUtc = DateTime.UtcNow;
            }

            return list;
        }

        /// <summary>IPv4 addresses of the interfaces that are up (no link-local), in interface order, then 127.0.0.1.</summary>
        private static IReadOnlyList<ShareAddress> ListAddresses()
        {
            var result = new List<ShareAddress>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties properties;
                    try { properties = network.GetIPProperties(); }
                    catch (NetworkInformationException) { continue; }
                    foreach (var unicast in properties.UnicastAddresses)
                    {
                        var address = unicast.Address;
                        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) continue;
                        var bytes = address.GetAddressBytes();
                        if (bytes[0] == 169 && bytes[1] == 254) continue;
                        if (seen.Add(address.ToString())) result.Add(new ShareAddress(address, SetupSanitize.Line(network.Name, 100) ?? address.ToString()));
                    }
                }
            }
            catch (NetworkInformationException) { /* Only loopback then. */ }

            result.Add(new ShareAddress(IPAddress.Loopback, SetupText.Get("이 PC만", "This PC only")));
            return result;
        }

        private sealed class ShareAddress
        {
            public ShareAddress(IPAddress address, string name)
            {
                Address = address;
                Name = name;
            }

            public IPAddress Address { get; }
            public string Name { get; }
        }

        // ---- helpers ----------------------------------------------------------------------------------------------------

        private void SetStateLocked(string state, string? error)
        {
            _state = state;
            _error = error;
            if (state != "on")
            {
                _models = null;
                _code = null;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ModelShareService));
        }

        private void RaiseChanged()
        {
            lock (_lock)
            {
                if (_disposed) return;
            }

            try { Changed?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Warn("A model share state handler failed: " + ex.GetType().Name); }
        }

        public void Dispose()
        {
            Run? run;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                run = _run;
                _run = null;
                _generation++;
                _code = null;
            }

            _disposeCts.Cancel();
            run?.Stop();
            _expiry.Dispose();
        }

        /// <summary>The current one-time code (secret + pin text); <see cref="Failures"/> changes under the service lock.</summary>
        private sealed class ActiveCode
        {
            private ActiveCode(byte[] secret, string text, DateTimeOffset expiresUtc)
            {
                Secret = secret;
                Text = text;
                ExpiresUtc = expiresUtc;
            }

            public byte[] Secret { get; }
            public string Text { get; }
            public DateTimeOffset ExpiresUtc { get; }
            public int Failures { get; set; }

            public static ActiveCode Create(byte[] pin)
            {
                var secret = new byte[ModelShareCode.SecretLength];
                using (var random = RandomNumberGenerator.Create()) random.GetBytes(secret);
                return new ActiveCode(secret, ModelShareCode.Format(secret, pin), DateTimeOffset.UtcNow + CodeLifetime);
            }
        }

        /// <summary>
        /// One start of sharing and everything it owns (certificate, listener, servers, HTTP client). <see cref="Stop"/> disposes them in
        /// reverse order, once; anything attached after that is disposed at once. <see cref="Started"/> and <see cref="Fault"/> change under the service lock.
        /// </summary>
        private sealed class Run
        {
            private readonly object _gate = new object();
            private readonly List<IDisposable> _owned = new List<IDisposable>();
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private bool _stopped;

            public Run()
            {
                Http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 4 * 1024 * 1024 };
            }

            public HttpClient Http { get; }
            public CancellationToken Token => _cts.Token;
            public List<ModelShareProcess> Processes { get; } = new List<ModelShareProcess>();
            public byte[]? Pin { get; set; }
            public string Endpoint { get; set; } = string.Empty;
            public bool Started { get; set; }
            public string? Fault { get; set; }

            public T Attach<T>(T item) where T : IDisposable
            {
                lock (_gate)
                {
                    if (!_stopped)
                    {
                        _owned.Add(item);
                        return item;
                    }
                }

                item.Dispose();
                throw new OperationCanceledException();
            }

            /// <summary>Text for the first server that already exited, or null.</summary>
            public string? ExitedProcessText()
            {
                var exited = Processes.FirstOrDefault(process => process.HasExited);
                return exited == null ? null : ExitedText(exited);
            }

            public void CancelStart()
            {
                try { _cts.Cancel(); }
                catch (ObjectDisposedException) { /* Stopped. */ }
            }

            public void Stop()
            {
                IDisposable[] owned;
                lock (_gate)
                {
                    if (_stopped) return;
                    _stopped = true;
                    owned = _owned.ToArray();
                    _owned.Clear();
                }

                CancelStart();
                for (var i = owned.Length - 1; i >= 0; i--)
                {
                    try { owned[i].Dispose(); }
                    catch (Exception) { /* Shutdown is best effort. */ }
                }

                try { Http.Dispose(); }
                catch (Exception) { /* Shutdown is best effort. */ }
            }
        }
    }
}
