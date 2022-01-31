using ChiaApi;
using ChiaApi.Models.Responses.FullNode;
using Microsoft.AspNetCore.Mvc;

namespace WalletServer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WalletController : ControllerBase
    {
        private readonly ILogger<WalletController> logger;
        private readonly FullNodeApiClient client;

        public WalletController(ILogger<WalletController> logger)
        {
            this.logger = logger;
            // command: redir :8666 :8555
            var path = "/home/sutu/.chia/mainnet/config/ssl/full_node/";
            var cfg = new ChiaApiConfig(path + "private_full_node.crt", path + "private_full_node.key", "localhost", 8555);
            this.client = new FullNodeApiClient(cfg);
        }

        public record GetRecordsRequest(string[] puzzleHashes, ulong? startHeight = null, ulong? endHeight = null, bool includeSpentCoins = false);
        public record GetRecordsResponse(ulong peekHeight, CoinRecordInfo[] coins);
        public record CoinRecordInfo(string puzzleHash, CoinRecord[] records);

        [HttpPost("records")]
        public async Task<ActionResult> GetRecords(GetRecordsRequest request)
        {
            if (request.puzzleHashes.Length > 20) return BadRequest("Valid puzzle hash number per request is 20");

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
    }
}