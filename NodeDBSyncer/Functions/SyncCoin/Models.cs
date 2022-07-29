namespace NodeDBSyncer.Functions.SyncCoin;

public record CoinRecord(
    long id,
    byte[] coin_name,
    long confirmed_index,
    long spent_index,
    bool coinbase,
    byte[] puzzle_hash,
    byte[] coin_parent,
    ulong amount,
    long timestamp);

public record HintRecord(
    long id,
    byte[] coin_name,
    byte[] hint);

public record SyncState(long spentHeight);

public record CoinSpentRecord(
    byte[] coin_name,
    long spent_index);

public record FullBlockRecord(
    byte[] header_hash,
    byte[] prev_hash,
    long height,
    byte[]? sub_epoch_summary,
    bool is_fully_compactified,
    byte[] block,
    byte[] block_record);
