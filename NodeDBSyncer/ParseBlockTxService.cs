namespace NodeDBSyncer;

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
using WalletServer.Helpers;

internal class ParseBlockTxService : BaseRefreshService
{
    private readonly AppSettings appSettings;

    public ParseBlockTxService(
        ILogger<ParseBlockTxService> logger,
        IOptions<AppSettings> appSettings)
        : base(logger, nameof(ParseBlockTxService), 2, 15, 3000)
    {
        this.appSettings = appSettings.Value;

    }

    protected override async Task DoWorkAsync(CancellationToken token)
    {
        if (this.appSettings.SyncBlockBatchSize == 0) return;

        if (string.IsNullOrEmpty(this.appSettings.NodeCertPath)
            || string.IsNullOrEmpty(this.appSettings.NodeKeyPath)
            || string.IsNullOrEmpty(this.appSettings.NodeUri))
        {
            logger.LogWarning("Node information is not available in appsettings");
            return;
        }

        var endpoint = new chia.dotnet.EndpointInfo
        {
            CertPath = this.appSettings.NodeCertPath,
            KeyPath = this.appSettings.NodeKeyPath,
            Uri = new Uri(this.appSettings.NodeUri),
        };
        using var chain = new SourceChain(endpoint);
        try
        {
            var st = chain.GetChainState();
            if (st == null)
            {
                logger.LogWarning("Cannot retrieve chain state without exception.");
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cannot retrieve information from full node");
            return;
        }

        using var source = new SouceConnection(this.appSettings.LocalSqliteConnString);
        source.Open();
        using var target = new PgsqlTargetConnection(this.appSettings.OnlineDbConnString);
        await target.Open();
        var nodeProcessor = new LocalNodeProcessor(this.appSettings.LocalNodeProcessor);

        await SyncBlock(chain, target, nodeProcessor);

    }

    private async Task SyncBlock(SourceChain source, PgsqlTargetConnection target, LocalNodeProcessor nodeProcessor)
    {
        var batch = (uint)this.appSettings.SyncBlockBatchSize;
        var sourcePeak = (await source.GetChainState()).Peak.Height - 1; // ignore last block as it may be during writing phase
        var targetPeak = await target.GetLastBlockSyncHeight() + 1; // saved state is processed peak, we start from next one

        var number = sourcePeak - targetPeak;
        if (number > 0)
            this.logger.LogInformation($"sync blocks with coin puzzle and solution [{targetPeak}]~[{sourcePeak}](+{number}) [Batch: {batch}].");
        var swTotal = new Stopwatch();
        swTotal.Start();

        var current = (uint)targetPeak;
        while (current < sourcePeak)
        {
            var sw = new Stopwatch();
            sw.Start();
            var blocks = (await source.GetBlocks(current, batch)).ToArray();

            var tc = current;
            var lstCoins = new ConcurrentBag<CoinInfo>();
            var sw1 = new Stopwatch();
            var sw2 = new Stopwatch();
            var sw3 = new Stopwatch();

            //for (int i = 0; i < blocks.Length; i++)
            //{
            //    var block = blocks[i];
            //    var coins= await GetCoinsFromBlock(source, target, nodeProcessor,  block);
            //}

            //var coinTasks = blocks.Select(_ => GetCoinsFromBlock(source, target, nodeProcessor, _)).ToArray();
            var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
            await Parallel.ForEachAsync(blocks, options, async (_, ct) => (await GetCoinsFromBlock(source, target, nodeProcessor, _, sw3))
                .ToList()
                .ForEach(_ => lstCoins.Add(_)));

            this.logger.LogInformation($"GetCoinPuzzleAndSolution: {sw1.ElapsedMilliseconds}ms, ParsePuzzle: {sw2.ElapsedMilliseconds}ms, ParseBlock: {sw3.ElapsedMilliseconds}ms");

            var tget = sw.ElapsedMilliseconds;
            sw.Restart();
            var cs = lstCoins.OrderBy(_ => _.coinname).ToArray();
            var d = JsonSerializer.Serialize(cs, new JsonSerializerOptions { WriteIndented = true });

            if (lstCoins.Count > 0)
                await target.WriteCoinClassRecords(ConvertRecordsToTable(lstCoins));

            var bis = blocks.Select(_ => GetBlockInfo(_)).ToArray();
            await target.WriteBlockRecords(ConvertRecordsToTable(bis));

            sw.Stop();
            current = blocks.Max(_ => _.RewardChainBlock.Height);
            await target.WriteLastBlockSyncHeight(current);

            var estimatedRemain = swTotal.GetEta(current - targetPeak, sourcePeak - targetPeak);
            this.logger.LogInformation($"batch processed spent records [{tc}]~[{current}]," +
                $" estimated remain {estimatedRemain.TotalMinutes} minute(s)" +
                $" {tget} ms, {sw.ElapsedMilliseconds} ms, total {lstCoins.Count} coin(s).");

            current++;// saved state is processed block, next loop process from next block
        }
    }

    private static async Task<CoinInfo[]> GetCoinsFromBlock(SourceChain source, PgsqlTargetConnection target, LocalNodeProcessor nodeProcessor,
        chia.dotnet.FullBlock block,
        Stopwatch? sw3 = null)
    {
        if (block.TransactionsGenerator == null || block.TransactionsGenerator.Length == 0)
        {
            return Array.Empty<CoinInfo>();
        }

        List<CoinInfo> lstCoins = new();
        sw3?.Start();
        var ref_generaters = block.TransactionsGeneratorRefList.Count == 0
            ? Array.Empty<string>()
            : await Task.WhenAll(block.TransactionsGeneratorRefList
                .Select(async _ => (await source.GetBlocks(_, 1))
                    .FirstOrDefault()?.TransactionsGenerator));
        var result = await nodeProcessor.ParseBlock(block.TransactionsGenerator, ref_generaters);
        sw3?.Stop();
        lstCoins.AddRange(result);

        return lstCoins.ToArray();
    }

    private static async Task<CoinInfo[]> GetCoinsByApi(SourceChain source, PgsqlTargetConnection target,
        LocalNodeProcessor nodeProcessor, chia.dotnet.FullBlock block, ILogger logger, Stopwatch? sw1, Stopwatch? sw2)
    {
        List<CoinInfo> lstCoins = new();
        var coins = await target.GetCoinRemovalIndex(block.RewardChainBlock.Height);
        if (coins.Length > 0)
            logger.LogDebug($"block {block.RewardChainBlock.Height} is special, with {coins.Length} coin(s)");
        foreach (var coin in coins)
        {
            var coinname = HexBytes.FromBytes(coin.coin_name).Hex.ToLower();
            sw1?.Start();
            var detail = await source.GetCoinPuzzleAndSolution(coinname, (uint)coin.spent_index);
            sw1?.Stop();

            sw2?.Start();
            var result = await nodeProcessor.ParsePuzzle(detail.PuzzleReveal);
            sw2?.Stop();
            lstCoins.Add(new CoinInfo(
                detail.Coin.ParentCoinInfo, result, detail.Coin.Amount, detail.Solution, coinname.Prefix0x()));
        }

        return lstCoins.ToArray();
    }

    private DataTable ConvertRecordsToTable(IEnumerable<CoinInfo> records)
    {
        var dt = new DataTable();
        dt.Columns.Add("coin_name", typeof(byte[]));
        //dt.Columns.Add(nameof(CoinInfo.puzzle), typeof(byte[]));
        dt.Columns.Add(nameof(CoinInfo.puzzle), typeof(string));
        dt.Columns.Add(nameof(CoinInfo.solution), typeof(byte[]));

        foreach (var r in records)
        {
            var puz = JsonSerializer.Serialize(r.puzzle);
            //var puzbytes = Encoding.UTF8.GetBytes(puz);

            dt.Rows.Add(r.coinname.ToHexBytes(), puz, r.solution.ToHexBytes().Compress());
        }

        return dt;
    }

    private DataTable ConvertRecordsToTable(IEnumerable<BlockInfo> records)
    {
        var dt = new DataTable();
        dt.Columns.Add(nameof(BlockInfo.is_tx_block), typeof(bool));
        dt.Columns.Add(nameof(BlockInfo.index), typeof(long));
        dt.Columns.Add(nameof(BlockInfo.weight), typeof(long));
        dt.Columns.Add(nameof(BlockInfo.iterations), typeof(long));
        dt.Columns.Add(nameof(BlockInfo.cost), typeof(long));
        dt.Columns.Add(nameof(BlockInfo.fee), typeof(long));
        dt.Columns.Add(nameof(BlockInfo.generator), typeof(byte[]));
        dt.Columns.Add(nameof(BlockInfo.generator_ref_list), typeof(byte[]));
        //dt.Columns.Add(nameof(BlockInfo.block_info), typeof(byte[]));
        dt.Columns.Add(nameof(BlockInfo.block_info), typeof(string));

        foreach (var r in records)
        {
            //var bi = Compress(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            //    r.block_info with { TransactionsGenerator = "", TransactionsGeneratorRefList = Array.Empty<uint>() })));
            var bi = JsonSerializer.Serialize(
                r.block_info with { TransactionsGenerator = "", TransactionsGeneratorRefList = Array.Empty<uint>() });

            dt.Rows.Add(
                r.is_tx_block,
                r.index,
                (long)r.weight,
                (long)r.iterations,
                (long)r.cost,
                (long)r.fee,
                r.generator.Compress(),
                r.generator_ref_list,
                bi);
        }

        return dt;
    }

    private BlockInfo GetBlockInfo(chia.dotnet.FullBlock block)
    {
        return new BlockInfo(block.RewardChainBlock.IsTransactionBlock,
            block.RewardChainBlock.Height,
            block.RewardChainBlock.Weight,
            block.RewardChainBlock.TotalIters,
            block.TransactionsInfo?.Cost ?? 0,
            block.TransactionsInfo?.Fees ?? 0,
            block.TransactionsGenerator?.ToHexBytes() ?? Array.Empty<byte>(),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(block.TransactionsGeneratorRefList)),
            block);
    }
}
