using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace ChatServer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly ILogger<ChatController> logger;
        private readonly ChatService chat;
        private readonly IHubContext<ChatHub> hub;

        public ChatController(ILogger<ChatController> logger, ChatService chat, IHubContext<ChatHub> hub)
        {
            this.logger = logger;
            this.chat = chat;
            this.hub = hub;
        }

        public record SendRequest(string ToPubKey, string Message, string Timestamp, string Signature);

        [HttpPost("send")]
        public async Task<ActionResult> Send(SendRequest request)
        {
            // TODO: check signature
            await chat.AddMessages(request.ToPubKey, request.Message);
            await hub.Clients.Group(request.ToPubKey).SendAsync("onMessageSignal");// send signal only
            return Ok();
        }

        public record ReceiveRequest(string PubKey, string Timestamp, string Signature);

        [HttpPost("receive")]
        public async Task<ActionResult> Receive(ReceiveRequest request)
        {
            // TODO: check signature
            var messages = await chat.GetMessages(request.PubKey, 10);
            return Ok(new { messages });
        }
    }
}