namespace NodeDBSyncer.Helpers;

using System.Data;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Npgsql;

public class PersistentHelper : IDisposable
{
    private const string PriceTableName = "series_price";

    private readonly NpgsqlConnection connection;
    private readonly AppSettings appSettings;
    private bool disposedValue;

    public PersistentHelper(IOptions<AppSettings> appSettings)
    {
        this.appSettings = appSettings.Value;
        connection = new NpgsqlConnection(this.appSettings.OnlineDbConnString);
    }

    public async Task Open()
    {
        connection.Open();
        if (!await this.CheckTableExistence())
        {
            await this.InitializeDatabase();
        }
    }

    private NpgsqlParameter[] GetPriceParameters(PriceEntity? price = null) => new NpgsqlParameter[]
    {
        new NpgsqlParameter(nameof(PriceEntity.source), price?.source),
        new NpgsqlParameter(nameof(PriceEntity.from), price?.from),
        new NpgsqlParameter(nameof(PriceEntity.to), price?.to),
        new NpgsqlParameter(nameof(PriceEntity.price), price?.price),
        new NpgsqlParameter(nameof(PriceEntity.time), price?.time),
    };

    public async Task PersistentPrice(PriceEntity[] prices)
    {
        //await this.CheckConnection();
        //var pars = GetPriceParameters(price);
        //using var cmd = new NpgsqlCommand(GenerateInsertSql(PriceTableName, pars), connection);
        //cmd.Parameters.AddRange(pars);

        //static string GenerateInsertSql(string tableName, IEnumerable<NpgsqlParameter> pars)
        //{
        //    return $"INSERT INTO public.{tableName} ({pars.Join()}) VALUES ({pars.Join(prefix: "@")});";
        //}
        //await cmd.ExecuteNonQueryAsync();
        var dataTable = prices.ConvertToDataTable();
        await this.connection.Import(dataTable, PriceTableName);
    }

    private async Task<bool> CheckTableExistence() => await this.connection.CheckExistence(PriceTableName);

    private async Task InitializeDatabase()
    {
        using var cmd = new NpgsqlCommand(@$"
CREATE TABLE public.{PriceTableName}
(
    id serial NOT NULL,
    ""{nameof(PriceEntity.source)}"" text NOT NULL,
    ""{nameof(PriceEntity.from)}"" text NOT NULL,
    ""{nameof(PriceEntity.to)}"" text NOT NULL,
    ""{nameof(PriceEntity.price)}"" decimal NOT NULL,
    ""{nameof(PriceEntity.time)}"" timestamp NOT NULL,
    PRIMARY KEY (id)
);

ALTER TABLE IF EXISTS public.{PriceTableName}
    OWNER to postgres;
", connection);

        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException pex)
        {
            Console.WriteLine($"Failed to execute table creation script due to [{pex.Message}], you may want to execute it yourself, here it is:");
            Console.WriteLine(cmd.CommandText);
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                this.connection.Dispose();
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