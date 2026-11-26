using DatabaseBuilder.Models;
using DatabaseBuilder.Services;


const string DefaultOutput = "faces";

string output = GetArg(args, "--output", DefaultOutput);
bool daemon = args.Contains("--daemon");

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
var scraper = new WikipediaListScraper(http);
var wikipedia = new WikipediaClient(http);
var commons = new CommonsClient(http);
string models = GetArg(args, "--models", "Services");
bool refresh = args.Contains("--refresh");
float cluster = float.Parse(GetArg(args, "--cluster", "0.5"), System.Globalization.CultureInfo.InvariantCulture);

var gov = new GovUkClient(http);
using var validator = new PhotoValidator(
    Path.Combine(models, "det_500m.onnx"), Path.Combine(models, "w600k_mbf.onnx"), cluster);
var folderWriter = new PersonFolderWriter(http, validator);

if (daemon)
{
    Console.WriteLine("Daemon mode: running every 30 days. Ctrl+C to stop");
    var timer = new PeriodicTimer(TimeSpan.FromDays(30));
    do
    {
        await RunAsync();
    }
    while (await timer.WaitForNextTickAsync());
}
else
{
    await RunAsync();
}


async Task RunAsync()
{
    Console.WriteLine($"\n[{DateTime.Now:dd-MM-yyyy HH:mm}] Starting database update");

    Console.WriteLine("\n --- Wikipedia lists ---");
    var persons = await scraper.ScrapeAllAsync();
    Console.WriteLine($" Found {persons.Count} person(s) from Wikipedia lists");


    var uk = await gov.FetchAsync();
    Console.WriteLine($" GOV.UK: {uk.Count} person(s)");
    persons = MergeOfficial(uk, persons);

    Console.WriteLine("\n --- Fetching photos ---");
    var articleNames = persons.Select(p => p.WikipediaArticle).ToList();
    var photoMap = await wikipedia.GetPhotoUrlsBatchAsync(articleNames: articleNames);

    foreach (var person in persons)
    {
        if (photoMap.TryGetValue(person.WikipediaArticle, out var url))
            person.ImageUrls.Add(url);

        bool known = !refresh && File.Exists(Path.Combine(output, Slug.From(person.Name), "person.json"));
        if (known) continue;
        person.ImageUrls.AddRange(await commons.SearchImagesAsync(person.Name));
        await Task.Delay(500);
    }

    var withPhoto = persons.Where(p => p.ImageUrls.Count > 0).ToList();
    Console.WriteLine($"Photos found: {withPhoto.Count}/{persons.Count}");

    Console.WriteLine("\n --- Downloading images ---");
    int saved = await folderWriter.WriteAllAsync(withPhoto, output, refresh);

    Console.WriteLine($"\n + Done. Saved {saved} new image(s) to '{output}'");
}

static List<PersonEntry> MergeOfficial(List<PersonEntry> official, List<PersonEntry> others)
{
    var known = official.Select(p => Slug.From(p.Name)).ToHashSet();
    var merged = new List<PersonEntry>(official);
    foreach (var o in others)
        if (known.Add(Slug.From(o.Name))) merged.Add(o);
    return merged;
}

static string GetArg(string[] args, string key, string def)
{
    int i = Array.IndexOf(args, key);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
}