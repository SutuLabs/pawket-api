using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WalletServer.Controllers;

namespace WalletServer.Helpers;

/// <summary>Append-only push diagnostics; logging failure must not affect the RPC result.</summary>
public sealed class FilePushLog
{
    private readonly string path;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public FilePushLog(IOptions<AppSettings> options)
    {
        path = Path.GetFullPath(options.Value.PushLogPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public async Task AppendAsync(WalletController.SpendBundleReq bundle, string ip, string? txid,
        int status, DateTime time, string? error)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(bundle);
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            await gzip.WriteAsync(bytes);
        var line = JsonSerializer.Serialize(new
        {
            time,
            ip,
            txid,
            status,
            error,
            bundleGzipBase64 = Convert.ToBase64String(compressed.ToArray()),
        }) + "\n";

        await writeGate.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(path, line, Encoding.UTF8);
        }
        finally
        {
            writeGate.Release();
        }
    }
}
