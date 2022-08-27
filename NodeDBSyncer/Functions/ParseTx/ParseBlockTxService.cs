namespace NodeDBSyncer.Functions.ParseTx;

using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using chia.dotnet.bech32;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodeDBSyncer.Helpers;
using NodeDBSyncer.Services;
using WalletServer.Helpers;

internal class ParseBlockTxService : BaseRefreshService
{
    public const int Timeout = 3000;
    private readonly AppSettings appSettings;

    public ParseBlockTxService(
        ILogger<ParseBlockTxService> logger,
        IOptions<AppSettings> appSettings)
        : base(logger, nameof(ParseBlockTxService), 5, 15, Timeout)
    {
        this.appSettings = appSettings.Value;

    }

    protected override async Task DoWorkAsync(CancellationToken token)
    {
        if (this.appSettings.ParsingTxBlockBatchSize == 0) return;

        using var target = new ParseTxDbConnection(this.appSettings.OnlineDbConnString);
        await target.Open();
        var nodeProcessor = new LocalNodeProcessor(this.appSettings.LocalNodeProcessor);

        var sw = new Stopwatch();
        sw.Start();
        var threshold = Timeout * 1000 / 2;
        while (sw.ElapsedMilliseconds < threshold)
        {
            var processed = await ParseTx(target, nodeProcessor);
            if (!processed) break;
        }
    }

    private async Task<bool> ParseTx(ParseTxDbConnection db, LocalNodeProcessor nodeProcessor)
    {
        var batch = this.appSettings.ParsingTxBlockBatchSize;

        var sw = new Stopwatch();
        sw.Start();
        var (blocks, refs) = await db.GetUnparsedBlock(batch);
        if (blocks.Length == 0) return false;

        var begin = blocks.Min(_ => _.index);
        var end = blocks.Max(_ => _.index);
        this.logger.LogInformation($"Parsing tx from blocks from [{begin}] to [{end}] [Total: {blocks.Length} with {refs.Length} refs].");


        var lstCoins = new ConcurrentBag<CoinInfoForStorage>();
        var lstBadBlocks = new ConcurrentBag<ulong>();

        byte[][] getGeneratorRefs(uint[]? refIdxes, ParseTxDbConnection.BlockTransactionGeneratorRetrieval[] refs)
        {
            if (refIdxes == null) return Array.Empty<byte[]>();
            return refs
                .Where(_ => refIdxes.Contains((uint)_.index))
                .Select(_ => _.generator)
                .ToArray();
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
        await Parallel.ForEachAsync(blocks, options, async (block, ct) =>
        {
            try
            {
                var coins = await GetCoinsFromBlock(block.generator, nodeProcessor, getGeneratorRefs(block.generator_ref_list, refs));
                var lstBlockCoins = new List<CoinInfoForStorage>();

                foreach (var r in coins)
                {
                    var pp = Newtonsoft.Json.JsonConvert.SerializeObject(
                    r.parsed_puzzle,
                    new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });
                    var coin = new CoinInfoForStorage(
                    r.coin_name.ToHexBytes(),
                    r.puzzle.ToHexBytes().Compress(),
                    pp,
                    r.solution.ToHexBytes().Compress(),
                    r.mods,
                    r.analysis);
                    lstBlockCoins.Add(coin);
                }

                lstBlockCoins.ToList().ForEach(c => lstCoins.Add(c));
            }
            catch (Exception ex)
            {
                var json = JsonSerializer.Serialize(new { generator = block.generator.ToHexWithPrefix0x(), });
                this.logger.LogWarning(ex, $"Block {block.index} format cannot be recognized. Json[Without Ref[{block.generator_ref_list?.Length}]]:\n{json}\nBlock Index: {block.index}");
                lstBadBlocks.Add(block.index);
            }
        });

        var tget = sw.ElapsedMilliseconds;

        if (!lstCoins.IsEmpty)
        {
            try
            {
                await db.WriteCoinClassRecords(lstCoins);
            }
            catch (Npgsql.PostgresException pgex)
            {
                if (pgex.SqlState == "23505"
                    && pgex.ConstraintName == $"{DbReference.CoinClassTableName}_coin_name_key"
                    && pgex.Routine == "_bt_check_unique"
                    && pgex.TableName == DbReference.CoinClassTableName)
                {
                    this.logger.LogWarning($"duplicate coin name found, maybe fork happened, clean related coins");
                    try
                    {
                        await db.RemoveCoinClass(lstCoins.Select(_ => _.coin_name).ToArray());
                        await db.WriteCoinClassRecords(lstCoins);
                    }
                    catch
                    {
                        return false;
                    }
                }
                else
                {
                    this.logger.LogWarning(pgex, $"Unknown postgres exception");
                    return false;
                }
            }
        }

        await db.UpdateParsedBlock(blocks.Select(_ => _.index).Except(lstBadBlocks).ToArray());

        sw.Stop();
        var estimatedRemain = sw.GetEta((long)(end - begin), (long)end);
        this.logger.LogInformation($"Processed," +
            $" estimated remain {estimatedRemain.TotalMinutes:0.0} minute(s)," +
            $" parse: {tget} ms, persistent: {sw.ElapsedMilliseconds - tget} ms," +
            $" total {lstCoins.Count} coin(s).");

        return true;
    }

    private static async Task<CoinInfo[]> GetCoinsFromBlock(
        byte[] generator,
        LocalNodeProcessor nodeProcessor,
        byte[][] refGenerators)
    {
        if (generator == null || generator.Length == 0) return Array.Empty<CoinInfo>();

        return await nodeProcessor.ParseBlock(generator, refGenerators);
    }
}
