using Core.Database;
using Web.Hubs;
using Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi

builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:5000");
//builder.WebHost.UseContentRoot(AppContext.BaseDirectory);
builder.Services.AddSignalR();
builder.Services.AddControllers();

builder.Services.AddSingleton(sp => new FaceDatabase(
    new Core.Inference.FaceDetector("models/det_500m.onnx"),
    new Core.Inference.FaceRecognizer("models/w600k_mbf.onnx"),
    sp.GetRequiredService<ILogger<FaceDatabase>>()
));

builder.Services.AddSingleton<NdiManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NdiManager>());

builder.Services.AddHostedService<RecognitionWorker>();


var app = builder.Build();




app.UseDefaultFiles();
app.UseStaticFiles();

var facesPath = builder.Configuration["Faces:Folder"] ?? "faces";
Directory.CreateDirectory(facesPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        Path.GetFullPath(facesPath)),
    RequestPath = "/faces"
});

app.MapControllers();
app.MapHub<RecognitionHub>("/hub/recognition");

Console.WriteLine("Loading face database...");
var db = app.Services.GetRequiredService<FaceDatabase>();
db.Load(facesPath);
Console.WriteLine($"Database ready");

await app.StartAsync();

var ndiManager = app.Services.GetRequiredService<NdiManager>();
var ndiConfig = app.Configuration["Ndi:SourceName"];

if (!string.IsNullOrEmpty(ndiConfig))
{
    Console.WriteLine($"[NDI] Connecting to configured source: {ndiConfig}");
    await ndiManager.ConnectAsync(ndiConfig);
}
else
{
    await SelectNdiSourceAsync(ndiManager);
}

Console.WriteLine("\n[S] – change NDI source | [Ctrl+C] – exit\n");

_ = Task.Run(async () =>
{
    while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        var input = Console.ReadLine();
        if (input?.Trim().Equals("s", StringComparison.OrdinalIgnoreCase) == true)
        {
            Console.WriteLine("\n[NDI] Switching source...");
            await ndiManager.DisconnectAsync();
            await SelectNdiSourceAsync(ndiManager);
        }
    }
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    Console.WriteLine("Exiting...");
    Environment.Exit(0);
});

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    app.StopAsync();
};

await app.WaitForShutdownAsync();




static async Task SelectNdiSourceAsync(NdiManager ndi)
{
    while (true)
    {
        Console.WriteLine("\n[NDI] Scanning for sources...");
        var sources = ndi.DiscoverSources();

        if (sources.Count == 0)
        {
            Console.WriteLine("[NDI] No sources found. Retry? (y/n):");
            if (Console.ReadLine()?.Trim().ToLower() != "y") break;
            continue;
        }

        Console.WriteLine("[NDI] Available sources:");
        for (int i = 0; i < sources.Count; i++)
            Console.WriteLine($"[{i + 1}] {sources[i]}");

        Console.WriteLine("Select source number (or 0 to skip): ");
        if (int.TryParse(Console.ReadLine(), out int choice))
        {
            if (choice == 0) break;
            if (choice >= 1 && choice <= sources.Count)
            {
                await ndi.ConnectAsync(sources[choice - 1]);
                break;

            }
        }
        Console.WriteLine("Invalid choice.");
    }
}