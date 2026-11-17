using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DatabaseBuilder.Models;
using HtmlAgilityPack;
using System.Text.RegularExpressions;

namespace DatabaseBuilder.Services
{
    public sealed class WikipediaListScrapper(HttpClient http)
    {
        private static readonly (string Page, string Role)[] Sources =
        [
             ("List_of_current_heads_of_state_and_government", "Head of State / Government"),
        ("List_of_current_foreign_ministers", "Foreign Minister"),
        ("List_of_current_defence_ministers", "Defence Minister"),
        ];


        public async Task<List<PersonEntry>> ScrapeAllAsync(CancellationToken ct = default)
        {
            var results = new List<PersonEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (page, role) in Sources)
            {
                Console.WriteLine($"Scraping: {page}");
                try
                {
                    var entries = await ScrapePageAsync(page, role, ct);
                    Console.WriteLine($"{entries.Count} entries");

                    foreach (var e in entries)
                    {
                        string key = e.WikipediaArticle;
                        if (seen.Add(key))
                            results.Add(e);
                        else
                        {
                            var existing = results.First(r => r.WikipediaArticle == key);
                            existing.AppendRole(e.Role);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] {ex.Message}");
                }

                await Task.Delay(800, ct);
            }
            return results;
        }

        private async Task<List<PersonEntry>> ScrapePageAsync(string pageTitle, string role, CancellationToken ct)
        {
            string html = await FetchHtmlAsync(pageTitle, ct);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var results = new List<PersonEntry>();
            var tables = doc.DocumentNode.SelectNodes("//table[contains(@class,'wikitable')]");
            if (tables is null) { Console.WriteLine("    no wikitables found"); return results; }

            bool heads = pageTitle.Contains("heads_of_state", StringComparison.OrdinalIgnoreCase);
            var stats = new Dictionary<string, int>();
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var table in tables)
            {
                var rows = table.SelectNodes(".//tr")?.ToList();
                var headerRow = rows?.FirstOrDefault(r => r.SelectNodes("./th") is not null && r.SelectNodes("./td") is null);
                if (rows is null || headerRow is null) { Bump(stats, "table-skipped"); continue; }

                var headers = headerRow.SelectNodes("./th").Select(h => CleanText(h).ToLowerInvariant()).ToList();
                int countryCol = headers.FindIndex(h => h.Contains("state") || h.Contains("country") || h.Contains("entity"));
                int nameCol = headers.FindIndex(h => h.Contains("minister"));
                int hosCol = headers.FindIndex(h => h.Contains("head of state") || h.Contains("head of entity"));
                int hogCol = headers.FindIndex(h => h.Contains("head of government"));

                int[] personCols = heads ? new[] { hosCol, hogCol } : new[] { nameCol };
                if (countryCol < 0 || personCols.All(c => c < 0)) { Bump(stats, "table-skipped"); continue; }

                foreach (var row in rows)
                {
                    if (row == headerRow) continue;
                    var cells = row.SelectNodes("./th|./td")?.ToList();   // row header may be <th>, so take both
                    if (cells is null || countryCol >= cells.Count) { Bump(stats, "short-row"); continue; }

                    string country = CleanText(cells[countryCol]);
                    if (!RelevantCountries.Contains(country)) { Bump(stats, "other-country"); continue; }

                    foreach (int col in personCols.Where(c => c >= 0 && c < cells.Count))
                    {
                        if (heads)
                        {
                            foreach (var (title, name, article) in ParseTitleNameCell(cells[col], stats))
                            {
                                results.Add(new PersonEntry(article, name, country, title));
                                found.Add(country); Bump(stats, "kept");
                            }
                        }
                        else
                        {
                            var link = cells[col].SelectNodes(".//a[@href]")?.FirstOrDefault(IsArticleLink);
                            if (link is null)
                            {
                                string text = Norm(Regex.Replace(CleanText(cells[col]), @"\(.*?\)", ""));
                                Dump(role, cells[col]);
                                if (!LooksLikeName(text)) { Bump(stats, "no-person-link"); continue; }
                                Bump(stats, "name-from-text");
                                results.Add(new PersonEntry(text.Replace(' ', '_'), text, country, role));
                            }
                            else
                            {
                                results.Add(new PersonEntry(ArticleOf(link), Norm(CleanText(link)), country, role));
                            }
                            found.Add(country); Bump(stats, "kept");
                            results.Add(new PersonEntry(ArticleOf(link), Norm(CleanText(link)), country, role));

                        }
                    }
                }
            }

