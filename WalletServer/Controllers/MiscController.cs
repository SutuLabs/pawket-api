using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Prometheus;
using WalletServer.Helpers;

namespace WalletServer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class MiscController : ControllerBase
    {
        private readonly ILogger<MiscController> logger;
        private readonly DataAccess dataAccess;
        private readonly AppSettings appSettings;

        private static readonly Counter RequestPriceCount = Metrics.CreateCounter("request_price_total", "Number of Price request.");

        public MiscController(ILogger<MiscController> logger, DataAccess dataAccess, IOptions<AppSettings> appSettings)
        {
            this.logger = logger;
            this.dataAccess = dataAccess;
            this.appSettings = appSettings.Value;
        }

        public record PriceResponse(string Source, string From, string To, decimal Price, DateTime Time);

        [HttpGet("prices")]
        public async Task<IActionResult> GetPrice()
        {
            RequestPriceCount.Inc();

            if (string.IsNullOrWhiteSpace(this.appSettings.PriceSourceUrl))
            {
                var prices = await this.dataAccess.GetLatestPrices("XCH");
                return this.Ok(prices.Select(_ => new PriceResponse(_.source, _.from, _.to, _.price, _.time)));
            }
            else
            {
                using var client = new HttpClient();
                try
                {
                    var json = await client.GetStringAsync(this.appSettings.PriceSourceUrl);
                    var prices = JsonConvert.DeserializeObject<PriceResponse[]>(json);
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
}