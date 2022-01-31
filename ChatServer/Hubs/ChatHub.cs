using Microsoft.AspNetCore.SignalR;

namespace ChatServer
{
    public class ChatHub : Hub
    {
        private Dictionary<string, ConnectionInfo> dictConnections = new();
        private record ConnectionInfo(string ConnectionId, string AuthId, string? PubKey);

        public override async Task OnConnectedAsync()
        {
            var ci = new ConnectionInfo(this.Context.ConnectionId, GetRandomAuthId(), null);
            dictConnections.TryAdd(this.Context.ConnectionId, ci);
            string GetRandomAuthId() => new Random((int)DateTime.Now.Ticks).NextInt64(int.MaxValue, long.MaxValue).ToString();
            await this.Clients.Client(this.Context.ConnectionId).SendAsync("onAuth", ci.AuthId);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            dictConnections.Remove(this.Context.ConnectionId);
            await  base.OnDisconnectedAsync(exception);
        }

        public async Task Auth(string pubKey, string signature)
        {
            //if (signature == null) return;
            // TODO: check signature
            if (dictConnections.TryGetValue(this.Context.ConnectionId, out var ci))
            {
                dictConnections[this.Context.ConnectionId] = ci with { PubKey = pubKey };
                await this.Groups.AddToGroupAsync(this.Context.ConnectionId, pubKey);

                await this.Clients.Client(this.Context.ConnectionId).SendAsync("onAuthSuccess");
                return;
            }

            await this.Clients.Client(this.Context.ConnectionId).SendAsync("onAuthFailure");
        }

        //public async Task Send(string targetPubKey, string message)
        //{
        //    var ci = dictConnections.FirstOrDefault(_ => _.Value.PubKey == targetPubKey).Value;
        //    if (ci == null)
        //    {
        //        // connection not exist, save to redis
        //    }
        //    else
        //    {
        //        // connection exist, send directly
        //        await this.Clients.Client(ci.ConnectionId).SendAsync("onAuthSuccess");
        //        await Clients.All.SendAsync("messageReceived", username, message);
        //        Clients.cli
        //    }
        //}
    }
}