namespace NodeDBSyncer.Functions.ParseInscription;

public record InscriptionRecordInfo(
    long coin_class_id,
    long spent_index,
    int serial,
    int coin_index,
    string coin_name,
    string parent,
    string from,
    string to,
    string p,
    string op,
    string tick,
    long amt,
    long? from_balance,
    long? to_balance,
    string? meta,
    bool? valid);

public record InscriptionRecordRaw(
    long coin_class_id,
    long spent_index,
    int serial,
    string? analysis);

public record TickInfo(
    string coin_name,
    long index,
    string tick,
    long max,
    long lim,
    string info);

public record JsonP2InscriptionCoin(
    string coinId,
    string from,
    string to,
    string parent,
    string? raw,
    JsonInscriptionEntity? meta);

public record JsonInscriptionEntity(
    string p,
    string op,
    string tick,
    long? amt,
    long? lim,
    long? max);

public record JsonP2InscriptionCoinAnalysisResult(JsonP2InscriptionCoin[] coins);

public record JsonDeployMeta(long lim, long max);