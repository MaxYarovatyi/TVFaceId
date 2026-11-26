using Core.Database;
using Microsoft.AspNetCore.SignalR;
using Web.Services;

namespace Web.Hubs
{
    public class RecognitionHub(FaceDatabase db, NdiManager ndi) : Hub
    {
        // A page that connects while the database or NDI is still starting gets the current state at once.
        public override async Task OnConnectedAsync()
        {
            await Clients.Caller.SendAsync("DatabaseStatus", new { loaded = db.IsLoaded, count = db.PersonCount });
            await Clients.Caller.SendAsync("NdiStatus", new { status = ndi.Status.ToString().ToLower(), source = ndi.ConnectedSource });
            await base.OnConnectedAsync();
        }
    }
}
