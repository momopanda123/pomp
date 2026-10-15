using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Internal;

namespace Pomp.Features
{
    /// <summary>
    /// Model servers the user runs themselves (any OpenAI-compatible server: a name, its address, an optional key), saved next to the
    /// API keys in <c>credentials\local-models.json</c> with the key encrypted like the API keys. The engine gets them as
    /// <c>POMP_LOCAL_MODELS</c>; the POMP extension (<c>extensions/local-models.ts</c>) registers each as a provider whose models are
    /// the server's model list.
    /// </summary>
    internal sealed partial class ProviderCredentialStore
    {
        private const int MaxLocalFileBytes = 256 * 1024;
        /// <summary>The servers travel to omp in one environment variable (Windows allows 32,767 characters).</summary>
        internal const int MaxLocalLaunchChars = 30000;
        private static readonly Regex LocalServerId = new Regex("^local-[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant);

        private string LocalFile => Path.Combine(Path.GetDirectoryName(_file)!, "local-models.json");

        /// <summary>Adds a server, or replaces the one with <paramref name="id"/>. An empty key keeps that server's saved key.</summary>
        public string SaveLocalServer(string? id, string? name, string? address, string? key)
        {
            name = name?.Trim();
            address = address?.Trim();
            if (string.IsNullOrEmpty(key)) key = null;
            ValidateServerName(name);
            ValidateAddress(address);
            if (key != null) ValidateKey(key);
            lock (_lock)
            {
                try
                {
                    var servers = ReadLocalServers();
                    JObject? entry = null;
                    if (!string.IsNullOrEmpty(id))
                    {
                        entry = servers.OfType<JObject>().FirstOrDefault(server => (string?)server["id"] == id)
                            ?? throw new ProviderSetupException(SetupText.Get("그 로컬 서버를 찾을 수 없습니다.", "That local server was not found."));
                    }
                    if (servers.OfType<JObject>().Any(server => server != entry && string.Equals((string?)server["name"], name, StringComparison.OrdinalIgnoreCase)))
                        throw new ProviderSetupException(SetupText.Get("같은 이름의 로컬 서버가 이미 있습니다.", "A local server with this name already exists."));
                    if (entry == null)
                    {
                        entry = new JObject { ["id"] = NewServerId(name!, servers) };
                        servers.Add(entry);
                    }

                    entry["name"] = name;
                    entry["address"] = address;
                    entry["savedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    if (key != null)
                    {
                        var bytes = Encoding.UTF8.GetBytes(key);
                        try { entry["key"] = Convert.ToBase64String(Dpapi.Transform(bytes, false)); }
                        finally { Array.Clear(bytes, 0, bytes.Length); }
                    }

                    if (LaunchValue(servers).Length > MaxLocalLaunchChars)
                        throw new ProviderSetupException(SetupText.Get("로컬 서버 목록이 너무 커서 더 저장할 수 없습니다.", "The local server list is too large to save more."));
                    WriteLocalServers(servers);
                    return (string)entry["id"]!;
                }
                catch (ProviderSetupException) { throw; }
                catch (Exception)
                {
                    // Never retain an exception whose text could contain credential material.
                    throw new ProviderSetupException(SetupText.Get("로컬 서버를 저장하지 못했습니다.", "The local server could not be saved."));
                }
            }
        }

        public void RemoveLocalServer(string? id)
        {
            lock (_lock)
            {
                try
                {
                    var servers = ReadLocalServers();
                    var entry = servers.OfType<JObject>().FirstOrDefault(server => (string?)server["id"] == id)
                        ?? throw new ProviderSetupException(SetupText.Get("그 로컬 서버를 찾을 수 없습니다.", "That local server was not found."));
                    servers.Remove(entry);
                    WriteLocalServers(servers);
                }
                catch (ProviderSetupException) { throw; }
                catch (Exception)
                {
                    throw new ProviderSetupException(SetupText.Get("로컬 서버를 삭제하지 못했습니다.", "The local server could not be removed."));
                }
            }
        }

        /// <summary>The saved servers for the page: id, name, address, whether a key is saved, when. Never the key.</summary>
        public JArray LocalServers()
        {
            lock (_lock)
            {
                return new JArray(ReadLocalServers().OfType<JObject>().Select(server => new JObject
                {
                    ["id"] = server["id"]!.DeepClone(), ["name"] = server["name"]!.DeepClone(), ["address"] = server["address"]!.DeepClone(),
                    ["hasKey"] = server["key"] != null, ["savedUtc"] = server["savedUtc"]!.DeepClone(),
                }));
            }
        }

        /// <summary>The value of <c>POMP_LOCAL_MODELS</c>: <c>[{ id, name, address, key? }]</c> as JSON, or null without servers.</summary>
        public string? LocalServersLaunchValue()
        {
            lock (_lock)
            {
                var servers = ReadLocalServers();
                if (servers.Count == 0) return null;
                var value = LaunchValue(servers);
                if (value.Length <= MaxLocalLaunchChars) return value;
                _logger.Warn("The local model servers do not fit the omp launch environment; none were passed.");
                return null;
            }
        }

        private string LaunchValue(JArray servers)
        {
            var rows = new JArray();
            foreach (var server in servers.OfType<JObject>())
            {
                var row = new JObject { ["id"] = server["id"]!.DeepClone(), ["name"] = server["name"]!.DeepClone(), ["address"] = server["address"]!.DeepClone() };
                if (server["key"] is JValue encrypted && encrypted.Type == JTokenType.String)
                {
                    try
                    {
                        var bytes = Dpapi.Transform(Convert.FromBase64String((string)encrypted!), true);
                        try
                        {
                            var key = new UTF8Encoding(false, true).GetString(bytes);
                            ValidateKey(key);
                            row["key"] = key;
                        }
                        finally { Array.Clear(bytes, 0, bytes.Length); }
                    }
                    catch (Exception)
                    {
                        _logger.Warn("A saved local server key could not be decrypted; the server is passed without it.");
                    }
                }
                rows.Add(row);
            }
            return rows.ToString(Formatting.None);
        }

        private static string NewServerId(string name, JArray servers)
        {
            var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (slug.Length > 30) slug = slug.Substring(0, 30).TrimEnd('-');
            if (slug.Length == 0) slug = "server";
            var taken = servers.OfType<JObject>().Select(server => (string?)server["id"]).ToList();
            var id = "local-" + slug;
            for (var n = 2; taken.Contains(id); n++) id = "local-" + slug + "-" + n.ToString(CultureInfo.InvariantCulture);
            return id;
        }

        private static void ValidateServerName(string? name)
        {
            if (string.IsNullOrEmpty(name) || name!.Length > 40 || name.Any(char.IsControl))
                throw new ProviderSetupException(SetupText.Get("로컬 서버 이름이 올바르지 않습니다.", "The local server name is invalid."));
        }

        /// <summary>An absolute http(s) address without user information, spaces or control characters.</summary>
        internal static void ValidateAddress(string? address)
        {
            if (address == null || address.Length > 2048 || address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
                || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || uri.UserInfo.Length > 0 || string.IsNullOrEmpty(uri.Host))
                throw new ProviderSetupException(SetupText.Get("주소 형식이 올바르지 않습니다.", "The address is invalid."));
        }

        private JArray ReadLocalServers()
        {
            try
            {
                if (!File.Exists(LocalFile)) return new JArray();
                using var stream = new FileStream(LocalFile, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (stream.Length > MaxLocalFileBytes) throw new InvalidDataException();
                using var text = new StreamReader(stream, Encoding.UTF8, true);
                using var reader = new JsonTextReader(text) { MaxDepth = 6, DateParseHandling = DateParseHandling.None };
                var data = JObject.Load(reader);
                if (reader.Read() || data["version"]?.Type != JTokenType.Integer || (int)data["version"]! != 1 || !(data["servers"] is JArray servers))
                    throw new InvalidDataException();
                var ids = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach (var token in servers)
                {
                    if (!(token is JObject server) || server["id"]?.Type != JTokenType.String || !LocalServerId.IsMatch((string)server["id"]!) || !ids.Add((string)server["id"]!)
                        || server["name"]?.Type != JTokenType.String || server["address"]?.Type != JTokenType.String || server["savedUtc"]?.Type != JTokenType.String
                        || (server["key"] != null && (server["key"]!.Type != JTokenType.String || ((string)server["key"]!).Length > 32768)))
                        throw new InvalidDataException();
                    ValidateServerName((string)server["name"]!);
                    ValidateAddress((string)server["address"]!);
                    if (server["key"] != null) Convert.FromBase64String((string)server["key"]!);
                }
                return servers;
            }
            catch (Exception)
            {
                _logger.Warn("The local model server file could not be read or was corrupt; treating it as empty.");
                return new JArray();
            }
        }

        private void WriteLocalServers(JArray servers) =>
            AtomicFile.WriteAllText(LocalFile, new JObject { ["version"] = 1, ["servers"] = servers }.ToString(Formatting.None));
    }
}
