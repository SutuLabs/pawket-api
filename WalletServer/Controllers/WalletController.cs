using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using chia.dotnet;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NodeDBSyncer.Helpers;
using Prometheus;
using WalletServer.Helpers;

namespace WalletServer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WalletController : ControllerBase
    {
        private readonly ILogger<WalletController> logger;
        private readonly IMemoryCache memoryCache;
        private readonly DataAccess dataAccess;
        private readonly PushLogHelper pushLogHelper;
        private readonly OnlineCounter onlineCounter;
        private readonly AppSettings appSettings;
        private readonly HttpRpcClient rpcClient;
        private readonly FullNodeProxy client;

        private static readonly Counter RequestRecordCount = Metrics.CreateCounter("request_record_total", "Number of record request.");
        private static readonly Counter PushTxCount = Metrics.CreateCounter("push_tx_total", "Number of pushtx request.");
        private static readonly Counter PushTxSuccessCount = Metrics.CreateCounter("push_tx_success_total", "Number of successful pushtx request.");
        private static readonly Counter RequestPuzzleCount = Metrics.CreateCounter("request_puzzle_total", "Number of puzzle request.");
        private static readonly Counter RequestCoinSolutionCount = Metrics.CreateCounter("request_coin_solution_total", "Number of CoinSolution request.");
        private static readonly Counter RequestOfferUploadCount = Metrics.CreateCounter("request_offer_upload_total", "Number of offer upload request.");
        private static readonly Counter RequestAnalysisCount = Metrics.CreateCounter("request_analysis_total", "Number of analysis record request.");

        public WalletController(
            ILogger<WalletController> logger,
            IMemoryCache memoryCache,
            DataAccess dataAccess,
            PushLogHelper pushLogHelper,
            OnlineCounter onlineCounter,
            IOptions<AppSettings> appSettings)
        {
            this.logger = logger;
            this.memoryCache = memoryCache;
            this.dataAccess = dataAccess;
            this.pushLogHelper = pushLogHelper;
            this.onlineCounter = onlineCounter;
            this.appSettings = appSettings.Value;
            // command: redir :8666 :8555
            var path = this.appSettings.Path ?? "";
            var endpoint = new EndpointInfo
            {
                CertPath = path + "private_full_node.crt",
                KeyPath = path + "private_full_node.key",
                Uri = new Uri($"https://{this.appSettings.Host}:{this.appSettings.Port}/"),
            };
            this.rpcClient = new HttpRpcClient(endpoint);
            this.client = new FullNodeProxy(this.rpcClient, "client");// { MaxRetries = 2 };
        }

        public record GetRecordsRequest(
            string[] puzzleHashes,
            long? startHeight = null,
            [property: Obsolete("don't need end height to restrict, as currently is using database instead of api")] ulong? endHeight = null,
            long? pageStart = null,
            int? pageLength = null,
            bool includeSpentCoins = false,
            bool hint = false,
            string? coinType = null);
        public record GetRecordsResponse(long peekHeight, CoinRecordInfo[] coins);
        public record CoinRecordInfo(string puzzleHash, CoinRecord[] records, long balance, FullBalanceInfo balanceInfo);

        private const int MaxCoinCount = 100;

        [HttpPost("records")]
        public async Task<ActionResult> GetRecords(GetRecordsRequest request)
        {
            if (request is null || request.puzzleHashes is null) return BadRequest("Invalid request");
            if (request.puzzleHashes.Length > 300)
                return BadRequest("Valid puzzle hash number per request is 300");
            var coinType = (CoinClassType?)null;
            if (request.coinType != null)
            {
                if (!Enum.TryParse<CoinClassType>(request.coinType, out var ct))
                    return BadRequest("Coin type cannot be recognized.");
                coinType = ct;
            }

            var remoteIpAddress = this.HttpContext.GetRealIp();
            this.onlineCounter.Renew(remoteIpAddress, request.puzzleHashes[0], request.puzzleHashes.Length);
            this.logger.LogDebug($"[{DateTime.UtcNow.ToShortTimeString()}]From {remoteIpAddress} request {request.puzzleHashes.FirstOrDefault()}"
                + $"[{request.puzzleHashes.Length}], includeSpent = {request.includeSpentCoins}");

            RequestRecordCount.Inc();
            var peak = await this.dataAccess.GetPeakHeight();

            var infos = new List<CoinRecordInfo>();
            foreach (var hash in request.puzzleHashes)
            {
                var balance = await this.dataAccess.GetBalance(hash);
                var coinRecords = await this.dataAccess.GetCoins(
                    new[] { hash },
                    request.includeSpentCoins,
                    GetCoinOrder.AnyIndexDesc,
                    request.coinType != null ? GetCoinMethod.Class : request.hint ? GetCoinMethod.Hint : GetCoinMethod.PuzzleHash,
                    request.startHeight,
                    request.pageStart,
                    request.pageLength,
                    coinType);
                infos.Add(new CoinRecordInfo(hash, coinRecords, balance.Amount, balance));
            }

            return Ok(new GetRecordsResponse(peak, infos.ToArray()));
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
            if (request?.bundle?.CoinSpends == null) return BadRequest("Invalid request");
            PushTxCount.Inc();

            var bundle = new SpendBundle
            {
                AggregatedSignature = request.bundle.AggregatedSignature,
                CoinSpends = request.bundle.CoinSpends
                    .Select(cs => new CoinSpend
                    {
                        PuzzleReveal = cs.PuzzleReveal,
                        Solution = cs.Solution,
                        Coin = new Coin
                        {
                            Amount = cs?.Coin?.Amount ?? 0,
                            ParentCoinInfo = cs?.Coin?.ParentCoinInfo ?? throw new Exception(""),
                            PuzzleHash = cs?.Coin?.PuzzleHash ?? throw new Exception(""),
                        },
                    })
                    .ToList(),
            };

            var remoteIpAddress = this.HttpContext.GetRealIp();
            this.logger.LogDebug($"[{DateTime.UtcNow.ToShortTimeString()}]From {remoteIpAddress} pushtx using coins[{request.bundle.CoinSpends.Length}]");
            var txid = (string?)null;
            var error = (string?)null;
            var status = 0;

            try
            {
                var result = await this.client.PushTx(bundle);

                if (!result)
                {
                    this.logger.LogWarning($@"[{DateTime.UtcNow.ToShortTimeString()}]push tx failed
============
{JsonSerializer.Serialize(result)}
============
{JsonSerializer.Serialize(bundle)}");
                    status = 2;
                }
                else
                {
                    PushTxSuccessCount.Inc();
                    status = 1;
                }

                return Ok(new { success = result });
            }
            catch (ResponseException re)
            {
                this.logger.LogWarning($"[{DateTime.UtcNow.ToShortTimeString()}]push tx failed: {(re.InnerException is null ? re.Message : re.InnerException.Message)}");
                status = 3;
                error = re.Message == "{\"status\":\"PENDING\",\"success\":true}" ? "error PENDING" : re.Message;
                return BadRequest(new { success = false, error = error });
            }
            finally
            {
                try
                {
                    await this.pushLogHelper.LogPushes(new PushLogEntity(
                        JsonSerializer.SerializeToUtf8Bytes(request.bundle).Compress(),
                        System.Net.IPAddress.Parse(remoteIpAddress),
                        txid,
                        status,
                        DateTime.UtcNow,
                        error));
                }
                catch (Exception ex)
                {
                    // ignore all exceptions
                    this.logger.LogWarning(ex, $"push log failed");
                }
            }
        }

        public record GetParentPuzzleRequest(string parentCoinId);
        public record GetParentPuzzleResponse(string parentCoinId, ulong amount, string parentParentCoinId, string puzzleReveal);

        [HttpPost("get-puzzle")]
        public async Task<ActionResult> GetParentPuzzle(GetParentPuzzleRequest request)
        {
            if (request == null || request.parentCoinId == null) return BadRequest("Invalid request");
            RequestPuzzleCount.Inc();

            var remoteIpAddress = this.HttpContext.GetRealIp();
            this.logger.LogDebug($"[{DateTime.UtcNow.ToShortTimeString()}]From {remoteIpAddress} request puzzle {request.parentCoinId}");

            //var parentCoin = await this.client.GetCoinRecordByName(request.parentCoinId);
            var parentCoin = await RetryAsync(_ => this.client.GetCoinRecordByName(request.parentCoinId));
            if (!parentCoin.Spent) return BadRequest("Coin not spend yet.");

            //var spend = await this.client.GetPuzzleAndSolution(request.parentCoinId, parentCoin.SpentBlockIndex);
            var spend = await RetryAsync(_ => this.client.GetPuzzleAndSolution(request.parentCoinId, parentCoin.SpentBlockIndex));
            if (string.IsNullOrEmpty(spend.PuzzleReveal))
            {
                this.logger.LogWarning($"failed to get puzzle for {parentCoin.Coin.ParentCoinInfo} on {parentCoin.ConfirmedBlockIndex}");
                return BadRequest("Failed to get coin.");
            }

            return Ok(new GetParentPuzzleResponse(request.parentCoinId, parentCoin.Coin.Amount, parentCoin.Coin.ParentCoinInfo, spend.PuzzleReveal));
        }

        public record GetCoinSolutionRequest(
            [property: Obsolete("legacy compatibility api")] string? coinId,
            string[]? coinIds,
            int? pageStart = null,
            int? pageLength = null);
        [Obsolete("legacy compatibility api")]
        public record GetCoinSolutionLegacyResponse(CoinSpendReq CoinSpend);
        public record GetCoinSolutionResponse(CoinSpendReq[] CoinSpends);

        [HttpPost("get-coin-solution")]
        public async Task<ActionResult> GetCoinSolution(GetCoinSolutionRequest request)
        {
            if (request == null) return BadRequest("Malformat request");
            var coinIds = request.coinId != null ? new[] { request.coinId } : request.coinIds != null ? request.coinIds : null;
            if (coinIds == null) return BadRequest("Invalid request");

            RequestCoinSolutionCount.Inc();

            var remoteIpAddress = this.HttpContext.GetRealIp();
            this.logger.LogInformation($"[{DateTime.UtcNow.ToShortTimeString()}]From {remoteIpAddress} request puzzle[debug] {request.coinId}");


            var coins = await dataAccess.GetCoinDetails(coinIds);
            if (request.coinId != null)
            {
                if (coins.Length != 1) return BadRequest("Cannot find corresponding coin.");
                return Ok(new GetCoinSolutionLegacyResponse(ConvertCoin(coins.First())));
            }

            return Ok(new GetCoinSolutionResponse(coins.Select(_ => ConvertCoin(_)).ToArray()));

            //if (request.coinId != null)
            //{
            //    var cs = await GetCoinSolutionByApi(request.coinId);
            //    if (cs == null) return BadRequest("Failed to get coin.");
            //    return Ok(new GetCoinSolutionResponse(cs));
            //}
        }

        public record UploadOfferRequest(string offer);
        public record DexieErrorResponse(bool success, string error_message);

        [HttpPost("offers")]
        public async Task<ActionResult> UploadOffer(UploadOfferRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.offer)) return BadRequest("Malformat request");
            RequestOfferUploadCount.Inc();

            using var client = new HttpClient();
            var resp = await client.PostAsJsonAsync(this.appSettings.Network.OfferUploadTarget, request);
            if (resp.IsSuccessStatusCode)
            {
                return Ok();
            }
            else
            {
                using var sr = new StreamReader(resp.Content.ReadAsStream());
                var content = await sr.ReadToEndAsync();
                try
                {
                    var err = JsonSerializer.Deserialize<DexieErrorResponse>(content);
                    this.logger.LogWarning($"failed to push to dexie, response: {content}");
                    var code = resp.StatusCode == HttpStatusCode.BadRequest
                        ? HttpStatusCode.BadRequest
                        : HttpStatusCode.BadGateway;
                    return StatusCode((int)code, "Unable to finish your request: " + err?.error_message);
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex, $"failed to deserialize and response, response: {content}");
                    return StatusCode((int)HttpStatusCode.BadGateway, "Unable to send your request");
                }
            }
        }

        private CoinSpendReq ConvertCoin(CoinDetail coin)
        {
            return new CoinSpendReq(new CoinItemReq(coin.Amount, coin.ParentCoinInfo, coin.PuzzleHash),
                coin.PuzzleReveal ?? string.Empty,
                coin.Solution ?? string.Empty);
        }

        private async Task<CoinSpendReq?> GetCoinSolutionByApi(string coinId)
        {
            var thisRecord = await this.client.GetCoinRecordByName(coinId);
            if (!thisRecord.Spent)
            {
                var c = thisRecord.Coin;
                return new CoinSpendReq(
                    new CoinItemReq(c.Amount, c.ParentCoinInfo, c.PuzzleHash), string.Empty, string.Empty);
            }

            var cs = await this.client.GetPuzzleAndSolution(coinId, thisRecord.SpentBlockIndex);
            if (string.IsNullOrEmpty(cs.PuzzleReveal) || string.IsNullOrEmpty(cs.Solution))
            {
                this.logger.LogWarning($"failed to get puzzle for {thisRecord.Coin.ParentCoinInfo} on {thisRecord.ConfirmedBlockIndex}");
                return null;
            }

            return new CoinSpendReq(
                new CoinItemReq(cs.Coin.Amount, cs.Coin.ParentCoinInfo, cs.Coin.PuzzleHash), cs.PuzzleReveal, cs.Solution);
        }

        public record GetNetworkInfoResponse(string name, string prefix, string chainId, string symbol, int @decimal, string explorerUrl);

        [HttpGet("network")]
        public async Task<ActionResult> GetNetworkInfo()
        {
            // TODO: the code may used when we need to check network or need compatibility.
            ////if (!this.memoryCache.TryGetValue(nameof(GetNetworkInfo), out GetNetworkInfoResponse cacheInfo))
            ////{
            ////    var (name, prefix) = await this.client.GetNetworkInfo();
            ////    cacheInfo = new GetNetworkInfoResponse(name, prefix);

            ////    var cacheEntryOptions = new MemoryCacheEntryOptions()
            ////        .SetAbsoluteExpiration(TimeSpan.FromMinutes(10));

            ////    this.memoryCache.Set(nameof(GetNetworkInfo), cacheInfo, cacheEntryOptions);
            ////}

            ////return Ok(cacheInfo);

            var net = this.appSettings.Network;
            return Ok(new GetNetworkInfoResponse(net.Name, net.Prefix, net.ChainId, net.Symbol, net.Decimal, net.ExplorerUrl));
        }

        public record GetAnalysisRequest(string[] puzzleHashes);
        public record GetAnalysisResponse(CoinAnalysis[] analyses);

        [HttpPost("analysis")]
        public async Task<ActionResult> GetAnalysis(GetAnalysisRequest request)
        {
            if (request is null || request.puzzleHashes is null) return BadRequest("Invalid request");
            if (request.puzzleHashes.Length > 200)
                return BadRequest("Valid puzzle hash number per request is 300");

            //var remoteIpAddress = this.HttpContext.GetRealIp();
            //this.onlineCounter.Renew(remoteIpAddress, request.puzzleHashes[0], request.puzzleHashes.Length);
            //this.logger.LogDebug($"[{DateTime.UtcNow.ToShortTimeString()}]From {remoteIpAddress} request {request.puzzleHashes.FirstOrDefault()}"
            //    + $"[{request.puzzleHashes.Length}], includeSpent = {request.includeSpentCoins}");

            RequestAnalysisCount.Inc();

            var analyses = await this.dataAccess.GetCoinAnalysis(request.puzzleHashes);
            return Ok(new GetAnalysisResponse(analyses));
        }

        const uint MaxRetries = 3;
        const uint RetryWait = 100;

        private async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> function, CancellationToken cancellationToken = default, uint? maxRetries = null)
        {
            var attempts = 0;
            var lastError = "";
            var lastRequest = new Message();
            maxRetries ??= MaxRetries;

            try
            {
                while (attempts <= maxRetries)
                {
                    try
                    {
                        var response = await function(cancellationToken).ConfigureAwait(false);
                        return response;
                    }
                    catch (ResponseException re)
                    {
                        lastError = re.Message;
                        lastRequest = re.Request;
                    }

                    if (maxRetries == 0) break;

                    attempts++;
                    var waitTime = (int)RetryWait * attempts;

                    await Task.Delay(waitTime, cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new TaskCanceledException();
                    }
                }
            }
            catch (TaskCanceledException)
            {
                throw;
            }
            catch (Exception e) // wrap eveything else in a response exception - this will include websocket or http specific failures
            {
                throw new ResponseException(lastRequest, "Something went wrong sending the rpc message. Inspect the InnerException for details.", e);
            }

            if (attempts == 1)
            {
                throw new ResponseException(lastRequest, lastError);
            }

            throw new ResponseException(lastRequest, $"Failed after {attempts} attempts, last error: {lastError}");
        }
    }
}