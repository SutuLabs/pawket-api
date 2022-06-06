public class AppSettings
{
    public string? OnlineDbConnString { get; set; }
    public string? LocalSqliteConnString { get; set; }
    public int SyncInsertBatchSize { get; set; } = 10000;
    public int SyncBatchCount { get; set; } = 100;
    public int SyncUpdateBatchSize { get; set; } = 10000;
}
