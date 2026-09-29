using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using WalletServer.Helpers;
using Xunit;

namespace WalletBackend.MigrationTests;

public class ChiaCoinStoreSnapshotTests
{
    [Fact]
    public void LaterConfirmationsAndSpendsDoNotChangeAnEarlierPageSnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pawket-coin-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "chia.sqlite");
        var hash = "0x" + new string('a', 64);
        try
        {
            using (var setup = new SqliteConnection($"Data Source={path}"))
            {
                setup.Open();
                using var command = setup.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE database_version (version INTEGER);
                    INSERT INTO database_version VALUES (2);
                    CREATE TABLE coin_record (
                        coin_name BLOB PRIMARY KEY, coin_parent BLOB, puzzle_hash BLOB,
                        amount BLOB, confirmed_index INTEGER, spent_index INTEGER,
                        coinbase INTEGER, timestamp INTEGER);
                    CREATE TABLE hints (coin_id BLOB, hint BLOB);
                    INSERT INTO coin_record VALUES
                        (zeroblob(32), zeroblob(32), $hash, x'0000000000000001', 10, 0, 0, 10),
                        (x'0100000000000000000000000000000000000000000000000000000000000000',
                         zeroblob(32), $hash, x'0000000000000001', 9, 0, 0, 9);";
                command.Parameters.AddWithValue("$hash", Convert.FromHexString(hash[2..]));
                command.ExecuteNonQuery();
            }

            var store = new ChiaCoinStore(Options.Create(new AppSettings { ChiaDbPath = path }));
            using var firstConnection = store.Open();
            var first = Assert.Single(store.GetPage(firstConnection, hash, false, false,
                null, null, 10, 0, 1));

            using (var update = new SqliteConnection($"Data Source={path}"))
            {
                update.Open();
                using var command = update.CreateCommand();
                command.CommandText = @"
                    UPDATE coin_record SET spent_index=12 WHERE confirmed_index=9;
                    INSERT INTO coin_record VALUES
                        (x'0200000000000000000000000000000000000000000000000000000000000000',
                         zeroblob(32), $hash, x'0000000000000001', 11, 0, 0, 11);";
                command.Parameters.AddWithValue("$hash", Convert.FromHexString(hash[2..]));
                command.ExecuteNonQuery();
            }

            using var nextConnection = store.Open();
            var next = Assert.Single(store.GetPage(nextConnection, hash, false, false,
                null, null, 10, 0, 2, first.SortHeight, first.CoinId));
            Assert.Equal(9U, next.Record.ConfirmedBlockIndex);
            Assert.False(next.Record.Spent);
            Assert.Equal(0U, next.Record.SpentBlockIndex);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
