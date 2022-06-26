using System.Data;

namespace NodeDBSyncer
{
    public interface ITargetConnection : IDisposable
    {
        Task<long> GetTotalCoinRecords();
        Task Open();
        Task WriteCoinRecords(DataTable reader);
    }
}