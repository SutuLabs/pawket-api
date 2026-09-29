using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WalletServer.Controllers;

namespace WalletServer.Helpers;

/// <summary>Append-only push diagnostics; logging failure must not affect the RPC result.</summary>
public sealed class FilePushLog
{
    [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
    private static extern int Chmod(string path, int mode);

    private readonly string path;
    private readonly long maxBytes;
    private readonly int maxFiles;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public FilePushLog(IOptions<AppSettings> options)
    {
        path = Path.GetFullPath(options.Value.PushLogPath);
        maxBytes = Math.Max(1024, options.Value.PushLogMaxBytes);
        maxFiles = Math.Max(2, options.Value.PushLogMaxFiles);
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
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoded = Encoding.UTF8.GetBytes(line);
            if (File.Exists(path) && new FileInfo(path).Length + encoded.Length > maxBytes)
                Rotate();
            await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            if (!OperatingSystem.IsWindows() && Chmod(path, 0x180) != 0) // 0600
                throw new IOException($"Unable to restrict push log permissions: {Marshal.GetLastWin32Error()}");
            await file.WriteAsync(encoded);
            await file.FlushAsync();
        }
        finally
        {
            writeGate.Release();
        }
    }

    private void Rotate()
    {
        for (var index = maxFiles - 1; index >= 1; index--)
        {
            var destination = path + "." + index;
            var source = index == 1 ? path : path + "." + (index - 1);
            if (File.Exists(destination)) File.Delete(destination);
            if (File.Exists(source)) File.Move(source, destination);
        }
    }
}
