namespace NodeDBSyncer.Functions.Price;

using System.Data;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Npgsql;
using NodeDBSyncer.Helpers;

public class PersistentHelper : PgsqlConnection
{
    private const string PriceTableName = "series_price";

    private readonly AppSettings appSettings;

    public PersistentHelper(IOptions<AppSettings> appSettings)
        : base(appSettings.Value.OnlineDbConnString ?? throw new ArgumentNullException("db connection cannot be null"))
    {
        this.appSettings = appSettings.Value;
    }

    public override async Task Open()
    {
        await base.Open();
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
        await this.connection.EnsureOpen();
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
}