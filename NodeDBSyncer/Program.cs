using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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

var appSettings = GetService<IOptions<AppSettings>>().Value;
if (string.IsNullOrWhiteSpace(appSettings.LocalNodeProcessor))
{
    Console.Error.WriteLine("Mandatory LocalNodeProcessor is not configured.");
    throw new SystemException("Mandatory LocalNodeProcessor is not configured.");
}

try
{
    var nodeProcessor = new LocalNodeProcessor(appSettings.LocalNodeProcessor);
    var version = await nodeProcessor.GetVersion();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"LocalNodeProcessor is unable to connect due to: {ex.Message}.");
    throw new SystemException($"LocalNodeProcessor is unable to connect due to: {ex.Message}.");
}

await GetService<PersistentHelper>().Open();
await GetService<PushLogHelper>().Open();

await app.RunAsync();

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
