using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Internal;

namespace Pomp.Features
{
    // Sign-out: omp 18.7 added an RPC logout per account (not used yet); the host runs the CLI's `omp auth-broker logout <provider>` (hidden, one-shot).
    internal sealed partial class ProviderSetup
    {
        /// <summary>The longest a sign-out may take; the usage limit applies instead when it is shorter.</summary>
        private static readonly TimeSpan MaxLogoutTime = TimeSpan.FromSeconds(20);

        private readonly Func<OmpLaunchOptions, CancellationToken, Task> _runLogout;
        private readonly TimeSpan _logoutTimeout;

        /// <summary>
        /// <c>logout</c>: omp soft-deletes every stored sign-in of the provider (OAuth logins and keys typed into a login flow; it cannot tell
        /// the accounts of one provider apart). API keys saved in POMP, environment variables and omp's config are not touched, so the
        /// provider may still be authenticated afterwards. Like <c>removeKey</c> it changes what a running engine would see: it adopts the change
        /// lazily, so a restart is recommended (or the engine, when it is not running, is started by the caller).
        /// </summary>
        /// <returns>Whether the engine, which is not running, should be started now.</returns>
        private async Task<bool> LogoutAsync(JObject request)
        {
            var provider = ReadLoginProvider(request, required: true);
            lock (_lock)
            {
                if (_disposed) throw new OperationCanceledException();
                // A sign-in writes the same profile and owns the helper: one change at a time.
                if (_login != null || _run != null)
                    throw new ProviderSetupException(SetupText.Get("로그인이 진행 중이어서 로그아웃할 수 없습니다.", "A provider login is running, so signing out is not possible."));
            }

            var launch = NewAuxiliaryLaunch();
            launch.Arguments.Add("auth-broker");
            launch.Arguments.Add("logout");
            launch.Arguments.Add(provider);
            // This run needs none of the saved API keys (and removes none): they stay out of its environment.
            launch.Environment.Clear();
            // No omp of ours may hold the profile while its sign-ins are rewritten; the next provider list comes from a new helper.
            _helper.Kill("a provider is being signed out");
            try { await _runLogout(launch, _token).ConfigureAwait(false); }
            catch (ProviderSetupException)
            {
                _logger.Warn("Signing out of " + provider + " failed.");
                throw;
            }

            _logger.Info("Signed out of " + provider + ".");
            lock (_lock)
            {
                // The account's limits are not shown any more (the page asks for the usage again when it wants to).
                if (_usage?["reports"] is JArray reports)
                {
                    foreach (var report in reports.OfType<JObject>().Where(item => item.GetString("provider") == provider).ToList()) report.Remove();
                }
            }

            return CredentialsChanged();
        }

        /// <summary>
        /// The helper was ended before the sign-out, so a loaded provider list is read again from a new one while the command is still
        /// answered: the answer carries the list as it is now (the page tells from it whether the provider is still authenticated).
        /// </summary>
        private async Task ReloadProvidersAfterLogoutAsync()
        {
            lock (_lock)
            {
                if (_providers == null) return; // The page never loaded the list: there is nothing out of date.
            }

            try { await RefreshProvidersAsync().ConfigureAwait(false); }
            catch (ProviderSetupException ex)
            {
                // A list that still shows the provider as signed in would claim the sign-out failed: the page gets the error state instead.
                lock (_lock) _providers = null;
                throw new ProviderSetupException(SetupText.Get("로그아웃했지만 계정 목록을 다시 읽지 못했습니다. ", "Signed out, but the account list could not be read again. ") + ex.Message);
            }
        }
    }

    /// <summary>
    /// Runs <c>omp auth-broker logout &lt;provider&gt;</c> hidden (no window, redirected output, no input) through the session's launcher, so a
    /// test can substitute it. It is bounded in time and output, and only exit code 0 counts: omp prints "Logged out of ..." even when nothing
    /// was stored, so the output means nothing and is neither shown nor logged. Failures are reported with fixed, credential-free texts.
    /// </summary>
    internal static class OmpLogoutRunner
    {
        private const int MaxOutputBytes = 64 * 1024;

        /// <exception cref="ProviderSetupException">The run could not start, timed out, printed too much or did not end with exit code 0.</exception>
        public static async Task RunAsync(IOmpProcessLauncher launcher, OmpLaunchOptions launch, TimeSpan timeout, IPompLogger logger, CancellationToken cancellationToken)
        {
            IOmpProcess process;
            try { process = launcher.Start(launch); }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                logger.Warn("omp sign-out could not be started: " + ex.GetType().Name);
                throw new ProviderSetupException(SetupText.Get("로그아웃하지 못했습니다.", "Sign-out could not be started."));
            }

            using (process)
            {
                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler onExited = (sender, e) => exited.TrySetResult(true);
                process.Exited += onExited;
                try
                {
                    if (process.HasExited) exited.TrySetResult(true);
                    try { process.StandardInput.Dispose(); }
                    catch (Exception) { /* Nothing is written to it. */ }

                    var overflow = 0;
                    var stdout = OmpUsageRunner.ReadBoundedAsync(process.StandardOutput, MaxOutputBytes, () =>
                    {
                        Interlocked.Exchange(ref overflow, 1);
                        Kill(process, logger);
                    });
                    var stderr = OmpUsageRunner.ReadBoundedAsync(process.StandardError, MaxOutputBytes, null);
                    var all = Task.WhenAll(stdout, stderr, exited.Task);
                    using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        limit.CancelAfter(timeout);
                        try { await all.WithCancellation(limit.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException)
                        {
                            Kill(process, logger);
                            all.Observe();
                            if (cancellationToken.IsCancellationRequested) throw;
                            var seconds = timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
                            throw new ProviderSetupException(SetupText.Get("로그아웃이 시간 초과되었습니다 (" + seconds + "초).", "Signing out timed out (" + seconds + " s)."));
                        }
                    }

                    if (Volatile.Read(ref overflow) == 1)
                        throw new ProviderSetupException(SetupText.Get("로그아웃 응답이 너무 깁니다.", "The sign-out response was too long."));
                    var code = process.ExitCode;
                    if (code != 0)
                    {
                        logger.Debug("omp sign-out exited with " + (code.HasValue ? "code " + code.Value.ToString(CultureInfo.InvariantCulture) : "an unknown status") + ".");
                        throw new ProviderSetupException(code.HasValue
                            ? SetupText.Get("로그아웃이 코드 " + code.Value.ToString(CultureInfo.InvariantCulture) + "(으)로 끝났습니다.", "Signing out exited with code " + code.Value.ToString(CultureInfo.InvariantCulture) + ".")
                            : SetupText.Get("로그아웃의 종료 상태를 알 수 없습니다.", "The sign-out exit status is unknown."));
                    }
                }
                finally { process.Exited -= onExited; }
            }
        }

        private static void Kill(IOmpProcess process, IPompLogger logger)
        {
            try { process.Kill(); }
            catch (Exception ex) { logger.Debug("Killing omp sign-out failed: " + ex.GetType().Name); }
        }
    }
}
