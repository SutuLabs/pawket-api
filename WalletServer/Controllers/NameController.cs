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
        private static readonly Counter GetRecentNameRequestRecordCount = Metrics.CreateCounter("name_recent_total", "Number of get recent name request.");
        private static readonly Counter GetWealthiestNameRequestRecordCount = Metrics.CreateCounter("name_wealthiest_total", "Number of get wealthiest name request.");

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
        public record StandardResolveAnswer(string name, string type, int time_to_live, string data, string proof_coin_name, int proof_coin_spent_index, string nft_coin_name, long expiry);
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
            if (ne is null) return StatusCode(500, "Internal name resolving issue");
            ValidNameRecordCount.Set(ne.Length);

            var answers = ProduceAnswers(queries, ne).ToArray();
            return Ok(new StandardResolveQueryResponse(answers));
        }

        public record GetRecentNamesQueryRequest(int? count);
        public record GetRecentNamesQueryResponse(RecentNameEntity[] names);

        [HttpPost("recent")]
        public async Task<ActionResult> GetRecentNames(GetRecentNamesQueryRequest request)
        {
            GetRecentNameRequestRecordCount.Inc();
            var count = request.count ?? 10;
            count = count > 100 ? 100 : count;
            var names = (await this.nameService.GetRecentNamesAsync()).Take(count).ToArray();
            return Ok(new GetRecentNamesQueryResponse(names));
        }

        public record GetWealthiestNamesQueryRequest(int? count, bool? isOwnerOnly);
        public record GetWealthiestNamesQueryResponse(WealthiestNameEntity[] names);

        [HttpPost("wealthiest")]
        public async Task<ActionResult> GetWealthiestNames(GetWealthiestNamesQueryRequest request)
        {
            GetWealthiestNameRequestRecordCount.Inc();
            var count = request.count ?? 10;
            count = count > 100 ? 100 : count;
            var names = (await this.nameService.GetWealthiestNamesAsync(request.isOwnerOnly ?? false))
                .Take(count).ToArray();
            return Ok(new GetWealthiestNamesQueryResponse(names));
        }

        [HttpGet("all")]
        public async Task<ActionResult> GetAllDomains()
        {
            var allNames = await this.nameService.GetAllNamesAsync();
            if (allNames is null) return StatusCode(500, "Internal name resolving issue");
            var validNames = GetValidNames(allNames);
            return Ok(validNames);
        }

        private NameEntity[] GetValidNames(NameEntity[] allNames)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var validNames = allNames.Where(_ => IsValid(_.expiry)).ToArray();
            return validNames;

            bool IsValid(int expiry)
            {
                // Temporarily extend expiry to 2024-04-15
                // UTC: Apr 15 2024 00:00:00
                expiry = expiry < 1713139200 ? 1713139200 : expiry;
                return expiry >= timestamp;
            }
        }

        private IEnumerable<StandardResolveAnswer> ProduceAnswers(StandardResolveQuery[] queries, NameEntity[] allNames)
        {
            var validNames = GetValidNames(allNames);
            const string typeWhois = "whois";

            foreach (var q in queries)
            {
                var a = q.type switch
                {
                    nameof(NameEntity.name) => validNames.FirstOrDefault(_ => q.name == _.address),
                    typeWhois => allNames.FirstOrDefault(_ => q.name == _.name),
                    _ => validNames.FirstOrDefault(_ => q.name == _.name),
                };
                if (a is null) continue;

                // type must be name/address or attribute in bindings
                if (q.type != nameof(NameEntity.name)
                    && q.type != nameof(NameEntity.address)
                    && q.type != typeWhois
                    && !(a?.bindings.ContainsKey(q.type) ?? false))
                {
                    continue;
                }

                yield return new StandardResolveAnswer(
                    q.type switch
                    {
                        nameof(NameEntity.name) => a.name,
                        _ => q.name,
                    },
                    q.type,
                    600,
                    q.type switch
                    {
                        nameof(NameEntity.address) => a.address,
                        nameof(NameEntity.name) => a.address,
                        typeWhois => "",
                        _ => a.bindings[q.type],
                    },
                    a.last_change_coin_name,
                    a.last_change_spent_index,
                    a.nft_coin_name,
                    a.expiry);
            }
        }
    }
}