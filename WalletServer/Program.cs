using NodeDBSyncer.Helpers;
using Prometheus;
using WalletServer.Helpers;

var metricServer = new KestrelMetricServer(port: 5888);
metricServer.Start();

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
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
builder.Services.AddScoped<PushLogHelper>();
builder.Services.AddSingleton<OnlineCounter>();

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

app.Run();
