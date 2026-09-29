using Microsoft.AspNetCore.Diagnostics;
using NodeDBSyncer.Helpers;
using Npgsql;
using Prometheus;
using System.Text.Json.Serialization;
using WalletServer.Helpers;
using static System.Net.Mime.MediaTypeNames;

var metricServer = new KestrelMetricServer(port: 5888);
metricServer.Start();

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
#if DEBUG
builder.Services.AddSwaggerGen();
#endif
builder.Services.AddMemoryCache();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(builder =>
    {
        builder
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowAnyOrigin();
    });
});
builder.Services.Configure<AppSettings>(builder.Configuration.GetSection(nameof(AppSettings)));
builder.Services.AddScoped<DataAccess>();
builder.Services.AddScoped<NameResolvingService>();
builder.Services.AddScoped<InscriptionCacheService>();
builder.Services.AddSingleton<FilePushLog>();
builder.Services.AddSingleton<PriceCacheService>();
builder.Services.AddSingleton<CoinClassCache>();
builder.Services.AddScoped<ChiaWalletData>();
builder.Services.AddSingleton<OnlineCounter>();

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});


var app = builder.Build();

#if DEBUG
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
#endif

// Configure the HTTP request pipeline.
app.UseAuthorization();
app.UseCors();

app.MapControllers();
app.UseHttpMetrics();

app.UseResponseCompression();

var logger = app.Services.GetRequiredService<ILogger<Program>>();

app.UseExceptionHandler(exceptionHandlerApp =>
{
    exceptionHandlerApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = Text.Plain;

        await context.Response.WriteAsync("Server internal error.");

        var exHandler = context.Features.Get<IExceptionHandlerPathFeature>();

        if (exHandler?.Error is BadHttpRequestException bex)
        {
            logger.LogWarning($"Bad request received: {bex.Message}");
        }
        else if (exHandler?.Error is NpgsqlException nex)
        {
            if (nex.InnerException is TimeoutException tex)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                logger.LogWarning($"timeout executing pg sql: {tex.Message}");
            }
            else
            {
                logger.LogWarning($"Pg unhandled exception: {nex.Message}");
            }
        }
        else
        {
            logger.LogError(exHandler?.Error, $"Unhandled exception for {exHandler?.Path}");
        }
    });
});

app.Run();
