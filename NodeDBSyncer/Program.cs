using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodeDBSyncer.Functions.ParseSingleton;
using NodeDBSyncer.Functions.ParseTx;
using NodeDBSyncer.Functions.Price;
using NodeDBSyncer.Functions.SyncCoin;
using NodeDBSyncer.Helpers;

var builder = Host.CreateDefaultBuilder(args);

var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .AddJsonFile($"appsettings.{environmentName}.json", true, true)
    .AddEnvironmentVariables()
    .Build();

// Add services to the container.
builder.ConfigureServices(services =>
{
    services.Configure<AppSettings>(config.GetSection(nameof(AppSettings)));
    services.AddHostedService<SyncDbService>();
    services.AddSingleton<PersistentHelper>();
    services.AddSingleton<PushLogHelper>();
    services.AddHostedService<RefreshPriceService>();
    services.AddHostedService<SyncBlockService>();
    services.AddHostedService<ParseBlockTxService>();
    services.AddHostedService<AnalyzeTxService>();
    services.AddHostedService<ParseSingletonService>();
});

var app = builder.Build();

T GetService<T>() where T : class
{
    if (app == null)
    {
        Console.Error.WriteLine("abnormal state, cannot get app");
        throw new SystemException("abnormal state, cannot get app");
    }

    if (app.Services.GetService(typeof(T)) is not T ph)
    {
        Console.Error.WriteLine("abnormal state, cannot get service");
        throw new SystemException("abnormal state, cannot get service");
    }

    return ph;
}

await GetService<PersistentHelper>().Open();
await GetService<PushLogHelper>().Open();

await app.RunAsync();
