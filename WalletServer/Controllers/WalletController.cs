using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using chia.dotnet;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
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
        private readonly ChiaWalletData chiaData;
        private readonly ChiaCoinStore coinStore;
        private readonly FilePushLog pushLogHelper;
        private readonly OnlineCounter onlineCounter;
        private readonly AppSettings appSettings;
        private readonly HttpRpcClient rpcClient;
        private readonly FullNodeProxy client;

        private static string[] refererLabels = new[] { "referer" };

        private static readonly Counter RequestRecordCount = Metrics.CreateCounter("request_record_total", "Number of record request.", refererLabels);
        private static readonly Counter PushTxCount = Metrics.CreateCounter("push_tx_total", "Number of pushtx request.", refererLabels);
        private static readonly Counter PushTxSuccessCount = Metrics.CreateCounter("push_tx_success_total", "Number of successful pushtx request.");
        private static readonly Counter RequestPuzzleCount = Metrics.CreateCounter("request_puzzle_total", "Number of puzzle request.", refererLabels);
        private static readonly Counter RequestCoinSolutionCount = Metrics.CreateCounter("request_coin_solution_total", "Number of CoinSolution request.", refererLabels);
        private static readonly Counter RequestBlockCount = Metrics.CreateCounter("request_block_total", "Number of Block request.", refererLabels);
        private static readonly Counter RequestOfferUploadCount = Metrics.CreateCounter("request_offer_upload_total", "Number of offer upload request.", refererLabels);

        public WalletController(
            ILogger<WalletController> logger,
            IMemoryCache memoryCache,
            ChiaWalletData chiaData,
            ChiaCoinStore coinStore,
            FilePushLog pushLogHelper,
            OnlineCounter onlineCounter,
            IOptions<AppSettings> appSettings)
        {
            this.logger = logger;
            this.memoryCache = memoryCache;
            this.chiaData = chiaData;
            this.coinStore = coinStore;
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
            ulong? endHeight = null,
            long? pageStart = null,
            int? pageLength = null,
            bool includeSpentCoins = false,
            bool hint = false)
        {
            [JsonExtensionData]
            public Dictionary<string, JsonElement>? Extra { get; init; }
        }
        public record GetRecordsResponse(long peekHeight, CoinRecordInfo[] coins);
        public record CoinRecordInfo(string puzzleHash, CoinRecord[] records, long? balance, FullBalanceInfo? balanceInfo);
        public record GetCoinsRequest(
            string puzzleHash,
            bool hint = false,
            bool includeSpentCoins = false,
            long? startHeight = null,
            ulong? endHeight = null,
            int pageLength = 100,
            string? cursor = null)
        {
            [JsonExtensionData]
            public Dictionary<string, JsonElement>? Extra { get; init; }
        }
        public record CoinPageInfo(string puzzleHash, CoinRecord[] records);
        public record GetCoinsResponse(long peekHeight, CoinPageInfo[] coins, string? nextCursor);
        private record RecordsCursor(string PuzzleHash, bool Hint, bool IncludeSpentCoins,
            long? StartHeight, ulong? EndHeight, int PageLength, uint SnapshotHeight,
            string BlockHash, long LastSortHeight, string LastCoinId);

        private const int MaxCoinCount = 100;

        [HttpPost("records")]
        public async Task<ActionResult> GetRecords(GetRecordsRequest request)
        {
            if (request is null || request.puzzleHashes is null || request.puzzleHashes.Length == 0)
                return BadRequest("Invalid request");
            if (request.puzzleHashes.Length > 300)
                return BadRequest("Valid puzzle hash number per request is 300");
            if (!ValidHeights(request.startHeight, request.endHeight)) return BadRequest("Invalid height range");

            var remoteIpAddress = this.HttpContext.GetRealIp();
            this.onlineCounter.Renew(remoteIpAddress, request.puzzleHashes[0], request.puzzleHashes.Length);
            this.logger.LogDebug($"[{DateTime.UtcNow.ToShortTimeString()}]From {remoteIpAddress} request {request.puzzleHashes.FirstOrDefault()}"
                + $"[{request.puzzleHashes.Length}], includeSpent = {request.includeSpentCoins}");

            RequestRecordCount.WithLabels(this.HttpContext.GetReferer(logger)).Inc();
            var peak = await this.chiaData.GetPeakHeight();
            if (HasDeprecatedFields(request.Extra))
                return Ok(new GetRecordsResponse(peak, Array.Empty<CoinRecordInfo>()));
            if (request.puzzleHashes.Any(hash => !IsValidHash(hash)))
                return BadRequest("Invalid puzzle hash");

            var infos = new List<CoinRecordInfo>();
            using var db = coinStore.Open();
            foreach (var hash in request.puzzleHashes)
            {
                var coinRecords = coinStore.GetPage(db, hash, request.hint, request.includeSpentCoins,
                    (uint?)request.startHeight, (uint?)request.endHeight, checked((uint)peak),
                    request.pageStart ?? 0, request.pageLength ?? 100);
                var balance = coinStore.GetBalance(db, hash, checked((uint)peak));
                infos.Add(new CoinRecordInfo(hash, coinRecords.Select(x => x.Record).ToArray(), balance.Amount, balance));
            }

            return Ok(new GetRecordsResponse(peak, infos.Where(_ => _.records.Length > 0).ToArray()));
        }

        [HttpPost("coins")]
        public async Task<ActionResult> GetCoins(GetCoinsRequest request)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.puzzleHash))
                return BadRequest("Invalid request");
            if (!ValidHeights(request.startHeight, request.endHeight)) return BadRequest("Invalid height range");
            if (request.pageLength is < 1 or > 1000) return BadRequest("Invalid page length");

            var peak = checked((uint)await chiaData.GetPeakHeight());
            if (HasDeprecatedFields(request.Extra))
                return Ok(new GetCoinsResponse(peak, Array.Empty<CoinPageInfo>(), null));
            if (!IsValidHash(request.puzzleHash)) return BadRequest("Invalid puzzle hash");

            RecordsCursor? previous = null;
            if (request.cursor != null)
            {
                if (request.cursor.Length > 4096) return BadRequest("Invalid cursor");
                try { previous = DecodeCursor(request.cursor); }
                catch (Exception ex) when (ex is FormatException or JsonException)
                { return BadRequest("Invalid cursor"); }
                if (previous is null || !IsValidHash(previous.PuzzleHash)
                    || !IsValidHash(previous.BlockHash) || !IsValidHash(previous.LastCoinId)
                    || previous.LastSortHeight < 0 || previous.LastSortHeight > previous.SnapshotHeight
                    || !previous.PuzzleHash.Equals(request.puzzleHash, StringComparison.OrdinalIgnoreCase)
                    || previous.Hint != request.hint || previous.IncludeSpentCoins != request.includeSpentCoins
                    || previous.StartHeight != request.startHeight || previous.EndHeight != request.endHeight
                    || previous.PageLength != request.pageLength || previous.SnapshotHeight > peak)
                    return BadRequest("Invalid cursor");
            }
            var snapshot = previous?.SnapshotHeight ?? peak;
            var block = await client.GetBlockRecordByHeight(snapshot);
            if (previous != null && !block.HeaderHash.Equals(previous.BlockHash, StringComparison.OrdinalIgnoreCase))
                return Conflict("Chain changed; restart pagination");

            using var db = coinStore.Open();
            byte[]? afterId;
            try { afterId = previous is null ? null : Convert.FromHexString(previous.LastCoinId); }
            catch (FormatException) { return BadRequest("Invalid cursor"); }
            var rows = coinStore.GetPage(db, request.puzzleHash, request.hint, request.includeSpentCoins,
                (uint?)request.startHeight, (uint?)request.endHeight, snapshot, 0, request.pageLength + 1,
                previous?.LastSortHeight, afterId);
            var page = rows.Take(request.pageLength).ToArray();
            string? next = null;
            if (rows.Length > request.pageLength)
            {
                var last = page[^1];
                next = EncodeCursor(new RecordsCursor(request.puzzleHash, request.hint, request.includeSpentCoins,
                    request.startHeight, request.endHeight, request.pageLength, snapshot, block.HeaderHash,
                    last.SortHeight, Convert.ToHexString(last.CoinId)));
            }
            var groups = page.Length == 0 ? Array.Empty<CoinPageInfo>() : new[]
            {
                new CoinPageInfo(request.puzzleHash, page.Select(x => x.Record).ToArray()),
            };
            return Ok(new GetCoinsResponse(snapshot, groups, next));
        }

        private static bool HasDeprecatedFields(Dictionary<string, JsonElement>? extra) =>
            extra?.Keys.Any(key => key.Equals("coinType", StringComparison.OrdinalIgnoreCase)
                || key.Equals("includeAnalysis", StringComparison.OrdinalIgnoreCase)) == true;

        private static bool IsValidHash(string? value)
        {
            if (value is null) return false;
            var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value.AsSpan(2) : value.AsSpan();
            if (hex.Length != 64) return false;
            foreach (var digit in hex)
                if (!Uri.IsHexDigit(digit)) return false;
            return true;
        }

        private static string EncodeCursor(RecordsCursor cursor) =>
            Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor)).TrimEnd('=')
                .Replace('+', '-').Replace('/', '_');

        private static RecordsCursor? DecodeCursor(string value)
        {
            var encoded = value.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            return JsonSerializer.Deserialize<RecordsCursor>(Convert.FromBase64String(encoded));
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
            [property: JsonPropertyName("solution")] string Solution,
            [property: JsonPropertyName("confirmed_index")] ulong ConfirmedIndex,
            [property: JsonPropertyName("spent_index")] ulong SpentIndex,
            [property: JsonPropertyName("timestamp")] ulong Timestamp
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
            PushTxCount.WithLabels(this.HttpContext.GetReferer(logger)).Inc();

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
                    await this.pushLogHelper.AppendAsync(request.bundle, remoteIpAddress, txid,
                        status, DateTime.UtcNow, error);
                }
                catch (Exception ex)
                {
                    // ignore all exceptions
                    this.logger.LogWarning(ex, $"push log failed");
                }
            }
        }

        public record GetParentPuzzleRequest(string parentCoinId, long? startHeight = null, ulong? endHeight = null);
        public record GetParentPuzzleResponse(string parentCoinId, ulong amount, string parentParentCoinId, string puzzleReveal);

        [HttpPost("get-puzzle")]
        public async Task<ActionResult> GetParentPuzzle(GetParentPuzzleRequest request)
        {
            if (request == null || request.parentCoinId == null) return BadRequest("Invalid request");
            if (!ValidHeights(request.startHeight, request.endHeight)) return BadRequest("Invalid height range");
            RequestPuzzleCount.WithLabels(this.HttpContext.GetReferer(logger)).Inc();

            var remoteIpAddress = this.HttpContext.GetRealIp();
            this.logger.LogDebug($"[{DateTime.UtcNow.ToShortTimeString()}]From {remoteIpAddress} request puzzle {request.parentCoinId}");

            var coins = await chiaData.GetParentPuzzle(request.parentCoinId,
                (uint?)request.startHeight, (uint?)request.endHeight);
            if (coins.Length != 1) return BadRequest("Cannot find corresponding coin.");
            var c = coins.Single();

            return Ok(new GetParentPuzzleResponse(request.parentCoinId, c.Amount, c.ParentCoinName, c.PuzzleReveal));
        }

        private async Task<GetParentPuzzleResponse?> GetParentPuzzleByApi(GetParentPuzzleRequest request)
        {
            var parentCoin = await RetryAsync(_ => this.client.GetCoinRecordByName(request.parentCoinId));
            if (!parentCoin.Spent) throw new BadHttpRequestException("Coin not spend yet.");

            var spend = await RetryAsync(_ => this.client.GetPuzzleAndSolution(request.parentCoinId, parentCoin.SpentBlockIndex));
            if (string.IsNullOrEmpty(spend.PuzzleReveal))
            {
                this.logger.LogWarning($"failed to get puzzle for {parentCoin.Coin.ParentCoinInfo} on {parentCoin.ConfirmedBlockIndex}");
                throw new BadHttpRequestException("Failed to get coin.");
            }

            return new GetParentPuzzleResponse(request.parentCoinId, parentCoin.Coin.Amount, parentCoin.Coin.ParentCoinInfo, spend.PuzzleReveal);
        }

        public record GetCoinSolutionRequest(
            [property: Obsolete("legacy compatibility api")] string? coinId,
            string[]? coinIds,
            int? pageStart = null,
            int? pageLength = null,
            long? startHeight = null,
            ulong? endHeight = null);
        [Obsolete("legacy compatibility api")]
        public record GetCoinSolutionLegacyResponse(CoinSpendReq CoinSpend);
        public record GetCoinSolutionResponse(CoinSpendReq[] CoinSpends);

        [HttpPost("get-coin-solution")]
        public async Task<ActionResult> GetCoinSolution(GetCoinSolutionRequest request)
        {
            if (request == null) return BadRequest("Malformat request");
            var coinIds = request.coinId != null ? new[] { request.coinId } : request.coinIds != null ? request.coinIds : null;
            if (coinIds == null) return BadRequest("Invalid request");
            if (!ValidHeights(request.startHeight, request.endHeight)) return BadRequest("Invalid height range");

            RequestCoinSolutionCount.WithLabels(this.HttpContext.GetReferer(logger)).Inc();

            var remoteIpAddress = this.HttpContext.GetRealIp();
            this.logger.LogDebug($"[{DateTime.UtcNow.ToShortTimeString()}]From {remoteIpAddress} request coin solution {request.coinId}");


            var coins = await chiaData.GetCoinDetails(coinIds,
                (uint?)request.startHeight, (uint?)request.endHeight, request.pageStart, request.pageLength);
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

        public record GetBlockRequest(int[]? indexes);

        [HttpPost("get-block")]
        public async Task<ActionResult> GetBlock(GetBlockRequest request)
        {
            if (request == null) return BadRequest("Malformat request");
            if (request.indexes == null) return BadRequest("Invalid request");

            RequestBlockCount.WithLabels(this.HttpContext.GetReferer(logger)).Inc();

            var remoteIpAddress = this.HttpContext.GetRealIp();
            this.logger.LogDebug($"[{DateTime.UtcNow.ToShortTimeString()}]From {remoteIpAddress} request block {string.Join(",", request.indexes)}");

            var blocks = await chiaData.GetBlock(request.indexes);
            blocks = blocks with
            {
                Blocks = blocks.Blocks.Select(_ => _ with { generator = _.generator.CompressGzip() }).ToArray(),
                RefBlocks = blocks.RefBlocks.Select(_ => _ with { generator = _.generator.CompressGzip() }).ToArray(),
            };

            return Ok(blocks);
        }

        public record UploadOfferRequest(string offer);
        public record DexieErrorResponse(bool success, string error_message);

        [HttpPost("offers")]
        public async Task<ActionResult> UploadOffer(UploadOfferRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.offer)) return BadRequest("Malformat request");
            RequestOfferUploadCount.WithLabels(this.HttpContext.GetReferer(logger)).Inc();

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
                coin.Solution ?? string.Empty,
                coin.ConfirmedIndex,
                coin.SpentIndex,
                coin.Timestamp);
        }

        private async Task<CoinSpendReq?> GetCoinSolutionByApi(string coinId)
        {
            var thisRecord = await this.client.GetCoinRecordByName(coinId);
            if (!thisRecord.Spent)
            {
                var c = thisRecord.Coin;
                return new CoinSpendReq(
                    new CoinItemReq(c.Amount, c.ParentCoinInfo, c.PuzzleHash), string.Empty, string.Empty,
                        thisRecord.ConfirmedBlockIndex, thisRecord.SpentBlockIndex, thisRecord.Timestamp);
            }

            var cs = await this.client.GetPuzzleAndSolution(coinId, thisRecord.SpentBlockIndex);
            if (string.IsNullOrEmpty(cs.PuzzleReveal) || string.IsNullOrEmpty(cs.Solution))
            {
                this.logger.LogWarning($"failed to get puzzle for {thisRecord.Coin.ParentCoinInfo} on {thisRecord.ConfirmedBlockIndex}");
                return null;
            }

            return new CoinSpendReq(
                new CoinItemReq(cs.Coin.Amount, cs.Coin.ParentCoinInfo, cs.Coin.PuzzleHash), cs.PuzzleReveal, cs.Solution,
                    thisRecord.ConfirmedBlockIndex, thisRecord.SpentBlockIndex, thisRecord.Timestamp);
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

        const uint MaxRetries = 3;
        const uint RetryWait = 100;

        private static bool ValidHeights(long? start, ulong? end) =>
            (start == null || start is >= 0 and <= uint.MaxValue)
            && (end == null || end <= uint.MaxValue)
            && (start == null || end == null || (ulong)start.Value <= end);

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
