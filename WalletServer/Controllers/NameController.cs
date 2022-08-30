using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Prometheus;
using WalletServer.Helpers;

namespace WalletServer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class NameController : ControllerBase
    {
        private readonly ILogger<NameController> logger;
        private readonly IMemoryCache memoryCache;
        private readonly NameResolvingService nameService;
        private readonly DataAccess dataAccess;
        private readonly AppSettings appSettings;

        private static readonly Counter StandardResolveRequestRecordCount = Metrics.CreateCounter("standard_resolve_total", "Number of standard resolve request.");

        public NameController(
            ILogger<NameController> logger,
            IMemoryCache memoryCache,
            NameResolvingService nameService,
            DataAccess dataAccess,
            IOptions<AppSettings> appSettings)
        {
            this.logger = logger;
            this.memoryCache = memoryCache;
            this.nameService = nameService;
            this.dataAccess = dataAccess;
            this.appSettings = appSettings.Value;
        }

        public record StandardResolveQueryRequest(StandardResolveQuery[]? queries);
        public record StandardResolveQueryResponse(StandardResolveAnswer[] answers);
        public record StandardResolveQuery(string name, string type);
        public record StandardResolveAnswer(string name, string type, int time_to_live, string data, string proof_coin_name, int proof_coin_spent_index, string nft_coin_name);
        public const int MaxQueryPerRequest = 10;

        [HttpPost("resolve")]
        public async Task<ActionResult> StandardResolve(StandardResolveQueryRequest request)
        {
            if (request is null || request.queries is null || request.queries.Length == 0) return BadRequest("Invalid request");
            request = request with { queries = request.queries.Select(_ => _ with { name = _.name.ToLower() }).ToArray() };
            StandardResolveRequestRecordCount.Inc();
            var ne = await this.nameService.QueryNames(request.queries.Select(_ => _.name).ToArray());
            var answers = request.queries
                .Select(_ => new { q = _, a = ne.FirstOrDefault(n => _.name == n.name) })
                .Where(_ => _.q.type == nameof(NameEntity.address))
                .Select(_ => _.a is null ? null : new StandardResolveAnswer(
                    _.q.name,
                    _.q.type,
                    600,
                    _.a.address,
                    _.a.last_change_coin_name,
                    _.a.last_change_spent_index,
                    _.a.nft_coin_name))
                .WhereNotNull()
                .ToArray();

            return Ok(new StandardResolveQueryResponse(answers));
        }
    }
}