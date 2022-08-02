namespace NodeDBSyncer.Functions.ParseTx;

using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodeDBSyncer.Helpers;
using NodeDBSyncer.Services;
using WalletServer.Helpers;

internal class SyncBlockService : BaseRefreshService
{
    public const int Timeout = 3000;
    private readonly AppSettings appSettings;

    public SyncBlockService(
        ILogger<SyncBlockService> logger,
        IOptions<AppSettings> appSettings)
        : base(logger, nameof(SyncBlockService), 2, 15, Timeout)
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

        using var target = new ParseTxDbConnection(this.appSettings.OnlineDbConnString);
        await target.Open();

        var ret = await SyncBlock(chain, target, token);
        if (!ret) return;

        // for first time finish initialize, initialize the index
        if (!await target.CheckIndexExistence())
        {
            this.logger.LogInformation($"First finish initialization, starting index initialization");
            await target.InitializeIndex();
        }
    }

    private async Task<bool> SyncBlock(SourceChain source, ParseTxDbConnection target, CancellationToken token)
    {
        var batch = (uint)this.appSettings.SyncBlockBatchSize;
        var sourcePeak = (await source.GetChainState()).Peak.Height - 1; // ignore last block as it may be during writing phase
        var targetPeak = await target.GetLatestBlockSynced() + 1; // saved state is processed peak, we start from next one

        var number = sourcePeak - targetPeak;
        if (number > 0)
            this.logger.LogInformation($"sync blocks [{targetPeak}]~[{sourcePeak}](+{number}) [Batch: {batch}].");
        var swTotal = new Stopwatch();
        swTotal.Start();

        var current = (uint)targetPeak;
        while (current < sourcePeak)
        {
            var sw = new Stopwatch();
            sw.Start();
            var tc = current;
            var blocks = (await source.GetBlocks(current, batch, true, true, token)).ToArray();

            var bis = blocks.Select(_ => GetBlockInfo(_)).ToArray();
            var tget = sw.ElapsedMilliseconds;
            sw.Restart();
            await target.WriteBlockRecords(bis);

            sw.Stop();
            current = (uint)bis.Max(_ => _.index);

            var estimatedRemain = swTotal.GetEta(current - targetPeak, sourcePeak - targetPeak);
            this.logger.LogInformation($"batch processed blocks [{tc}]~[{current}]," +
                $" estimated remain {estimatedRemain.TotalMinutes:0.0} minute(s)," +
                $" this time cost {tget} + {sw.ElapsedMilliseconds} ms.");

            current++;// saved state is processed block, next loop process from next block

            if (token.IsCancellationRequested)
            {
                this.logger.LogInformation($"Sync block cancelled.");
                return false;
            }

            if (swTotal.ElapsedMilliseconds > Timeout * 1000 / 2)
            {
                this.logger.LogInformation($"Sync block timeout, continue later.");
                return false;
            }
        }

        return true;
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
            block.TransactionsGeneratorRefList.ToArray(),
            block);
    }
}