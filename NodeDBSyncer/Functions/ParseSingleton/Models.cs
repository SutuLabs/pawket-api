namespace NodeDBSyncer.Functions.ParseSingleton;

public record SingletonRecordInfo(
    long last_coin_class_id,
    string singleton_coin_name,
    int singleton_create_index,
    string bootstrap_coin_name,
    string? creator_puzzle_hash,
    string? creator_did,
    string? type);

public record SingletonHistoryInfo(
    long coin_class_id,
    string singleton_coin_name,
    string this_coin_name,
    int this_coin_spent_index,
    string next_coin_name,
    string p2_owner,
    string did_owner,
    string? type);