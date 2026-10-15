using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Ui;

namespace Pomp.Hosting
{
    // Which omp runs (docs/architecture.md "엔진 버전"): the engine the POMP package ships, pinned by size and SHA-256.
    public sealed partial class PompSession
    {
        private EngineInfoPayload? _engineInfo;
        private string? _probedEnginePath;

        /// <summary>
        /// The omp to start. A package with an engine uses that engine, verified, unless the options name another omp; a damaged or
        /// replaced bundled engine stops the start (no silent fallback to whatever omp the PC has). A development package without an
        /// engine uses an installed omp. Anything that is not the verified bundled engine is reported as unverified.
        /// </summary>
        private async Task<OmpExecutableResolution> ResolveEngineAsync(CancellationToken cancellationToken)
        {
            var resolution = _resolver.Resolve(_settings.OmpExecutablePath, _environment.ExtensionDirectory);
            BundledEngineCheck bundle = null!;
            var refusal = await Task.Run(() => BundledEngine.Refusal(resolution, _environment.ExtensionDirectory, out bundle), cancellationToken).ConfigureAwait(false);
            var overridden = resolution.Found && resolution.Source == "settings";
            if (refusal != null)
            {
                _logger.Error("The bundled omp engine cannot be used: " + bundle.Problem);
                throw new OmpBridgeException(refusal);
            }

            if (!resolution.Found)
            {
                throw new OmpBridgeException(OmpExecutableResolver.DescribeNotFound(resolution));
            }

            var verified = bundle.Status == BundledEngineStatus.Verified && !overridden
                && string.Equals(Path.GetFullPath(resolution.Path!), bundle.Path, StringComparison.OrdinalIgnoreCase);
            var info = verified
                ? new EngineInfoPayload { Source = "bundled", Version = bundle.Version, Verified = true, ExpectedVersion = bundle.Version }
                : new EngineInfoPayload { Source = overridden ? "settings" : "installed", Verified = false, ExpectedVersion = bundle.Version, Path = resolution.Path };
            if (!verified)
            {
                _logger.Warn("omp engine not verified for this POMP release: " + resolution.Path + " (" + info.Source + ")"
                    + (bundle.Version != null ? "; this release ships omp " + bundle.Version + "." : "; this package has no bundled engine."));
            }

            PublishEngineInfo(info, resolution.Path!);
            return resolution;
        }

        private void PublishEngineInfo(EngineInfoPayload info, string path)
        {
            bool probe;
            lock (_stateLock)
            {
                // A restart of the same unverified engine keeps the version found before.
                if (!info.Verified && string.Equals(_probedEnginePath, path, StringComparison.OrdinalIgnoreCase) && _engineInfo?.Path == path)
                {
                    info.Version = _engineInfo.Version;
                }

                _engineInfo = info;
                probe = !info.Verified && info.Version == null && !string.Equals(_probedEnginePath, path, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetFileNameWithoutExtension(path), "omp", StringComparison.OrdinalIgnoreCase);
                if (probe) _probedEnginePath = path;
            }

            _ui.Post(UiMessageTypes.EngineInfo, info, UiDelivery.State);
            if (probe) ProbeEngineVersionAsync(path).Forget(_logger, "omp version check");
        }

        /// <summary>Runs <c>omp --version</c> once for an engine that is not the bundled one, so the page can show which version runs.</summary>
        private async Task ProbeEngineVersionAsync(string path)
        {
            var launch = new OmpLaunchOptions { ExecutablePath = path, WorkingDirectory = Path.GetFullPath(_environment.DataDirectory) };
            launch.Arguments.Add("--version");
            string? version;
            try
            {
                Directory.CreateDirectory(launch.WorkingDirectory);
                var output = await OmpUsageRunner.RunAsync(_processLauncher, launch, TimeSpan.FromSeconds(15), 64 * 1024, _logger, _disposeCts.Token).ConfigureAwait(false);
                version = BundledEngine.ParseVersionOutput(output);
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Warn("Could not read the omp version of " + path + ": " + ex.Message);
                return;
            }

            if (version == null) return;
            _logger.Info("omp engine " + path + " reports version " + version + ".");
            EngineInfoPayload? updated = null;
            lock (_stateLock)
            {
                if (_engineInfo != null && !_engineInfo.Verified && string.Equals(_engineInfo.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    updated = new EngineInfoPayload { Source = _engineInfo.Source, Version = version, Verified = false, ExpectedVersion = _engineInfo.ExpectedVersion, Path = path };
                    _engineInfo = updated;
                }
            }

            if (updated != null) _ui.Post(UiMessageTypes.EngineInfo, updated, UiDelivery.State);
        }
    }
}
