using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Internal;

namespace Pomp.Features
{
    /// <summary>Allowlisted provider keys, encrypted with Windows DPAPI for the current user.</summary>
    internal sealed partial class ProviderCredentialStore
    {
        private static readonly ConcurrentDictionary<string, object> FileLocks = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The DPAPI purpose of the saved API keys and local server keys (changing it would orphan every saved key).</summary>
        private const string KeyPurpose = "POMP provider key v1";
        private static readonly Provider[] Providers =
        {
            new Provider("anthropic", "Anthropic (Claude)", "ANTHROPIC_API_KEY"),
            new Provider("openai", "OpenAI", "OPENAI_API_KEY"),
            new Provider("google", "Google Gemini", "GEMINI_API_KEY"),
            new Provider("openrouter", "OpenRouter", "OPENROUTER_API_KEY"),
            new Provider("xai", "xAI (Grok)", "XAI_API_KEY"),
            new Provider("mistral", "Mistral", "MISTRAL_API_KEY"),
            new Provider("groq", "Groq", "GROQ_API_KEY"),
            new Provider("zai", "z.ai (GLM)", "ZAI_API_KEY"),
            // Muse Spark on pay-as-you-go terms: a Muse Code subscription only serves the Muse Code client (403 client_attestation_denied).
            new Provider("meta", "Meta Model API (Muse Spark)", "META_API_KEY"),
        };
        private readonly string _file;
        private readonly object _lock;
        private readonly IPompLogger _logger;

        public ProviderCredentialStore(string dataDirectory, IPompLogger logger)
        {
            _file = Path.Combine(Path.GetFullPath(dataDirectory), "credentials", "provider-keys.json");
            _lock = FileLocks.GetOrAdd(_file, _ => new object());
            _logger = logger;
        }

        public static JArray ApiKeyProviders() => new JArray(Providers.Select(p => new JObject { ["id"] = p.Id, ["label"] = p.Label, ["envVar"] = p.EnvVar }));

        public void Save(string? provider, string? key)
        {
            RequireProvider(provider);
            ValidateKey(key);
            lock (_lock)
            {
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(key!);
                    byte[] encrypted;
                    try { encrypted = Dpapi.Transform(bytes, false, KeyPurpose); }
                    finally { Array.Clear(bytes, 0, bytes.Length); }
                    var keys = ReadKeys();
                    keys[provider!] = new JObject { ["data"] = Convert.ToBase64String(encrypted), ["savedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
                    WriteKeys(keys);
                }
                catch (Exception)
                {
                    // Never retain an exception whose text could contain credential material.
                    throw new ProviderSetupException(SetupText.Get("API 키를 암호화하거나 저장하지 못했습니다.", "The API key could not be encrypted or saved."));
                }
            }
        }

        public void Remove(string? provider)
        {
            RequireProvider(provider);
            lock (_lock)
            {
                try
                {
                    var keys = ReadKeys();
                    if (keys.Remove(provider!)) WriteKeys(keys);
                }
                catch (Exception)
                {
                    throw new ProviderSetupException(SetupText.Get("저장된 API 키를 삭제하지 못했습니다.", "The saved API key could not be removed."));
                }
            }
        }

        public JArray SavedKeys()
        {
            lock (_lock)
            {
                var keys = ReadKeys();
                return new JArray(Providers.Where(p => keys[p.Id] != null).Select(p => new JObject
                {
                    ["provider"] = p.Id, ["envVar"] = p.EnvVar, ["savedUtc"] = keys[p.Id]!["savedUtc"]!.DeepClone(),
                }));
            }
        }

        /// <summary>Only the fixed provider environment-variable names can reach an omp launch.</summary>
        public IReadOnlyDictionary<string, string> GetLaunchEnvironment()
        {
            lock (_lock)
            {
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                var keys = ReadKeys();
                foreach (var provider in Providers)
                {
                    if (!(keys[provider.Id] is JObject entry)) continue;
                    try
                    {
                        var bytes = Dpapi.Transform(Convert.FromBase64String((string)entry["data"]!), true, KeyPurpose);
                        try
                        {
                            var key = new UTF8Encoding(false, true).GetString(bytes);
                            ValidateKey(key);
                            result.Add(provider.EnvVar, key);
                        }
                        finally { Array.Clear(bytes, 0, bytes.Length); }
                    }
                    catch (Exception)
                    {
                        _logger.Warn("A saved provider API key could not be decrypted; it was omitted from the omp launch environment.");
                    }
                }
                return result;
            }
        }

        internal static void ValidateKey(string? key)
        {
            if (key == null || key.Length < 8 || key.Length > 1024 || key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || char.IsSurrogate(c)
                || char.GetUnicodeCategory(c) == UnicodeCategory.Format || char.GetUnicodeCategory(c) == UnicodeCategory.OtherNotAssigned))
                throw new ProviderSetupException(SetupText.Get("API 키 형식이 올바르지 않습니다.", "The API key is invalid."));
        }

        private static void RequireProvider(string? id)
        {
            if (!Providers.Any(p => p.Id == id)) throw new ProviderSetupException(SetupText.Get("API 키 공급자 목록에 없는 공급자입니다.", "The provider is not in the API-key provider list."));
        }

        private JObject ReadKeys()
        {
            try
            {
                if (!File.Exists(_file)) return new JObject();
                using var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (stream.Length > 128 * 1024) throw new InvalidDataException();
                using var text = new StreamReader(stream, Encoding.UTF8, true);
                using var reader = new JsonTextReader(text) { MaxDepth = 6, DateParseHandling = DateParseHandling.None };
                var data = JObject.Load(reader);
                if (reader.Read() || data["version"]?.Type != JTokenType.Integer || (int)data["version"]! != 1 || !(data["keys"] is JObject keys)
                    || keys.Properties().Count() > Providers.Length) throw new InvalidDataException();
                foreach (var property in keys.Properties())
                {
                    RequireProvider(property.Name);
                    if (!(property.Value is JObject entry) || entry["data"]?.Type != JTokenType.String || ((string)entry["data"]!).Length > 32768
                        || entry["savedUtc"]?.Type != JTokenType.String || !DateTimeOffset.TryParse((string)entry["savedUtc"]!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                        throw new InvalidDataException();
                    Convert.FromBase64String((string)entry["data"]!);
                }
                return keys;
            }
            catch (Exception)
            {
                _logger.Warn("The provider credential file could not be read or was corrupt; treating it as empty.");
                return new JObject();
            }
        }

        private void WriteKeys(JObject keys) => AtomicFile.WriteAllText(_file, new JObject { ["version"] = 1, ["keys"] = keys }.ToString(Formatting.None));

        private sealed class Provider
        {
            public Provider(string id, string label, string envVar) { Id = id; Label = label; EnvVar = envVar; }
            public string Id { get; }
            public string Label { get; }
            public string EnvVar { get; }
        }
    }

    /// <summary>A fixed, credential-free explanation safe to publish to the setup UI.</summary>
    internal sealed class ProviderSetupException : Exception
    {
        public ProviderSetupException(string message) : base(message) { }
    }
}