            Console.WriteLine("    " + string.Join(", ", stats.Select(kv => $"{kv.Key}={kv.Value}")));
            var missing = Expected.Where(c => !found.Contains(c)).ToList();
            if (missing.Count > 0) Console.WriteLine("    no entry for: " + string.Join(", ", missing));
            return results;
        }
        private async Task<string> FetchHtmlAsync(string pageTitle, CancellationToken ct)
        {
            var url = $"https://en.wikipedia.org/wiki/{Uri.EscapeDataString(pageTitle)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", "MinisterRecognitionBot/1.0");
            req.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }

        private static string CleanText(HtmlNode node) =>
            System.Net.WebUtility.HtmlDecode(node.InnerText)
            .Replace("\n", " ")
            .Trim()
            .Split('[')[0]
            .Trim();

        private static readonly string[] SkipTitles = ["governor", "representative", "co-prince", "vacant"];

        private static void Bump(Dictionary<string, int> d, string key) => d[key] = d.GetValueOrDefault(key) + 1;

        private static string Norm(string s) => Regex.Replace(s.Replace('\u00A0', ' '), @"\s+", " ").Trim();

        private static string? ArticleTarget(HtmlNode a)
        {
            string href = a.GetAttributeValue("href", "");
            foreach (var prefix in new[] { "https://en.wikipedia.org/wiki/", "//en.wikipedia.org/wiki/", "/wiki/", "./" })
            {
                if (!href.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string t = Uri.UnescapeDataString(href[prefix.Length..]).Split('#')[0].Split('?')[0].Trim();
                return t.Length == 0 || t.Contains(':') || t.StartsWith("List_of") ? null : t;
            }
            return null;
        }

        private static bool IsArticleLink(HtmlNode a) => ArticleTarget(a) is not null && CleanText(a).Length > 0;

        private static string ArticleOf(HtmlNode a) => ArticleTarget(a)!;

        // cells look like "Title – Name", several entries stacked in one cell
        private static List<(string Title, string Name, string Article)> ParseTitleNameCell(HtmlNode cell, Dictionary<string, int> stats)
        {
            var found = new List<(string Title, string Name, string Article)>();
            string html = Regex.Replace(cell.InnerHtml, @"<br\s*/?>|</li>|</div>|</p>", "\n", RegexOptions.IgnoreCase);
            string text = HtmlEntity.DeEntitize(Regex.Replace(html, "<[^>]+>", ""));
            var links = cell.SelectNodes(".//a[@href]")?.Where(IsArticleLink).ToList() ?? [];

            foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int dash = raw.IndexOfAny(['–', '—']);
                if (dash < 0) { Bump(stats, "no-dash"); continue; }

                string title = Norm(raw[..dash]);
                string name = Norm(Regex.Replace(raw[(dash + 1)..], @"\[.*?\]|\(.*?\)", ""));
                if (name.Length < 4 || SkipTitles.Any(s => title.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;

                var link = links.FirstOrDefault(a => Norm(CleanText(a)).Equals(name, StringComparison.OrdinalIgnoreCase));
                string article;
                if (link is not null) article = ArticleOf(link);
                else
                {
                    Bump(stats, "name-from-text");
                    Dump("heads", cell);
                    article = name.Replace(' ', '_');
                }
                found.Add((title, name, article));
            }
            return found;
        }

        private static readonly HashSet<string> RelevantCountries = new(StringComparer.OrdinalIgnoreCase)
        {
            "Austria","Belgium","Bulgaria","Croatia","Cyprus","Czech Republic","Denmark",
    "Estonia","Finland","France","Germany","Greece","Hungary","Ireland","Italy",
    "Latvia","Lithuania","Luxembourg","Malta","Netherlands","Poland","Portugal",
    "Romania","Slovakia","Slovenia","Spain","Sweden",
    // Ключові союзники та партнери
    "United States","United Kingdom","Ukraine","Canada","Turkey","Norway",
    "Iceland","North Macedonia","Montenegro","Albania","United Kingdom",
    "NATO","EU","European Union"
        };

        private static readonly string[] Expected =
    RelevantCountries.Except(["NATO", "EU", "European Union"], StringComparer.OrdinalIgnoreCase).ToArray();

        private static int _dumps;
        private static void Dump(string tag, HtmlNode cell)
        {
            if (_dumps++ >= 6) return;
            string html = cell.OuterHtml.Replace("\n", " ");
            Console.WriteLine($"    DEBUG [{tag}] {(html.Length > 500 ? html[..500] + "..." : html)}");
        }

        private static bool LooksLikeName(string s) =>
            s.Length is >= 4 and <= 60 && s.Any(char.IsLetter) && !s.Any(char.IsDigit)
            && !s.Contains("vacant", StringComparison.OrdinalIgnoreCase)
            && !s.Contains("n/a", StringComparison.OrdinalIgnoreCase);
    }

}