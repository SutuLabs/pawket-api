using System.Text.Json;
using System.Text.Json.Serialization;
using ChiaApi;
using ChiaApi.Models.Request.FullNode;
using ChiaApi.Models.Responses.FullNode;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Prometheus;

namespace WalletServer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WalletController : ControllerBase
    {
        private readonly ILogger<WalletController> logger;
        private readonly AppSettings appSettings;
        private readonly FullNodeApiClient client;

        private static readonly Counter RequestRecordCount = Metrics.CreateCounter("request_record_total", "Number of record request.");
        private static readonly Counter PushTxCount = Metrics.CreateCounter("push_tx_total", "Number of pushtx request.");
        private static readonly Counter PushTxSuccessCount = Metrics.CreateCounter("push_tx_success_total", "Number of successful pushtx request.");
        private static readonly Counter RequestPuzzleCount = Metrics.CreateCounter("request_puzzle_total", "Number of puzzle request.");
        private static readonly Counter RequestCoinSolutionCount = Metrics.CreateCounter("request_coin_solution_total", "Number of CoinSolution request.");

        public WalletController(ILogger<WalletController> logger, IOptions<AppSettings> appSettings)
        {
            this.logger = logger;
            this.appSettings = appSettings.Value;
            // command: redir :8666 :8555
            var path = this.appSettings.Path ?? "";
            var cfg = new ChiaApiConfig(path + "private_full_node.crt", path + "private_full_node.key", this.appSettings.Host, this.appSettings.Port);
            this.client = new FullNodeApiClient(cfg);
        }

        public record GetRecordsRequest(string[] puzzleHashes, ulong? startHeight = null, ulong? endHeight = null, bool includeSpentCoins = false);
        public record GetRecordsResponse(ulong peekHeight, CoinRecordInfo[] coins);
        public record CoinRecordInfo(string puzzleHash, CoinRecord[] records);

        [HttpPost("records")]
        public async Task<ActionResult> GetRecords(GetRecordsRequest request)
        {
            if (request == null) return BadRequest("Invalid request");
            if (request.puzzleHashes == null || request.puzzleHashes.Length > 200)
                return BadRequest("Valid puzzle hash number per request is 200");
            var remoteIpAddress = this.GetRealIp();
            this.logger.LogDebug($"[{DateTime.UtcNow.ToShortTimeString()}]From {remoteIpAddress} request {request.puzzleHashes.FirstOrDefault()}"
                + $"[{request.puzzleHashes?.Length ?? -1}], includeSpent = {request.includeSpentCoins}");

            RequestRecordCount.Inc();
            var bcstaResp = await this.client.GetBlockchainStateAsync();
            if (bcstaResp == null || !bcstaResp.Success || bcstaResp.BlockchainState?.Peak == null) return StatusCode(503, "Cannot get blockchain status.");

            var list = new List<CoinRecordInfo>();
            foreach (var hash in request.puzzleHashes)
            {
                var recResp = await this.client.GetCoinRecordsByPuzzleHashAsync(hash, request.startHeight, request.endHeight, request.includeSpentCoins);
                if (recResp == null || !recResp.Success || recResp.CoinRecords == null) return BadRequest("Cannot get records.");
                list.Add(new CoinRecordInfo(hash, recResp.CoinRecords.ToArray()));
            }

            return Ok(new GetRecordsResponse(bcstaResp.BlockchainState.Peak.Height, list.ToArray()));
        }

        public record PushTxRequest(SpendBundleReq? bundle);
        public record SpendBundleReq
        (
            [property: JsonPropertyName("aggregated_signature")] string AggregatedSignature,
            [property: JsonPropertyName("coin_spends")] CoinSpendReq[]? CoinSpends
        );
        public record CoinSpendReq
        (
            [property: JsonPropertyName("coin")] CoinItemReq? Coin,
            [property: JsonPropertyName("puzzle_reveal")] string PuzzleReveal,
            [property: JsonPropertyName("solution")] string Solution
        );
        public record CoinItemReq
        (
            [property: JsonPropertyName("amount")] ulong Amount,
            [property: JsonPropertyName("parent_coin_info")] string ParentCoinInfo,
            [property: JsonPropertyName("puzzle_hash")] string PuzzleHash
        );

        [HttpPost("pushtx")]
        public async Task<ActionResult> PushTx(PushTxRequest request)
        {
            if (request == null || request.bundle == null) return BadRequest("Invalid request");
            PushTxCount.Inc();

            var bundle = new SpendBundle
            {
                AggregatedSignature = request.bundle.AggregatedSignature,
                CoinSolutions = request.bundle.CoinSpends?
                    .Select(cs => new ChiaApi.Models.Responses.Shared.CoinSpend
                    {
                        PuzzleReveal = cs.PuzzleReveal,
                        Solution = cs.Solution,
                        Coin = new ChiaApi.Models.Responses.Shared.CoinItem
                        {
                            Amount = cs.Coin.Amount,
                            ParentCoinInfo = cs.Coin.ParentCoinInfo,
                            PuzzleHash = cs.Coin.PuzzleHash,
                        },
                    })
                    .ToList(),
            };

            var remoteIpAddress = this.GetRealIp();
            this.logger.LogDebug($"[{DateTime.UtcNow.ToShortTimeString()}]From {remoteIpAddress} pushtx using coins[{request.bundle.CoinSpends?.Length}]");

            var result = await this.client.PushTxAsync(new SpendBundleRequest { SpendBundle = bundle });
            if (!result.Success)
            {
                this.logger.LogWarning($"[{DateTime.UtcNow.ToShortTimeString()}]push tx failed\n============\n{JsonSerializer.Serialize(result)}\n============\n{JsonSerializer.Serialize(bundle)}");
            }
            else
            {
                PushTxSuccessCount.Inc();
            }

            return Ok(result);
        }

        public record GetParentPuzzleRequest(string parentCoinId);
        public record GetParentPuzzleResponse(string parentCoinId, ulong amount, string parentParentCoinId, string puzzleReveal);

        [HttpPost("get-puzzle")]
        public async Task<ActionResult> GetParentPuzzle(GetParentPuzzleRequest request)
        {
            if (request == null || request.parentCoinId == null) return BadRequest("Invalid request");
            RequestPuzzleCount.Inc();

            var remoteIpAddress = this.GetRealIp();
            this.logger.LogDebug($"[{DateTime.UtcNow.ToShortTimeString()}]From {remoteIpAddress} request puzzle {request.parentCoinId}");

            var recResp = await this.client.GetCoinRecordByNameAsync(request.parentCoinId);
            if (recResp == null || !recResp.Success || recResp.CoinRecord?.Coin?.ParentCoinInfo == null) return BadRequest("Cannot get records.");
            if (!recResp.CoinRecord.Spent) return BadRequest("Coin not spend yet.");

            var puzResp = await this.client.GetPuzzleAndSolutionAsync(request.parentCoinId, recResp.CoinRecord.SpentBlockIndex);
            if (puzResp == null || !puzResp.Success || puzResp.CoinSolution?.PuzzleReveal == null)
            {
                this.logger.LogWarning($"failed to get puzzle for {recResp.CoinRecord.Coin.ParentCoinInfo} on {recResp.CoinRecord.ConfirmedBlockIndex}");
                return BadRequest("Failed to get coin.");
            }

            return Ok(new GetParentPuzzleResponse(request.parentCoinId, recResp.CoinRecord.Coin.Amount, recResp.CoinRecord.Coin.ParentCoinInfo, puzResp.CoinSolution.PuzzleReveal));
        }

        public record GetCoinSolutionRequest(string coinId);
        public record GetCoinSolutionResponse(CoinSpendReq CoinSpend);

        [HttpPost("get-coin-solution")]
        public async Task<ActionResult> GetCoinSolution(GetCoinSolutionRequest request)
        {
            if (request == null || request.coinId == null) return BadRequest("Invalid request");
            RequestCoinSolutionCount.Inc();

            var remoteIpAddress = this.GetRealIp();
            this.logger.LogInformation($"[{DateTime.UtcNow.ToShortTimeString()}]From {remoteIpAddress} request puzzle[debug] {request.coinId}");

            var recResp = await this.client.GetCoinRecordByNameAsync(request.coinId);
            if (recResp == null || !recResp.Success || recResp.CoinRecord?.Coin?.ParentCoinInfo == null) return BadRequest("Cannot get records.");
            if (!recResp.CoinRecord.Spent) return BadRequest("Coin not spend yet.");

            var puzResp = await this.client.GetPuzzleAndSolutionAsync(request.coinId, recResp.CoinRecord.SpentBlockIndex);
            if (puzResp == null || !puzResp.Success || puzResp.CoinSolution?.Coin == null)
            {
                this.logger.LogWarning($"failed to get puzzle for {recResp.CoinRecord.Coin.ParentCoinInfo} on {recResp.CoinRecord.ConfirmedBlockIndex}");
                return BadRequest("Failed to get coin.");
            }

            var cs = puzResp.CoinSolution;
            return Ok(new GetCoinSolutionResponse(new CoinSpendReq(
                new CoinItemReq(cs.Coin.Amount, cs.Coin.ParentCoinInfo, cs.Coin.PuzzleHash), cs.PuzzleReveal, cs.Solution)));
        }

        private string GetRealIp()
        {
            if (Request.Headers.TryGetValue("X-Real-IP", out var realIp) && !string.IsNullOrWhiteSpace(realIp))
            {
                return realIp;
            }

            return this.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        }
    }
}