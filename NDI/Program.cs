using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NDI;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddSingleton<NdiReceiver>();
        services.AddHostedService<MonitorWorker>();
    })
    .Build();

await host.RunAsync();