using System.Text.Json;
using System.Text.Json.Serialization;
using ChiaApi;
using ChiaApi.Models.Request.FullNode;
using ChiaApi.Models.Responses.FullNode;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace WalletServer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class MiscController : ControllerBase
    {
        private readonly ILogger<MiscController> logger;
        private readonly AppSettings appSettings;

        public MiscController(ILogger<MiscController> logger, IOptions<AppSettings> appSettings)
        {
            this.logger = logger;
            this.appSettings = appSettings.Value;
        }

        public record PriceEntity(string Source, string From, string To, decimal Price, DateTime Time);

        [HttpGet("prices")]
        public async Task<IActionResult> GetPrice()
        {
            using var client = new HttpClient();
            try
            {
                var json = await client.GetStringAsync("http://10.177.0.173:5000/misc/prices");
                var prices = JsonConvert.DeserializeObject<PriceEntity[]>(json);
                return this.Ok(prices);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning("unable to get prices, ex: " + ex.Message);
                return this.StatusCode(StatusCodes.Status502BadGateway);
            }
        }
    }
}