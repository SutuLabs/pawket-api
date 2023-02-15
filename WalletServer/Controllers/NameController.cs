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

        private static readonly Counter LegacyStandardResolveRequestRecordCount = Metrics.CreateCounter("standard_resolve_total", "Number of standard resolve request.");
        private static readonly Counter StandardResolveRequestRecordCount = Metrics.CreateCounter("name_resolve_total", "Number of standard resolve request.");
        private static readonly Gauge ValidNameRecordCount = Metrics.CreateGauge("name_valid_total", "Number of valid name.");

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
            if (request is null || request.queries is null || request.queries.Length == 0)
                return BadRequest("Invalid request");
            var queries = request.queries
                .Select(_ => _ with { name = _.name.ToLower() })
                .ToArray();
            StandardResolveRequestRecordCount.Inc();
            LegacyStandardResolveRequestRecordCount.Inc();
            var ne = await this.nameService.GetAllNamesAsync();
            ValidNameRecordCount.Set(ne.Length);

            var answers = ProduceAnswers(queries, ne).ToArray();
            return Ok(new StandardResolveQueryResponse(answers));
        }

#if DEBUG
        [HttpGet("all")]
        public async Task<ActionResult> GetAllDomains()
        {
            var ne = await this.nameService.GetAllNamesAsync();
            return Ok(ne);
        }
#endif

        private IEnumerable<StandardResolveAnswer> ProduceAnswers(StandardResolveQuery[] queries, NameEntity[] allNames)
        {
            foreach (var q in queries)
            {
                var a = q.type switch
                {
                    nameof(NameEntity.name) => allNames.FirstOrDefault(_ => q.name == _.address),
                    _ => allNames.FirstOrDefault(_ => q.name == _.name),
                };
                if (a is null) continue;

                // type must be name/address or attribute in bindings
                if (q.type != nameof(NameEntity.name)
                    && q.type != nameof(NameEntity.address)
                    && !(a?.bindings.ContainsKey(q.type) ?? false))
                {
                    continue;
                }

                yield return new StandardResolveAnswer(
                    q.name,
                    q.type,
                    600,
                    q.type switch
                    {
                        nameof(NameEntity.address) => a.address,
                        nameof(NameEntity.name) => a.address,
                        _ => a.bindings[q.type],
                    },
                    a.last_change_coin_name,
                    a.last_change_spent_index,
                    a.nft_coin_name);
            }
        }
    }
}