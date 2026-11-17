using System.Text.Json;

namespace DatabaseBuilder.Services
{
    public sealed class CommonsClient(HttpClient http)
    {
        private const string Api = "https://commons.wikimedia.org/w/api.php";

        public async Task<List<string>> SearchImagesAsync(string personName, int limit = 4, CancellationToken ct = default)
        {
            var urls = new List<string>();
            string query = Uri.EscapeDataString($"\"{personName}\" portrait");
            string url = $"{Api}?action=query&generator=search&gsrsearch={query}&gsrnamespace=6&gsrlimit={limit}"
                       + "&prop=imageinfo&iiprop=url|mime&iiurlwidth=500&format=json&formatversion=2";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent", "TVFaceId-DatabaseBuilder/1.0");
                using var resp = await http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return urls;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (!doc.RootElement.TryGetProperty("query", out var qEl) ||
                    !qEl.TryGetProperty("pages", out var pages)) return urls;

                foreach (var page in pages.EnumerateArray())
                {
                    if (!page.TryGetProperty("imageinfo", out var infos)) continue;
                    foreach (var info in infos.EnumerateArray())
                    {
                        string? mime = info.TryGetProperty("mime", out var m) ? m.GetString() : null;
                        if (mime is not ("image/jpeg" or "image/png")) continue;
                        string? thumb = info.TryGetProperty("thumburl", out var t) ? t.GetString() : null;
                        if (!string.IsNullOrEmpty(thumb)) urls.Add(thumb);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Commons '{personName}': {ex.Message}");
            }
            return urls;
        }
    }
}