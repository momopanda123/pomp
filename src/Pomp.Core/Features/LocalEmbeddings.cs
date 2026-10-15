using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;

namespace Pomp.Features
{
    public sealed class LocalEmbeddingConfiguration
    {
        public LocalEmbeddingConfiguration(bool enabled, string endpoint, string model) { Enabled = enabled; Endpoint = endpoint; Model = model; }
        public bool Enabled { get; }
        public string Endpoint { get; }
        public string Model { get; }
        public string Key => Enabled ? Endpoint + "\n" + Model : "";
    }
    public interface ILocalEmbeddingSettings { LocalEmbeddingConfiguration EmbeddingConfiguration { get; } }
    public interface ILocalEmbeddingClient : IDisposable
    {
        Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> input, CancellationToken token);
    }
    /// <summary>Opt-in OpenAI-compatible embeddings on a literal loopback address. No credentials, redirects or proxy routing.</summary>
    public sealed class LoopbackEmbeddingClient : ILocalEmbeddingClient
    {
        private readonly HttpClient client;
        private readonly Uri endpoint;
        private readonly string model;
        public static Uri ValidateEndpoint(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") ||
                !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new ArgumentException(PompLanguage.Pick("로컬 임베딩 주소가 올바르지 않아요.", "The local embedding endpoint is invalid."));
            return uri;
        }
        public LoopbackEmbeddingClient(string endpoint, string model, HttpMessageHandler? handler = null)
        {
            this.endpoint = ValidateEndpoint(endpoint);
            if (string.IsNullOrWhiteSpace(model) || model.Length > 200 || model.Any(char.IsControl)) throw new ArgumentException(PompLanguage.Pick("로컬 임베딩 모델 이름이 비어 있거나 너무 길어요.", "The local embedding model name is empty or too long."));
            this.model = model;
            client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };
        }
        public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> input, CancellationToken token)
        {
            if (input.Count == 0 || input.Count > 32 || input.Any(text => text == null || text.Length > 4096)) throw new ArgumentException("The embedding batch is invalid.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var requestToken = timeout.Token; requestToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(new JObject { ["model"] = model, ["input"] = new JArray(input), ["encoding_format"] = "float" }.ToString(), Encoding.UTF8, "application/json") };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
            using var abortRead = requestToken.Register(() => response.Dispose());
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Local embedding server returned HTTP " + (int)response.StatusCode + ". No external fallback was used.");
            if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new InvalidOperationException("Embedding response exceeded its byte budget.");
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var memory = new MemoryStream(); var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, 0, buffer.Length, requestToken).ConfigureAwait(false); if (count == 0) break;
                if (memory.Length + count > 8 * 1024 * 1024) throw new InvalidOperationException("Embedding response exceeded its byte budget.");
                memory.Write(buffer, 0, count);
            }
            memory.Position = 0;
            using var textReader = new StreamReader(memory, Encoding.UTF8);
            using var jsonReader = new Newtonsoft.Json.JsonTextReader(textReader) { MaxDepth = 12 };
            var data = JObject.Load(jsonReader)["data"] as JArray;
            if (data == null || data.Count != input.Count) throw new InvalidOperationException("Embedding response count does not match the request.");
            var vectors = new float[input.Count][]; int dimensions = 0;
            foreach (var item in data)
            {
                if (item["index"]?.Type != JTokenType.Integer || !(item["embedding"] is JArray values)) throw new InvalidOperationException("Invalid embedding response schema.");
                var index = (int)item["index"]!;
                if (index < 0 || index >= input.Count || vectors[index] != null || values.Count == 0 || values.Count > 4096) throw new InvalidOperationException("Invalid embedding index or dimensions.");
                if (dimensions != 0 && dimensions != values.Count) throw new InvalidOperationException("Embedding dimensions changed within a batch.");
                dimensions = values.Count;
                var vector = values.Select(value => value.Type == JTokenType.Float || value.Type == JTokenType.Integer ? value.Value<float>() : throw new InvalidOperationException("Embedding components must be numbers.")).ToArray();
                Normalize(vector); vectors[index] = vector;
            }
            return vectors;
        }
        public static void Normalize(float[] vector)
        {
            if (vector.Length == 0 || vector.Length > 4096 || vector.Any(x => float.IsNaN(x) || float.IsInfinity(x))) throw new InvalidOperationException("Invalid embedding components.");
            var norm = Math.Sqrt(vector.Sum(value => (double)value * value));
            if (norm <= 1e-20 || double.IsInfinity(norm)) throw new InvalidOperationException("Embedding has no finite nonzero length.");
            for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
        }
        public void Dispose() => client.Dispose();
    }
}
