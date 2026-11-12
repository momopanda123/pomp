using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Internal;

namespace Pomp.Features.ModelShare
{
    /// <summary>One project PC that registered with this PC: its name and the SHA-256 of its gateway token (never the token).</summary>
    internal sealed class ModelShareClient
    {
        public ModelShareClient(string name, string tokenSha256, string createdUtc)
        {
            Name = name;
            TokenSha256 = tokenSha256;
            CreatedUtc = createdUtc;
        }

        public string Name { get; }
        public string TokenSha256 { get; }
        public string CreatedUtc { get; }
    }

    /// <summary>
    /// <c>model-share\settings.json</c>: <c>{ version:1, enabled, address, port, clients:[{ name, tokenSha256, createdUtc }] }</c>, and the
    /// stock omp gateway clients file derived from it. Immutable; a change is a new instance written atomically.
    /// </summary>
    internal sealed class ModelShareSettings
    {
        public const int DefaultPort = 47611;
        public const int MaxClientName = 40;
        private const int MaxClients = 256;
        private const long MaxFileBytes = 256 * 1024;
        private static readonly Regex Sha256Hex = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

        public ModelShareSettings(bool enabled, string? address, int port, IReadOnlyList<ModelShareClient> clients)
        {
            Enabled = enabled;
            Address = address;
            Port = port;
            Clients = clients;
        }

        public static ModelShareSettings Default { get; } = new ModelShareSettings(false, null, DefaultPort, Array.Empty<ModelShareClient>());

        public bool Enabled { get; }
        public string? Address { get; }
        public int Port { get; }
        public IReadOnlyList<ModelShareClient> Clients { get; }

        public ModelShareSettings WithEnabled(bool enabled) => new ModelShareSettings(enabled, Address, Port, Clients);
        public ModelShareSettings WithEndpoint(string? address, int port) => new ModelShareSettings(Enabled, address, port, Clients);
        public ModelShareSettings WithClients(IReadOnlyList<ModelShareClient> clients) => new ModelShareSettings(Enabled, Address, Port, clients);

        /// <summary>An IPv4 address in dotted form, or null.</summary>
        public static string? NormalizeAddress(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var trimmed = text!.Trim();
            if (trimmed.Count(c => c == '.') != 3) return null;
            return IPAddress.TryParse(trimmed, out var address) && address.AddressFamily == AddressFamily.InterNetwork ? address.ToString() : null;
        }

        public static bool ValidPort(int port) => port >= 1024 && port <= 65535;

        /// <summary>Reads the file; a missing file gives the defaults, a damaged one is logged and gives the defaults (clients that do not validate are dropped).</summary>
        public static ModelShareSettings Read(string path, IPompLogger logger)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return Default;
                if (info.Length > MaxFileBytes) throw new InvalidDataException("too large");
                JObject root;
                using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None })
                {
                    root = JObject.Load(reader);
                }

                var enabled = root["enabled"]?.Type == JTokenType.Boolean && (bool)root["enabled"]!;
                var address = root["address"]?.Type == JTokenType.String ? NormalizeAddress((string?)root["address"]) : null;
                var port = root["port"]?.Type == JTokenType.Integer ? (long)root["port"]! : DefaultPort;
                var clients = new List<ModelShareClient>();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in (root["clients"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var name = item["name"]?.Type == JTokenType.String ? (string?)item["name"] : null;
                    var hash = item["tokenSha256"]?.Type == JTokenType.String ? (string?)item["tokenSha256"] : null;
                    var created = item["createdUtc"]?.Type == JTokenType.String ? (string?)item["createdUtc"] : null;
                    if (name == null || SanitizeName(name) != name || hash == null || !Sha256Hex.IsMatch(hash) || !names.Add(name)) continue;
                    if (clients.Any(client => client.TokenSha256 == hash)) continue;
                    clients.Add(new ModelShareClient(name, hash, created != null && created.Length <= 64 ? created : string.Empty));
                    if (clients.Count == MaxClients) break;
                }

                return new ModelShareSettings(enabled, address, port >= 1024 && port <= 65535 ? (int)port : DefaultPort, clients);
            }
            catch (Exception ex)
            {
                logger.Warn("The model share settings could not be read (" + ex.GetType().Name + "); using defaults.");
                return Default;
            }
        }

        public void Write(string path)
        {
            var clients = new JArray();
            foreach (var client in Clients)
            {
                clients.Add(new JObject { ["name"] = client.Name, ["tokenSha256"] = client.TokenSha256, ["createdUtc"] = client.CreatedUtc });
            }

            var root = new JObject
            {
                ["version"] = 1,
                ["enabled"] = Enabled,
                ["address"] = Address,
                ["port"] = Port,
                ["clients"] = clients,
            };
            AtomicFile.WriteAllText(path, root.ToString(Formatting.Indented));
        }

        /// <summary>The stock omp <c>--clients</c> file: <c>{ "&lt;name&gt;": { "tokenSha256": "&lt;hex&gt;" } }</c> (every served model).</summary>
        public void WriteGatewayClients(string path)
        {
            var root = new JObject();
            foreach (var client in Clients) root[client.Name] = new JObject { ["tokenSha256"] = client.TokenSha256 };
            AtomicFile.WriteAllText(path, root.ToString(Formatting.Indented));
        }

        /// <summary>A client name the gateway accepts: one printable line of at most 40 characters; null when nothing printable is left.</summary>
        public static string? SanitizeName(string? text) => SetupSanitize.Line(text, MaxClientName);

        /// <summary><paramref name="wanted"/> sanitized and made unique among the clients with a <c>-2</c>, <c>-3</c>… suffix.</summary>
        public string UniqueClientName(string? wanted)
        {
            var name = SanitizeName(wanted) ?? "PC";
            var taken = new HashSet<string>(Clients.Select(client => client.Name), StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(name)) return name;
            for (var number = 2; ; number++)
            {
                var suffix = "-" + number.ToString(CultureInfo.InvariantCulture);
                var stem = name.Length + suffix.Length > MaxClientName ? name.Substring(0, MaxClientName - suffix.Length).TrimEnd() : name;
                var candidate = stem + suffix;
                if (!taken.Contains(candidate)) return candidate;
            }
        }
    }
}
