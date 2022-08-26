public class AppSettings
{
    public string? OnlineDbConnString { get; set; }
    public string? LocalSqliteConnString { get; set; }
    public string? LocalNodeProcessor { get; set; }
    public string? NodeCertPath { get; set; }
    public string? NodeKeyPath { get; set; }
    public string? NodeUri { get; set; }
    public int SyncInsertBatchSize { get; set; } = 10000;
    public int SyncBatchCount { get; set; } = 100;
    public int SyncUpdateBatchSize { get; set; } = 10000;
    public int SyncBlockBatchSize { get; set; } = 200;
    public int ParsingTxBlockBatchSize { get; set; } = 100;
    public int AnalyzingTxBatchSize { get; set; } = 0;
    public string? PriceProxy { get; set; }
    public string? PriceSource { get; set; }
}
