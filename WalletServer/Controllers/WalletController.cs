using System.Text.Json.Serialization;
using ChiaApi;
using ChiaApi.Models.Request.FullNode;
using ChiaApi.Models.Responses.FullNode;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace WalletServer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WalletController : ControllerBase
    {
        private readonly ILogger<WalletController> logger;
        private readonly AppSettings appSettings;
        private readonly FullNodeApiClient client;

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
            if (request.puzzleHashes == null || request.puzzleHashes.Length > 20)
                return BadRequest("Valid puzzle hash number per request is 20");
            var remoteIpAddress = this.HttpContext.Connection.RemoteIpAddress;
            this.logger.LogDebug($"[{DateTime.UtcNow.ToShortTimeString()}]From {remoteIpAddress} request {request.puzzleHashes.FirstOrDefault()}"
                + $"[{request.puzzleHashes?.Length ?? -1}], includeSpent = {request.includeSpentCoins}");

            var bcstaResp = await this.client.GetBlockchainStateAsync();
            if (bcstaResp == null || !bcstaResp.Success || bcstaResp.BlockchainState?.Peak == null) return StatusCode(503, "Cannot get blockchain status.");

            var list = new List<CoinRecordInfo>();
            foreach (var hash in request.puzzleHashes)
            {
                var recResp = await this.client.GetCoinRecordsByPuzzleHashAsync(hash, request.startHeight, request.endHeight, request.includeSpentCoins);
                if (recResp == null || !recResp.Success || recResp.CoinRecords == null) return StatusCode(503, "Cannot get records.");
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

            var result = await this.client.PushTxAsync(new SpendBundleRequest { SpendBundle = bundle });

            return Ok(result);
        }
    }
}