using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodeDBSyncer;
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
    services.AddHostedService<RefreshPriceService>();
    //services.AddHostedService<ParseBlockTxService>();
});

var app = builder.Build();

var ph = app.Services.GetService(typeof(PersistentHelper)) as PersistentHelper;
if (ph == null)
{
    Console.Error.WriteLine("abnormal state, cannot get service");
    return;
}

await ph.Open();

await app.RunAsync();
