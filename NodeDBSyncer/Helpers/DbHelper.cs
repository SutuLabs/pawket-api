namespace NodeDBSyncer.Helpers;

using System.ComponentModel;
using System.Data;
using System.Threading.Tasks;
using K4os.Compression.LZ4;
using Npgsql;

public static class DbHelper
{
    public static async Task<bool> CheckExistence(this NpgsqlConnection connection, string objName)
    {
        using var cmd = new NpgsqlCommand(@$"SELECT to_regclass('public.{objName}')", connection);
        cmd.AllResultTypesAreUnknown = true;
        var o = await cmd.ExecuteScalarAsync();
        return o is not DBNull;
    }

    public static async Task<bool> CheckColumnExistence(
        this NpgsqlConnection connection, string tableName, string columnName)
    {

        using var cmd = new NpgsqlCommand(
            @"SELECT count(*) FROM information_schema.columns WHERE table_name = @table AND column_name = @column", connection)
        {
            Parameters =
            {
                new("table", tableName),
                new("column", columnName),
            },
            AllResultTypesAreUnknown = true,
        };
        var o = await cmd.ExecuteScalarAsync();
        return (o is long l && l == 1) || (o is string s && s == "1");
    }

    public static string Join(this IEnumerable<NpgsqlParameter> pars, string prefix = "")
        => string.Join(",", pars.Select(_ => prefix + _.ParameterName));

    public static async Task Import(this NpgsqlConnection connection, DataTable dataTable, string tableName, CancellationToken ct = default)
    {
        var fields = string.Join(",", dataTable.Columns.OfType<DataColumn>().Select(_ => $"\"{_.ColumnName}\""));
        using var writer = connection.BeginBinaryImport($"COPY {tableName} ({fields}) FROM STDIN (FORMAT BINARY)");

        foreach (DataRow row in dataTable.Rows)
        {
            writer.WriteRow(row.ItemArray);
            //writer.StartRow();
            //writer.Write(row.ItemArray[0], NpgsqlTypes.NpgsqlDbType.Varchar);
            //writer.Write(row.ItemArray[1], NpgsqlTypes.NpgsqlDbType.Varchar);
            //writer.Write(row.ItemArray[2], NpgsqlTypes.NpgsqlDbType.Varchar);
            //writer.Write(row.ItemArray[3], NpgsqlTypes.NpgsqlDbType.Numeric);
            //writer.Write(row.ItemArray[4], NpgsqlTypes.NpgsqlDbType.Timestamp);
        }

        writer.Complete();
    }

    public static DataTable ConvertToDataTable<T>(this IEnumerable<T> data)
    {
        var properties = TypeDescriptor.GetProperties(typeof(T));
        DataTable table = new DataTable();
        foreach (PropertyDescriptor prop in properties)
            table.Columns.Add(prop.Name, Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType);
        foreach (T item in data)
        {
            DataRow row = table.NewRow();
            foreach (PropertyDescriptor prop in properties)
                row[prop.Name] = prop.GetValue(item) ?? DBNull.Value;
            table.Rows.Add(row);
        }
        return table;
    }

    public static byte[] Compress(this byte[] input)
    {
        if (input.Length == 0) return input;

        var output = LZ4Pickler.Pickle(input);
        ////if (new Random(DateTime.Now.Millisecond).NextDouble() < 0.01)
        ////    Console.WriteLine($"compressed {(double)output.Length / input.Length * 100:00.0}%, before: {input.Length}, after: {output.Length}");
        return output;
    }

    public static byte[] Decompress(this byte[] input)
    {
        if (input.Length == 0) return input;
        var output = LZ4Pickler.Unpickle(input);
        return output;
    }
}