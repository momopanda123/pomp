using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Adapters;
using Pomp.Hosting;
using Pomp.Internal;

namespace Pomp.Features
{
    /// <summary>Bounded local vector cache (<c>.pomp/local/vectors.bin</c> of the workspace) and cancellable background embedding. Never substitutes lexical hashes for learned vectors.</summary>
    public sealed class LocalVectorIndex : IDisposable
    {
        private readonly string dataDirectory;
        private readonly Func<string, string, ILocalEmbeddingClient> createClient;
        private readonly object gate = new object();
        private CancellationTokenSource? workerCancellation;
        private Task worker = Task.CompletedTask;
        private Dictionary<string, float[]> vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        private string target = "", endpoint = "", model = "", state = "disabled", message = "";
        private int total, ready;
        public LocalVectorIndex(string dataDirectory, Func<string, string, ILocalEmbeddingClient>? createClient = null)
        { this.dataDirectory = dataDirectory; this.createClient = createClient ?? ((url, name) => new LoopbackEmbeddingClient(url, name)); }
        public event EventHandler? Changed;
        public string State { get { lock (gate) return state; } }
        public string Message { get { lock (gate) return message; } }
        public int Ready { get { lock (gate) return ready; } }
        public int Total { get { lock (gate) return total; } }
        public void Disable(string? reason = null)
        {
            lock (gate) { CancelWorker(); target = ""; vectors = new Dictionary<string, float[]>(); state = "disabled"; message = reason ?? PompLanguage.Pick("로컬 임베딩이 꺼져 있어요.", "Local embeddings are off."); ready = total = 0; }
            Raise();
        }
        public void Ensure(string root, CodeIndexSnapshot snapshot, string url, string modelName, bool rebuild = false)
        {
            url = LoopbackEmbeddingClient.ValidateEndpoint(url).AbsoluteUri;
            if (string.IsNullOrWhiteSpace(modelName) || modelName.Length > 200) throw new ArgumentException(PompLanguage.Pick("로컬 임베딩 모델 이름이 비어 있거나 너무 길어요.", "The local embedding model name is empty or too long."));
            var identity = ContentHash.Compute(root + "\n" + snapshot.Identity + "\n" + url + "\n" + modelName);
            lock (gate)
            {
                if (!rebuild && target == identity) return;
                CancelWorker();
                var oldCancellation = workerCancellation;
                var cancellation = new CancellationTokenSource(); var generationToken = cancellation.Token; workerCancellation = cancellation;
                target = identity; endpoint = url; model = modelName; state = "building"; message = PompLanguage.Pick("로컬 벡터를 만드는 중이에요.", "Building local vectors.");
                var chunks = snapshot.Chunks.Take(2000).ToArray(); total = chunks.Length; ready = 0; vectors = new Dictionary<string, float[]>();
                var previous = worker;
                worker = Task.Run(async () => { try { await previous.ConfigureAwait(false); } catch { } finally { oldCancellation?.Dispose(); } await BuildAsync(root, chunks, identity, url, modelName, rebuild, generationToken).ConfigureAwait(false); });
                worker.Observe();
            }
            Raise();
        }
        private async Task BuildAsync(string root, IReadOnlyList<CodeChunk> chunks, string identity, string url, string modelName, bool rebuild, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var signature = ContentHash.Compute(url + "\n" + modelName);
                var file = ProjectData.File(root, "vectors.bin", Path.Combine(dataDirectory, "vectors", ContentHash.Compute(root) + ".bin"));
                Dictionary<string, float[]> cached;
                try { cached = rebuild ? new Dictionary<string, float[]>() : ReadCache(file, signature); }
                catch { cached = new Dictionary<string, float[]>(); }
                var items = chunks.Select(chunk => new { Chunk = chunk, Text = EmbeddingText(chunk), Key = Key(chunk) }).GroupBy(x => x.Key).Select(g => g.First()).ToArray();
                var result = new Dictionary<string, float[]>(StringComparer.Ordinal);
                using var client = createClient(url, modelName);
                int dimensions = 0;
                for (var offset = 0; offset < items.Length; offset += 16)
                {
                    token.ThrowIfCancellationRequested();
                    var batch = items.Skip(offset).Take(16).ToArray();
                    var missing = batch.Where(item => !cached.ContainsKey(item.Key)).ToArray();
                    IReadOnlyList<float[]> embedded = Array.Empty<float[]>();
                    if (missing.Length > 0) embedded = await client.EmbedAsync(missing.Select(item => item.Text).ToArray(), token).ConfigureAwait(false);
                    if (embedded.Count != missing.Length) throw new InvalidOperationException(PompLanguage.Pick("로컬 임베딩 응답이 요청과 맞지 않아요.", "The local embedding service returned an inconsistent batch."));
                    for (var i = 0; i < missing.Length; i++) cached[missing[i].Key] = embedded[i];
                    foreach (var item in batch)
                    {
                        var vector = (float[])cached[item.Key].Clone();
                        if (dimensions != 0 && vector.Length != dimensions) throw new InvalidOperationException(PompLanguage.Pick("임베딩 모델의 차원이 바뀌었어요.", "The embedding model dimensions changed."));
                        dimensions = vector.Length; LoopbackEmbeddingClient.Normalize(vector); result[item.Key] = vector;
                    }
                    lock (gate) { if (target != identity) return; ready = result.Count; }
                    Raise();
                }
                token.ThrowIfCancellationRequested();
                var persistWarning = "";
                try { WriteCache(file, signature, result, token); } catch (Exception ex) when (!(ex is OperationCanceledException)) { persistWarning = PompLanguage.Pick(" · 캐시 저장 실패", " · Cache save failed"); }
                lock (gate)
                {
                    if (target != identity) return; vectors = result; state = "ready";
                    message = PompLanguage.Pick("로컬 검색 준비됨 · 청크 " + result.Count + "개", "Local search ready · " + result.Count + " chunks") + persistWarning;
                }
            }
            catch (OperationCanceledException) { lock (gate) if (target == identity) { state = "cancelled"; message = PompLanguage.Pick("로컬 임베딩 작업을 취소했어요.", "Local embedding indexing was cancelled."); } }
            catch (Exception ex) { lock (gate) if (target == identity) { state = "failed"; message = PompLanguage.Pick("로컬 임베딩을 쓸 수 없어요: ", "Local embeddings unavailable: ") + ex.Message; } }
            finally { Raise(); }
        }
        public async Task<IReadOnlyList<CodeSearchHit>> SearchAsync(string query, IReadOnlyList<CodeChunk> chunks, IReadOnlyList<CodeSearchHit> lexical, int limit, CancellationToken token)
        {
            Dictionary<string, float[]> current; string url, modelName, expected; CancellationTokenSource queryCancellation;
            lock (gate) { if (state != "ready" || vectors.Count == 0) return lexical; current = vectors; url = endpoint; modelName = model; expected = target; queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, workerCancellation!.Token); }
            using var queryLifetime = queryCancellation;
            queryLifetime.Token.ThrowIfCancellationRequested();
            using var client = createClient(url, modelName);
            var embedded = await client.EmbedAsync(new[] { query }, queryLifetime.Token).ConfigureAwait(false); var queryVector = (float[])embedded.Single().Clone(); LoopbackEmbeddingClient.Normalize(queryVector);
            lock (gate) if (target != expected) return lexical;
            var scores = new Dictionary<string, CodeSearchHit>(StringComparer.Ordinal);
            string HitKey(CodeChunk chunk) => chunk.Path + "|" + chunk.StartLine + "|" + chunk.EndLine + "|" + chunk.Hash;
            for (var i = 0; i < lexical.Count; i++) scores[HitKey(lexical[i].Chunk)] = new CodeSearchHit(lexical[i].Chunk, 1.0 / (20 + i));
            var nearest = new List<CodeSearchHit>();
            foreach (var chunk in chunks)
            {
                token.ThrowIfCancellationRequested();
                if (!current.TryGetValue(Key(chunk), out var vector)) continue;
                if (vector.Length != queryVector.Length) throw new InvalidOperationException(PompLanguage.Pick("검색어 임베딩의 차원이 캐시와 달라요.", "The query embedding dimensions differ from the cache."));
                double score = 0; for (var i = 0; i < vector.Length; i++) score += (double)vector[i] * queryVector[i];
                nearest.Add(new CodeSearchHit(chunk, score));
            }
            var ranked = nearest.OrderByDescending(hit => hit.Score).Take(40).ToArray();
            for (var i = 0; i < ranked.Length; i++) { var key = HitKey(ranked[i].Chunk); scores.TryGetValue(key, out var prior); scores[key] = new CodeSearchHit(ranked[i].Chunk, (prior?.Score ?? 0) + 1.0 / (20 + i)); }
            var output = new List<CodeSearchHit>();
            foreach (var hit in scores.Values.OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Chunk.Path, StringComparer.OrdinalIgnoreCase).ThenBy(hit => hit.Chunk.StartLine))
            {
                if (output.Any(prior => prior.Chunk.Path == hit.Chunk.Path && prior.Chunk.StartLine <= hit.Chunk.EndLine && hit.Chunk.StartLine <= prior.Chunk.EndLine)) continue;
                output.Add(hit); if (output.Count >= Math.Max(1, Math.Min(20, limit))) break;
            }
            return output;
        }
        private void CancelWorker() { try { workerCancellation?.Cancel(); } catch (ObjectDisposedException) { } }
        public void Cancel() { lock (gate) if (state == "building") CancelWorker(); }
        public Task WaitAsync() { lock (gate) return worker; }
        public void Dispose() { Disable("Index disposed."); }
        private void Raise() { try { Changed?.Invoke(this, EventArgs.Empty); } catch { } }
        private static string EmbeddingText(CodeChunk chunk)
        { var text = Path.GetFileName(chunk.Path) + "\n" + chunk.Symbol + "\n" + chunk.Text; return text.Length <= 4096 ? text : text.Substring(0, 4096); }
        private static string Key(CodeChunk chunk) => ContentHash.Compute(EmbeddingText(chunk));
        private static Dictionary<string, float[]> ReadCache(string file, string signature)
        {
            var result = new Dictionary<string, float[]>(StringComparer.Ordinal);
            if (!File.Exists(file)) return result;
            CheckpointStore.CheckDirectPath(file);
            using var stream = File.OpenRead(file); if (stream.Length > 34 * 1024 * 1024) return result;
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadInt32() != 0x504F4D50 || reader.ReadInt32() != 1 || reader.ReadString() != signature) return result;
            var count = reader.ReadInt32(); if (count < 0 || count > 2000) throw new InvalidDataException("Vector cache count exceeds its budget.");
            for (var row = 0; row < count; row++)
            {
                var key = reader.ReadString(); var dimensions = reader.ReadInt32();
                if (key.Length != 64 || dimensions < 1 || dimensions > 4096) throw new InvalidDataException("Invalid vector cache row.");
                var vector = new float[dimensions]; for (var i = 0; i < dimensions; i++) vector[i] = reader.ReadSingle();
                LoopbackEmbeddingClient.Normalize(vector); result.Add(key, vector);
            }
            return result;
        }
        private static void WriteCache(string file, string signature, IReadOnlyDictionary<string, float[]> vectors, CancellationToken token)
        {
            CheckpointStore.CheckDirectPath(file); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temp))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(0x504F4D50); writer.Write(1); writer.Write(signature); writer.Write(vectors.Count);
                    foreach (var row in vectors) { token.ThrowIfCancellationRequested(); writer.Write(row.Key); writer.Write(row.Value.Length); foreach (var value in row.Value) writer.Write(value); }
                }
                token.ThrowIfCancellationRequested(); if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp, file);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
