using System.Data;

namespace NodeDBSyncer
{
    public interface ITargetConnection : IDisposable
    {
        Task<long> GetTotalCoinRecords();
        void Open();
        Task WriteCoinRecords(DataTable reader);
    }
}