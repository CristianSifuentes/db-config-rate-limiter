using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Hosting;

namespace App.ConfigCatalog.Infrastructure.Services;

public sealed class RateLimitConfigWarmupHostedService : IHostedService, IDisposable
{
    private readonly RateLimitConfigAccessor _accessor;
    private readonly TelemetryClient _telemetry;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public RateLimitConfigWarmupHostedService(RateLimitConfigAccessor accessor, TelemetryClient telemetry)
    {
        _accessor = accessor;
        _telemetry = telemetry;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null) return;

        try
        {
            _cts.Cancel();
            if (_loop is not null)
                await _loop.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        await SafeRefresh(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
                await SafeRefresh(stoppingToken);
            }
            catch (OperationCanceledException) { }
        }
    }

    private async Task SafeRefresh(CancellationToken ct)
    {
        try
        {
            await _accessor.RefreshAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _telemetry.TrackException(ex);
        }
    }
}
