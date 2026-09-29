using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WalletServer.Controllers;
using WalletServer.Helpers;
using Xunit;

namespace WalletBackend.MigrationTests;

public class FilePushLogTests
{
    [Fact]
    public async Task PushDiagnosticIsAppendedToFileWithoutDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pawket-push-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "pushtx.jsonl");
            var log = new FilePushLog(Options.Create(new AppSettings { PushLogPath = path }));
            var bundle = new WalletController.SpendBundleReq("signature", Array.Empty<WalletController.CoinSpendReq>());
            await log.AppendAsync(bundle, "127.0.0.1", null, 3,
                new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc), "sample error");
            var line = Assert.Single(File.ReadAllLines(path));
            using var record = JsonDocument.Parse(line);
            Assert.Equal(3, record.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("sample error", record.RootElement.GetProperty("error").GetString());
            var bytes = Convert.FromBase64String(record.RootElement.GetProperty("bundleGzipBase64").GetString()!);
            using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            using var original = JsonDocument.Parse(await reader.ReadToEndAsync());
            Assert.Equal("signature", original.RootElement.GetProperty("aggregated_signature").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
