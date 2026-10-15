using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Internal;

namespace Pomp.Features
{
    /// <summary>
    /// The pictures of agent roles, by role name: a shape, a color and optionally an image the user chose. A project role's live in
    /// <c>&lt;root&gt;/.pomp/local/avatars.json</c> (deleting <c>.pomp</c> resets them), every other role's (the user's, POMP's own) in
    /// <c>avatars.json</c> next to the user's teams. The agents state carries shape, color and an image revision only; the images go to
    /// the page on demand (<c>avatarImages</c>), so the state stays small however many roles have one.
    /// </summary>
    internal static class AgentAvatars
    {
        public static readonly string[] Shapes = { "circle", "rounded", "square", "hexagon" };
        /// <summary>The page sends a small square picture (96 px); a data URL longer than this is refused.</summary>
        public const int MaxImageChars = 64 * 1024;
        private const long MaxFileBytes = 64L * 1024 * 1024;
        private static readonly Regex Color = new Regex("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant);
        private static readonly Regex Image = new Regex("^data:image/(png|jpeg|webp);base64,[A-Za-z0-9+/]+={0,2}$", RegexOptions.CultureInvariant);
        private static readonly ConcurrentDictionary<string, object> Locks = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The checked picture to store: <c>{ shape, color?, image? }</c>; anything else is refused.</summary>
        public static JObject Normalize(JObject avatar)
        {
            var shape = (string?)avatar["shape"] ?? "circle";
            if (!Shapes.Contains(shape)) throw new InvalidOperationException(PompLanguage.Pick("모양이 올바르지 않아요.", "The shape is not valid."));
            var result = new JObject { ["shape"] = shape };
            if (avatar["color"] is JValue color && color.Type == JTokenType.String)
            {
                if (!Color.IsMatch((string)color!)) throw new InvalidOperationException(PompLanguage.Pick("색이 올바르지 않아요.", "The color is not valid."));
                result["color"] = ((string)color!).ToLowerInvariant();
            }
            if (avatar["image"] is JValue image && image.Type == JTokenType.String)
            {
                var data = (string)image!;
                if (data.Length > MaxImageChars) throw new InvalidOperationException(PompLanguage.Pick("이미지가 너무 커요.", "The image is too large."));
                if (!Image.IsMatch(data)) throw new InvalidOperationException(PompLanguage.Pick("이미지 형식이 올바르지 않아요.", "The image format is not valid."));
                result["image"] = data;
            }
            return result;
        }

        /// <summary>The pictures of one store by role name; an unreadable or corrupt store reads as none.</summary>
        public static JObject Read(string? file)
        {
            if (file == null) return new JObject();
            try
            {
                if (!File.Exists(file)) return new JObject();
                CheckpointStore.CheckDirectPath(file);
                if (new FileInfo(file).Length > MaxFileBytes) return new JObject();
                using var reader = new JsonTextReader(new StreamReader(file, Encoding.UTF8, true)) { MaxDepth = 8, DateParseHandling = DateParseHandling.None };
                var data = JObject.Load(reader);
                if (data["version"]?.Type != JTokenType.Integer || (int)data["version"]! != 1 || !(data["roles"] is JObject roles)) return new JObject();
                var result = new JObject();
                foreach (var entry in roles.Properties())
                {
                    if (!AgentRoster.IsValidName(entry.Name) || !(entry.Value is JObject value)) continue;
                    try
                    {
                        var avatar = Normalize(value);
                        avatar["rev"] = value["rev"]?.Type == JTokenType.Integer ? (long)value["rev"]! : 1;
                        result[entry.Name] = avatar;
                    }
                    catch (InvalidOperationException) { }
                }
                return result;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is InvalidDataException)
            {
                return new JObject();
            }
        }

        /// <summary>
        /// Sets (or with null removes) the picture of <paramref name="name"/>; each change raises its revision. A picture without an
        /// <c>image</c> field keeps the stored image (a shape or color change); <c>image: null</c> removes it.
        /// </summary>
        public static void Write(string file, string name, JObject? avatar)
        {
            lock (Locks.GetOrAdd(Path.GetFullPath(file), _ => new object()))
            {
                var roles = Read(file);
                var rev = roles[name]?["rev"]?.Type == JTokenType.Integer ? (long)roles[name]!["rev"]! : 0;
                if (avatar == null) roles.Remove(name);
                else
                {
                    var stored = Normalize(avatar);
                    if (avatar["image"] == null && roles[name]?["image"] is JToken kept) stored["image"] = kept.DeepClone();
                    stored["rev"] = rev + 1;
                    roles[name] = stored;
                }
                var directory = Path.GetDirectoryName(file)!;
                CheckpointStore.CheckDirectPath(directory);
                Directory.CreateDirectory(directory);
                AtomicFile.WriteAllText(file, new JObject { ["version"] = 1, ["roles"] = roles }.ToString(Formatting.None));
            }
        }

        /// <summary>What the agents state says of a picture: shape, color and, when it has an image, its revision.</summary>
        public static JObject? Summary(JObject? avatar)
        {
            if (avatar == null) return null;
            var summary = new JObject { ["shape"] = avatar["shape"]!.DeepClone() };
            if (avatar["color"] != null) summary["color"] = avatar["color"]!.DeepClone();
            if (avatar["image"] != null) summary["imageRev"] = avatar["rev"]!.DeepClone();
            return summary;
        }
    }
}
