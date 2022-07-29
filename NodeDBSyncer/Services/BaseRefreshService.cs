namespace NodeDBSyncer.Services;

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public abstract class BaseRefreshService : IHostedService, IDisposable
{
    private Timer? timer;
    private bool isRunning;
    protected readonly ILogger logger;

    public BaseRefreshService(
        ILogger<BaseRefreshService> logger,
        string serviceName,
        int delayStartSeconds,
        int intervalSeconds,
        int timeoutSeconds)
    {
        this.logger = logger;
        this.ServiceName = serviceName;
        this.DelayStartSeconds = delayStartSeconds;
        this.IntervalSeconds = intervalSeconds;
        this.TimeoutSeconds = timeoutSeconds;
    }

    public string ServiceName { get; }
    public int DelayStartSeconds { get; }
    public int IntervalSeconds { get; }
    public int TimeoutSeconds { get; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var seconds = this.IntervalSeconds;
        if (seconds == 0) return Task.CompletedTask;

        if (seconds < 0) throw new ArgumentOutOfRangeException(nameof(IntervalSeconds), "must > 0");

        logger.LogInformation($"{ServiceName} refresh Service is starting, set to refresh per [{seconds}]s");

        timer = new Timer(LoopDoWork, null, TimeSpan.FromSeconds(DelayStartSeconds), TimeSpan.FromSeconds(seconds));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogDebug($"{ServiceName} refresh service is stopping.");

        timer?.Change(Timeout.Infinite, 0);

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        timer?.Dispose();
    }

    protected abstract Task DoWorkAsync(CancellationToken token);

    private async void LoopDoWork(object? state)
    {
        if (this.isRunning) return;
        try
        {
            this.isRunning = true;
            var sw = new Stopwatch();
            sw.Start();
            using var cts = new CancellationTokenSource();
            var work = DoWorkAsync(cts.Token);
            var finishWork = await Task.WhenAny(work, Task.Delay(this.TimeoutSeconds * 1000));
            sw.Stop();
            if (finishWork == work)
            {
                if (finishWork.Status == TaskStatus.RanToCompletion)
                {
                    logger.LogInformation($"{ServiceName} refreshed, {sw.ElapsedMilliseconds}ms elapsed.");
                }
                else if (finishWork.Status == TaskStatus.Faulted)
                {
                    logger.LogInformation($"{ServiceName} failed to refresh due to exception, ex: {(finishWork.Exception?.InnerException != null ? finishWork.Exception.InnerException.ToString() : finishWork.Exception.ToString())}");
                    cts.Cancel();
                }
                else
                {
                    logger.LogInformation($"{ServiceName} failed to refresh due to unexpected status [{finishWork.Status}].");
                    cts.Cancel();
                }
            }
            else
            {
                logger.LogInformation($"{ServiceName} failed to refresh due to timeout, {sw.ElapsedMilliseconds}ms elapsed.");
                cts.Cancel();
            }
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "error when doing work");
        }
        finally
        {
            this.isRunning = false;
        }
    }
}