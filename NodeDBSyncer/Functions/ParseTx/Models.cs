using System.Numerics;

namespace NodeDBSyncer.Functions.ParseTx;

public record CoinRemovalIndex(
    byte[] coin_name,
    long spent_index);

public record CoinInfo(string parent, PuzzleArg puzzle, ulong amount, string solution, string coinname);
public record CoinInfoJson(string parent, PuzzleArg puzzle, string amount, string solution, string coinname);

public record PuzzleArg(string? mod, PuzzleArg[]? args, string? raw);

public record BlockInfo(
    bool is_tx_block,
    ulong index,
    BigInteger weight,
    BigInteger iterations,
    BigInteger cost,
    BigInteger fee,
    byte[] generator,
    byte[] generator_ref_list,
    chia.dotnet.FullBlock block_info);

