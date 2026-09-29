using chia.dotnet;

namespace WalletServer.Helpers;

public record FullBalanceInfo(long Amount, int CoinCount, long SpentAmount, int SpentCount);

public record CoinDetail(
    ulong Amount,
    ulong ConfirmedIndex,
    ulong SpentIndex,
    ulong Timestamp,
    string ParentCoinInfo,
    string PuzzleHash,
    string? PuzzleReveal,
    string? Solution);

public record CoinPuzzleInfo(string CoinName, ulong Amount, string ParentCoinName, string PuzzleReveal);

public record GetBlockResponse(BlockTransactionGeneratorRetrieval[] Blocks, BlockTransactionGeneratorRetrieval[] RefBlocks);
public record BlockTransactionGeneratorRetrieval(ulong index, byte[] generator, uint[]? generator_ref_list);
