using chia.dotnet;
using Microsoft.Extensions.Options;

namespace WalletServer.Helpers;

/// <summary>Chia-backed implementation for retained wallet read endpoints.</summary>
public sealed class ChiaWalletData : IDisposable
{
    private readonly HttpRpcClient rpc;
    private readonly FullNodeProxy node;
    public ChiaWalletData(IOptions<AppSettings> options)
    {
        var settings = options.Value;
        var path = settings.Path ?? "";
        rpc = new HttpRpcClient(new EndpointInfo
        {
            CertPath = path + "private_full_node.crt",
            KeyPath = path + "private_full_node.key",
            Uri = new Uri($"https://{settings.Host}:{settings.Port}/"),
        });
        node = new FullNodeProxy(rpc, "client");
    }

    public async Task<long> GetPeakHeight() => (await node.GetBlockchainState()).Peak?.Height ?? 0;

    public async Task<CoinPuzzleInfo[]> GetParentPuzzle(string coinId, uint? startHeight, uint? endHeight)
    {
        var records = await node.GetCoinRecordsByNames(new[] { coinId }, true, startHeight, endHeight);
        var record = records.FirstOrDefault(c => ChiaCoinId.FromCoin(c.Coin).Equals(
            coinId.Unprefix0x(), StringComparison.OrdinalIgnoreCase));
        if (record == null || !record.Spent) return Array.Empty<CoinPuzzleInfo>();
        var spend = await node.GetPuzzleAndSolution(coinId, record.SpentBlockIndex);
        if (string.IsNullOrEmpty(spend.PuzzleReveal)) return Array.Empty<CoinPuzzleInfo>();
        return new[] { new CoinPuzzleInfo(coinId, record.Coin.Amount, record.Coin.ParentCoinInfo, spend.PuzzleReveal) };
    }

    public async Task<CoinDetail[]> GetCoinDetails(string[] coinIds, uint? startHeight, uint? endHeight,
        long? pageStart, int? pageLength)
    {
        var records = (await node.GetCoinRecordsByNames(coinIds, true, startHeight, endHeight))
            .OrderByDescending(c => Math.Max(c.SpentBlockIndex, c.ConfirmedBlockIndex))
            .Skip(checked((int)Math.Max(0, pageStart ?? 0)))
            .Take(Math.Max(0, pageLength ?? 100));
        var details = new List<CoinDetail>();
        foreach (var coin in records)
        {
            CoinSpend? spend = coin.Spent
                ? await node.GetPuzzleAndSolution(ChiaCoinId.FromCoin(coin.Coin), coin.SpentBlockIndex) : null;
            details.Add(new CoinDetail(coin.Coin.Amount, coin.ConfirmedBlockIndex, coin.SpentBlockIndex,
                coin.Timestamp, coin.Coin.ParentCoinInfo, coin.Coin.PuzzleHash,
                spend?.PuzzleReveal, spend?.Solution));
        }
        return details.ToArray();
    }

    public async Task<GetBlockResponse> GetBlock(int[] indexes)
    {
        var peak = await GetPeakHeight();
        var blocks = new List<BlockTransactionGeneratorRetrieval>();
        foreach (var height in indexes.Distinct().Where(i => i >= 0 && i <= peak).OrderByDescending(i => i))
        {
            var result = await node.GetBlocks((uint)height, (uint)height + 1);
            blocks.AddRange(result.Select(ToBlock));
        }
        var refs = new List<BlockTransactionGeneratorRetrieval>();
        foreach (var height in blocks.SelectMany(b => b.generator_ref_list ?? Array.Empty<uint>()).Distinct().OrderBy(i => i))
        {
            var result = await node.GetBlocks(height, height + 1);
            refs.AddRange(result.Select(ToBlock));
        }
        return new GetBlockResponse(blocks.ToArray(), refs.ToArray());
    }

    private static BlockTransactionGeneratorRetrieval ToBlock(FullBlock block) => new(
        block.RewardChainBlock.Height,
        string.IsNullOrEmpty(block.TransactionsGenerator) ? Array.Empty<byte>() : block.TransactionsGenerator.ToHexBytes(),
        block.TransactionsGeneratorRefList.ToArray());

    public void Dispose() => rpc.Dispose();
}
