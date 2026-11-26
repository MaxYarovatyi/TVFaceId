using Core.Database;
using Microsoft.AspNetCore.SignalR;
using Web.Hubs;

namespace Web.Services
{
    /// <summary>
    /// Loads the face database on a worker thread, so the web server, NDI and video start immediately.
    /// Until it finishes, recognition simply finds no matches. Clients are told when it is ready.
    /// </summary>
    public sealed class FaceDatabaseLoader(
        FaceDatabase db,
        FacesFolder folder,
        IHubContext<RecognitionHub> hub,
        ILogger<FaceDatabaseLoader> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            try
            {
                logger.LogInformation("Loading face database from {Path} in the background...", folder.Path);
                await Task.Run(() => db.Load(folder.Path), CancellationToken.None);
                await hub.Clients.All.SendAsync("DatabaseStatus",
                    new { loaded = true, count = db.PersonCount }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Face database failed to load");
            }
        }
    }
}
