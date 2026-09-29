using chia.dotnet;
using Microsoft.Extensions.Options;

namespace WalletServer.Helpers;

/// <summary>Chia-backed implementation for retained wallet read endpoints.</summary>
public sealed class ChiaWalletData : IDisposable
{
    private readonly HttpRpcClient rpc;
    private readonly FullNodeProxy node;
    private readonly CoinClassCache classes;

    public ChiaWalletData(IOptions<AppSettings> options, CoinClassCache classes)
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
        this.classes = classes;
    }

    public async Task<long> GetPeakHeight() => (await node.GetBlockchainState()).Peak?.Height ?? 0;

    public async Task<CoinRecordWithAnalysis[]> GetCoins(string hash, bool includeSpent,
        bool hint, CoinClassType? coinType, uint? startHeight, uint? endHeight,
        long? pageStart, int? pageLength)
    {
        var source = coinType != null || hint
            ? await node.GetCoinRecordsByHint(hash, includeSpent, startHeight, endHeight)
            : await node.GetCoinRecordsByPuzzleHash(hash, includeSpent, startHeight, endHeight);
        var rows = new List<CoinRecordWithAnalysis>();
        foreach (var coin in source)
        {
            string? analysis = null;
            if (coinType is CoinClassType type)
            {
                var parent = await node.GetCoinRecordByName(coin.Coin.ParentCoinInfo);
                if (!parent.Spent) continue;
                var block = await node.GetBlockRecordByHeight(parent.SpentBlockIndex);
                var classified = await classes.GetOrAnalyzeAsync(node, parent, block.HeaderHash);
                if (!Matches(type, classified.Mods)) continue;
                analysis = classified.Analysis;
            }
            rows.Add(new CoinRecordWithAnalysis
            {
                Coin = coin.Coin,
                Coinbase = coin.Coinbase,
                ConfirmedBlockIndex = coin.ConfirmedBlockIndex,
                Spent = coin.Spent,
                SpentBlockIndex = coin.SpentBlockIndex,
                Timestamp = coin.Timestamp,
                Analysis = analysis,
            });
        }
        return rows.OrderByDescending(c => Math.Max(c.SpentBlockIndex, c.ConfirmedBlockIndex))
            .Skip(checked((int)Math.Max(0, pageStart ?? 0)))
            .Take(Math.Max(0, pageLength ?? 100)).ToArray();
    }

    public async Task<FullBalanceInfo> GetBalance(string hash)
    {
        var records = await node.GetCoinRecordsByPuzzleHash(hash, true, null, null);
        long unspentAmount = 0, spentAmount = 0;
        int unspentCount = 0, spentCount = 0;
        foreach (var record in records.Where(c => c.Coin.Amount > 0))
        {
            if (record.Spent)
            {
                spentAmount = checked(spentAmount + (long)record.Coin.Amount);
                spentCount++;
            }
            else
            {
                unspentAmount = checked(unspentAmount + (long)record.Coin.Amount);
                unspentCount++;
            }
        }
        return new FullBalanceInfo(unspentAmount, unspentCount, spentAmount, spentCount);
    }

    public async Task<CoinPuzzleInfo[]> GetParentPuzzle(string coinId, uint? startHeight, uint? endHeight)
    {
        var records = await node.GetCoinRecordsByNames(new[] { coinId }, true, startHeight, endHeight);
        var record = records.FirstOrDefault(c => c.Coin.Name.Equals(coinId, StringComparison.OrdinalIgnoreCase));
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
                ? await node.GetPuzzleAndSolution(coin.Coin.Name, coin.SpentBlockIndex) : null;
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

    private static bool Matches(CoinClassType type, string mods) => type switch
    {
        CoinClassType.CatV2 => mods is "cat_v2()" or "cat_v2(p2_delegated_puzzle_or_hidden_puzzle())"
            or "cat_v2(settlement_payments())" or "cat_v2(settlement_payments_v1())",
        CoinClassType.DidV1 => mods == "singleton_top_layer_v1_1(did_innerpuz(p2_delegated_puzzle_or_hidden_puzzle()))",
        CoinClassType.NftV1 => mods is
            "singleton_top_layer_v1_1(nft_state_layer(nft_ownership_layer(nft_ownership_transfer_program_one_way_claim_with_royalties(),p2_delegated_puzzle_or_hidden_puzzle())))"
            or "singleton_top_layer_v1_1(nft_state_layer(nft_ownership_layer(nft_ownership_transfer_program_one_way_claim_with_royalties(),settlement_payments())))"
            or "singleton_top_layer_v1_1(nft_state_layer(nft_ownership_layer(nft_ownership_transfer_program_one_way_claim_with_royalties(),settlement_payments_v1())))",
        _ => false,
    };

    public void Dispose() => rpc.Dispose();
}
