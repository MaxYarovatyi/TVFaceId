using System.Text.Json;
using System.Text.RegularExpressions;
using DatabaseBuilder.Models;

namespace DatabaseBuilder.Services
{
    public sealed class GovUkClient(HttpClient http)
    {
        private const string Api = "https://www.gov.uk/api/content/government/ministers";

        public async Task<List<PersonEntry>> FetchAsync(CancellationToken ct = default)
        {
            var result = new List<PersonEntry>();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, Api);
                req.Headers.Add("User-Agent", "TVFaceId-DatabaseBuilder/1.0");
                using var resp = await http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("links", out var links) ||
                    !links.TryGetProperty("ordered_cabinet_ministers", out var cabinet))
                    return result;

                foreach (var person in cabinet.EnumerateArray())
                {
                    var roles = CurrentRoles(person);
                    if (roles.Count == 0) continue;

                    string name = CleanName(person.GetProperty("title").GetString() ?? "");
                    if (name.Length == 0) continue;

                    var entry = new PersonEntry(
                        wikipediaArticle: name.Replace(' ', '_'),
                        name: name,
                        country: "United Kingdom",
                        role: string.Join(" / ", roles));

                    if (person.TryGetProperty("details", out var d) &&
                        d.TryGetProperty("image", out var img) &&
                        img.TryGetProperty("url", out var u) &&
                        u.ValueKind == JsonValueKind.String)
                        entry.ImageUrls.Add(u.GetString()!);

                    result.Add(entry);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] GOV.UK: {ex.Message}");
            }
            return result;
        }

        private static List<string> CurrentRoles(JsonElement person)
        {
            var labels = new List<string>();
            if (!person.TryGetProperty("links", out var l) ||
                !l.TryGetProperty("role_appointments", out var apps)) return labels;

            foreach (var app in apps.EnumerateArray())
            {
                if (!app.TryGetProperty("details", out var d) ||
                    !d.TryGetProperty("current", out var cur) ||
                    cur.ValueKind != JsonValueKind.True) continue;

                if (!app.TryGetProperty("links", out var al) ||
                    !al.TryGetProperty("role", out var roles)) continue;

                foreach (var role in roles.EnumerateArray())
                {
                    string title = role.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    string? label = Classify(title);
                    if (label is not null && !labels.Contains(label)) labels.Add(label);
                }
            }
            return labels;
        }

        private static string? Classify(string title)
        {
            if (title == "Prime Minister") return "Prime Minister";
            if (title.StartsWith("Secretary of State for Foreign")) return "Foreign Minister";
            if (title == "Secretary of State for Defence") return "Defence Minister";
            return null;
        }

        private static string CleanName(string t)
        {
            t = Regex.Replace(t, @"^(The\s+)?(Rt\s+Hon\s+)?((Sir|Dame|Dr)\s+)?", "", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\s+(MP|MSP|MS|MLA|CBE|OBE|MBE|KC|PC)\b.*$", "");
            return t.Trim();
        }
    }
}