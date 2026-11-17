using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DatabaseBuilder.Models;

namespace DatabaseBuilder.Services
{
    public sealed class PersonFolderWriter(HttpClient http, PhotoValidator validator)
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public async Task<int> WriteAllAsync(IEnumerable<PersonEntry> persons, string root,
            bool refresh, CancellationToken ct = default)
        {
            Directory.CreateDirectory(root);
            var seen = new HashSet<string>();
            int written = 0;

            foreach (var p in persons)
            {
                ct.ThrowIfCancellationRequested();
                string slug = Slug.From(p.Name);
                if (slug.Length == 0 || !seen.Add(slug)) continue;

                string dir = Path.Combine(root, slug);
                string metaPath = Path.Combine(dir, "person.json");

                if (File.Exists(metaPath) && !refresh)
                {
                    var node = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
                    if ((string?)node["role"] != p.Role)
                    {
                        node["role"] = p.Role;
                        File.WriteAllText(metaPath, node.ToJsonString(Json));
                        Console.WriteLine($" ~ role updated: {p.Name} -> {p.Role}");
                    }
                    continue;
                }

                var images = new List<byte[]>();
                foreach (var url in p.ImageUrls.Distinct())
                {
                    var bytes = await DownloadAsync(url, ct);
                    if (bytes is { Length: >= 8_000 } && Ext(bytes) is not null) images.Add(bytes);
                    await Task.Delay(300, ct);
                }

                var kept = validator.Filter(images, p.Name);
                if (kept.Count == 0) { Console.WriteLine($" ! no usable face: {p.Name}"); continue; }

                Directory.CreateDirectory(dir);
                foreach (var old in Directory.EnumerateFiles(dir, "*.*")
                             .Where(f => !f.EndsWith("person.json"))) File.Delete(old);

                for (int i = 0; i < kept.Count; i++)
                    File.WriteAllBytes(Path.Combine(dir, $"{i + 1}{Ext(kept[i].Bytes)}"), kept[i].Bytes);

                File.WriteAllText(metaPath, JsonSerializer.Serialize(new
                {
                    name = p.Name, country = p.Country, role = p.Role,
                    wikipedia = p.WikipediaArticle, verified = kept.Count >= 2
                }, Json));

                Console.WriteLine($" + {slug} ({kept.Count} photo(s){(kept.Count >= 2 ? "" : ", UNVERIFIED")})");
                written++;
            }

            foreach (var d in Directory.EnumerateDirectories(root))
                if (!seen.Contains(Path.GetFileName(d)))
                    Console.WriteLine($" ? not in roster any more: {Path.GetFileName(d)} (delete manually if outdated)");

            return written;
        }

        private async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent", "TVFaceId-DatabaseBuilder/1.0");
                using var resp = await http.SendAsync(req, ct);
                return resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync(ct) : null;
            }
            catch { return null; }
        }

        private static string? Ext(byte[] b) =>
            b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 ? ".jpg" :
            b.Length > 4 && b[0] == 0x89 && b[1] == 0x50 ? ".png" : null;
    }
}