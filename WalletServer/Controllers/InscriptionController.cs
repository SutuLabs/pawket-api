using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Prometheus;
using WalletServer.Helpers;

namespace WalletServer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class InscriptionController : ControllerBase
    {
        private readonly ILogger<InscriptionController> logger;
        private readonly IMemoryCache memoryCache;
        private readonly InscriptionCacheService inscriptionService;
        private readonly DataAccess dataAccess;
        private readonly AppSettings appSettings;

        private static readonly Counter GetTickListRequestRecordCount =
            Metrics.CreateCounter("inscription_tick_list_total", "Number of get tick list request.");

        public InscriptionController(
            ILogger<InscriptionController> logger,
            IMemoryCache memoryCache,
            InscriptionCacheService inscriptionService,
            DataAccess dataAccess,
            IOptions<AppSettings> appSettings)
        {
            this.logger = logger;
            this.memoryCache = memoryCache;
            this.inscriptionService = inscriptionService;
            this.dataAccess = dataAccess;
            this.appSettings = appSettings.Value;
        }

        public record GetTickListQueryResponse(TickEntity[] ticks);

        [HttpPost("ticks")]
        public async Task<ActionResult> GetTickList()
        {
            GetTickListRequestRecordCount.Inc();
            var ticks = (await this.inscriptionService.GetAllTicksAsync());

            return Ok(new GetTickListQueryResponse(ticks));
        }

        // /ticks (start, page, order) -> list
        // /tick/{tick} -> tick entity
        // /tick/{tick}/txs (start, page, order) -> list
        // /holder/{address} -> summary
        // /holder/{address}/{tick} (start, page, order) -> tick list
        // /holder/{address}/{tick}/txs (start, page, order) -> list
    }
}