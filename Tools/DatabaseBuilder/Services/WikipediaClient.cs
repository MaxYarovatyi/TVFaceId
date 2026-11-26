using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DatabaseBuilder.Models;

namespace DatabaseBuilder.Services
{
    public sealed class WikipediaClient(HttpClient http)
    {
        private const string Api = "https://en.wikipedia.org/w/api.php";

        public async Task<string?> FetchWithRetryAsync(string url, CancellationToken ct = default, int maxRetries = 4)
        {
            int delay = 2000;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent", "TVFaceId-DatabaseBuilder/1.0");

                using var resp = await http.SendAsync(req, ct);

                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadAsStringAsync(ct);

                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    int waitMs = delay;
                    if (resp.Headers.TryGetValues("Retry-After", out var values) &&
                    int.TryParse(values.FirstOrDefault(), out var retryAfter))
                        waitMs = retryAfter * 1000;

                    Console.WriteLine($"[429] Waiting {waitMs / 1000}s before retry ({attempt + 1}/{maxRetries})...");
                    await Task.Delay(waitMs, ct);
                    delay *= 2;
                    continue;
                }

                Console.WriteLine($"[WARN] HTTP {resp.StatusCode} for batch request");
                return null;
            }
            return null;
        }

        public async Task<Dictionary<string, string>> GetPhotoUrlsBatchAsync(IEnumerable<string> articleNames, CancellationToken ct = default)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var batches = articleNames.Distinct().Chunk(50);

            foreach (var batch in batches)
            {
                var titles = string.Join("|", batch.Select(Uri.EscapeDataString));
                var url = $"{Api}?action=query&titles={titles}"
                    + "&prop=pageimages|pageprops&ppprop=disambiguation&piprop=thumbnail&pithumbsize=500&redirects=1&format=json&formatversion=2";

                try
                {
                    var data = await FetchWithRetryAsync(url, ct);
                    if (data is null) continue;

                    using var doc = JsonDocument.Parse(data);
                    var query = doc.RootElement.GetProperty("query");
                    var pages = query.GetProperty("pages");
                    var alias = new List<(string From, string To)>();
                    foreach (var key in new[] { "normalized", "redirects" })
                        if (query.TryGetProperty(key, out var arr))
                            foreach (var m in arr.EnumerateArray())
                                alias.Add((m.GetProperty("from").GetString()!, m.GetProperty("to").GetString()!));

                    foreach (var page in pages.EnumerateArray())
                    {
                        if (!page.TryGetProperty("title", out var titleEl)) continue;
                        if (page.TryGetProperty("pageprops", out var pp) && pp.TryGetProperty("disambiguation", out _)) continue;
                        if (!page.TryGetProperty("thumbnail", out var thumb)) continue;
                        if (!thumb.TryGetProperty("source", out var src)) continue;

                        string title = titleEl.GetString() ?? string.Empty;
                        string photoUrl = src.GetString() ?? string.Empty;

                        if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(photoUrl))
                        {
                            var names = new HashSet<string> { title };
                            for (int pass = 0; pass < 3; pass++)
                                foreach (var (from, to) in alias)
                                    if (names.Contains(to)) names.Add(from);
                            foreach (var n in names) result[n.Replace(' ', '_')] = photoUrl;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Batch photo fetch failed: {ex.Message}");
                }

                await Task.Delay(500, ct);
            }

            return result;
        }


    }
}