namespace NodeDBSyncer.Functions.ParseTx;

using System.Buffers.Binary;
using System.Threading.Tasks;
using chia.dotnet;
using Microsoft.Data.Sqlite;

public class SourceChain : IDisposable
{
    private bool disposedValue;
    private readonly HttpRpcClient rpcClient;
    private readonly FullNodeProxy nodeClient;

    public SourceChain(EndpointInfo endpoint)
    {

        this.rpcClient = new HttpRpcClient(endpoint);

        this.nodeClient = new FullNodeProxy(rpcClient, "client");
    }

    public async Task<BlockchainState?> GetChainState()
    {
        var state = await nodeClient.GetBlockchainState();
        return state;
    }

    public async Task<FullBlock[]> GetBlocks(
        uint start, uint number, bool excludeHeaderhash = false, bool excludeReorged = false, CancellationToken cancellationToken = default)
    {
        return (await nodeClient.GetBlocks(start, start + number, excludeHeaderhash, excludeReorged, cancellationToken)).ToArray();
    }

    public async Task<CoinRecord[]> GetCoins(
        string headerhash, CancellationToken cancellationToken = default)
    {
        return (await nodeClient.GetAdditionsAndRemovals(headerhash, cancellationToken)).Removals.ToArray();
    }

    public async Task<CoinSpend> GetCoinPuzzleAndSolution(
        string coinId, uint height, CancellationToken cancellationToken = default)
    {
        return await nodeClient.GetPuzzleAndSolution(coinId, height, cancellationToken);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                this.rpcClient.Dispose();
            }

            disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
