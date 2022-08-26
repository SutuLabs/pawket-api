using System.Numerics;

namespace NodeDBSyncer.Functions.ParseTx;

public record CoinRemovalIndex(
    byte[] coin_name,
    long spent_index);

public record CoinInfoForStorage(byte[] coin_name, byte[] puzzle, string parsed_puzzle, byte[] solution, string mods, string analysis);
public record CoinInfoJson(string parent, string puzzle, PuzzleArg parsed_puzzle, string amount, string solution, string coin_name, string mods, string analysis);

public record AnalysisUpdateEntity(long id, string analysis);
public record UnanalyzedTx(long id, string coin_name, string puzzle, string solution, long amount, string coin_parent, string puzzle_hash);

public record BlockInfo(
    bool is_tx_block,
    ulong index,
    BigInteger weight,
    BigInteger iterations,
    BigInteger cost,
    BigInteger fee,
    byte[] generator,
    uint[] generator_ref_list,
    chia.dotnet.FullBlock block_info);