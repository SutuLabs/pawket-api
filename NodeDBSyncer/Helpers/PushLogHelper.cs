namespace NodeDBSyncer.Helpers;

using System.Data;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

public class PushLogHelper : IDisposable
{
    private const string PushLogTableName = "push_log";

    private readonly NpgsqlConnection connection;
    private readonly AppSettings appSettings;
    private readonly ILogger<PushLogHelper> logger;
    private bool disposedValue;

    public PushLogHelper(IOptions<AppSettings> appSettings, ILogger<PushLogHelper> logger)
    {
        this.appSettings = appSettings.Value;
        connection = new NpgsqlConnection(this.appSettings.OnlineDbConnString);
        connection.Open();
        this.logger = logger;
    }

    public async Task Open()
    {
        if (!await this.connection.CheckExistence(PushLogTableName))
        {
            await this.InitializeDatabase();
        }
    }

    public async Task LogPushes(params PushLogEntity[] logs)
    {
        var dataTable = logs.ConvertToDataTable();
        await this.connection.Import(dataTable, PushLogTableName);
    }

    private async Task InitializeDatabase()
    {
        using var cmd = new NpgsqlCommand(@$"
CREATE TABLE public.{PushLogTableName}
(
    id serial NOT NULL,
    ""{nameof(PushLogEntity.bundle)}"" bytea NOT NULL,
    ""{nameof(PushLogEntity.ip)}"" inet,
    ""{nameof(PushLogEntity.txid)}"" text,
    ""{nameof(PushLogEntity.status)}"" integer NOT NULL,
    ""{nameof(PushLogEntity.time)}"" timestamp NOT NULL,
    ""{nameof(PushLogEntity.error)}"" text,
    ""{nameof(PushLogEntity.parsed)}"" json,
    PRIMARY KEY (id)
);

ALTER TABLE IF EXISTS public.{PushLogTableName}
    OWNER to postgres;
", connection);

        try
        {
            await cmd.ExecuteNonQueryAsync();
            this.logger.LogInformation($"db of push log initialized");
        }
        catch (PostgresException pex)
        {
            this.logger.LogWarning($"Failed to execute table creation script due to [{pex.Message}], you may want to execute it yourself, here it is: \n" + cmd.CommandText);
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

public record PushLogEntity(byte[] bundle, IPAddress ip, string? txid, int status, DateTime time,string? error=null, string? parsed = null);