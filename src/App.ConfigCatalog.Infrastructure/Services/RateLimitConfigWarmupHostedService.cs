
using App.ConfigCatalog.Domain;
using App.ConfigCatalog.Infrastructure.Services;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
namespace SPARK.Domain.Services;

public sealed class RateLimitConfigWarmupHostedService : BackgroundService
{
    private readonly RateLimitConfigAccessor _accessor;
    private readonly TelemetryClient _telemetry;
    private readonly IOptionsMonitor<RateLimitConfigWarmupOptions> _options;

    // In .NET 8 you can use TimeProvider for testability.
    private readonly TimeProvider _time;
    private readonly Random _rng = new();

    public RateLimitConfigWarmupHostedService(
        RateLimitConfigAccessor accessor,
        TelemetryClient telemetry,
        IOptionsMonitor<RateLimitConfigWarmupOptions> options,
        TimeProvider? timeProvider = null)
    {
        _accessor = accessor;
        _telemetry = telemetry;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1) One immediate warmup at startup (best practice).
        await SafeRefresh(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = _options.CurrentValue;

            if (!opt.Enabled)
            {
                // If disabled, sleep a bit and re-check (so you can enable without restart).
                await DelaySafe(TimeSpan.FromSeconds(10), opt, stoppingToken);
                continue;
            }

            if (opt.Mode == WarmupScheduleMode.FixedInterval)
            {
                await RunFixedIntervalLoop(opt, stoppingToken);
                // If options change, loop returns and outer while re-evaluates.
            }
            else
            {
                await RunCalendarLoop(opt, stoppingToken);
                // If options change, loop returns and outer while re-evaluates.
            }
        }
    }

    private async Task RunFixedIntervalLoop(RateLimitConfigWarmupOptions opt, CancellationToken ct)
    {
        var interval = NormalizeInterval(opt.Interval);

        using var timer = new PeriodicTimer(interval);
        while (!ct.IsCancellationRequested)
        {
            // If options changed (mode/interval), restart loop quickly.
            var nowOpt = _options.CurrentValue;
            if (!nowOpt.Enabled || nowOpt.Mode != WarmupScheduleMode.FixedInterval || nowOpt.Interval != opt.Interval)
                return;

            try
            {
                // Wait for next tick. PeriodicTimer avoids overlap by design.
                if (!await timer.WaitForNextTickAsync(ct))
                    return;

                await MaybeJitter(nowOpt, ct);
                await SafeRefresh(ct);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunCalendarLoop(RateLimitConfigWarmupOptions opt, CancellationToken ct)
    {
        // Calendar loop: compute next run, delay until then, run once, repeat.
        while (!ct.IsCancellationRequested)
        {
            var nowOpt = _options.CurrentValue;
            if (!nowOpt.Enabled || nowOpt.Mode != WarmupScheduleMode.Calendar)
                return;

            var next = ComputeNextRun(_time.GetLocalNow(), nowOpt);
            var delay = next - _time.GetLocalNow();

            delay = CapDelay(delay, nowOpt.MaxDelayCap);
            await DelaySafe(delay, nowOpt, ct);

            // re-check after delay (options might have changed while waiting)
            nowOpt = _options.CurrentValue;
            if (!nowOpt.Enabled || nowOpt.Mode != WarmupScheduleMode.Calendar)
                return;

            await MaybeJitter(nowOpt, ct);
            await SafeRefresh(ct);
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

    private async Task DelaySafe(TimeSpan delay, RateLimitConfigWarmupOptions opt, CancellationToken ct)
    {
        // Never allow negative/zero in Delay
        if (delay <= TimeSpan.Zero)
            delay = TimeSpan.FromMilliseconds(10);

        // Safety cap
        delay = CapDelay(delay, opt.MaxDelayCap);

        try
        {
            //await _time.Delay(delay, ct);
            await _time.DelayAsync(delay, ct);

        }
        catch (OperationCanceledException) { }
    }

    private async Task MaybeJitter(RateLimitConfigWarmupOptions opt, CancellationToken ct)
    {
        if (opt.JitterMax <= TimeSpan.Zero) return;

        var ms = _rng.Next(0, (int)Math.Max(1, opt.JitterMax.TotalMilliseconds));
        await DelaySafe(TimeSpan.FromMilliseconds(ms), opt, ct);
    }

    private static TimeSpan NormalizeInterval(TimeSpan interval)
    {
        // Guardrails
        if (interval < TimeSpan.FromSeconds(5)) return TimeSpan.FromSeconds(5);
        if (interval > TimeSpan.FromDays(7)) return TimeSpan.FromDays(7);
        return interval;
    }

    private static TimeSpan CapDelay(TimeSpan delay, TimeSpan max)
        => delay > max ? max : delay;

    /// <summary>
    /// Calendar scheduling:
    /// - Runs at RunAtTimeOfDay (default 02:00)
    /// - Repeats by Months/Days/Hours/Minutes (first non-zero precedence: Months > Days > Hours > Minutes)
    /// </summary>
    private static DateTimeOffset ComputeNextRun(DateTimeOffset nowLocal, RateLimitConfigWarmupOptions opt)
    {
        var tod = opt.RunAtTimeOfDay ?? TimeSpan.Zero;

        // baseline candidate: today at RunAtTimeOfDay
        var candidate = new DateTimeOffset(
            nowLocal.Year, nowLocal.Month, nowLocal.Day,
            0, 0, 0, nowLocal.Offset).Add(tod);

        // if we already passed it, move forward by one unit
        if (candidate <= nowLocal)
        {
            candidate = AddPeriod(candidate, opt);
        }

        // If schedule is “every N months/days/etc”, keep moving until >= now
        while (candidate <= nowLocal)
        {
            candidate = AddPeriod(candidate, opt);
        }

        return candidate;

        static DateTimeOffset AddPeriod(DateTimeOffset dt, RateLimitConfigWarmupOptions opt)
        {
            if (opt.Months > 0) return dt.AddMonths(opt.Months);
            if (opt.Days > 0) return dt.AddDays(opt.Days);
            if (opt.Hours > 0) return dt.AddHours(opt.Hours);
            if (opt.Minutes > 0) return dt.AddMinutes(opt.Minutes);

            // default if Calendar but no period set: daily
            return dt.AddDays(1);
        }
    }
}





//using Microsoft.ApplicationInsights;
//using Microsoft.Extensions.Hosting;

//namespace App.ConfigCatalog.Infrastructure.Services;

//public sealed class RateLimitConfigWarmupHostedService : IHostedService, IDisposable
//{
//    private readonly RateLimitConfigAccessor _accessor;
//    private readonly TelemetryClient _telemetry;

//    private CancellationTokenSource? _cts;
//    private Task? _loop;

//    public RateLimitConfigWarmupHostedService(RateLimitConfigAccessor accessor, TelemetryClient telemetry)
//    {
//        _accessor = accessor;
//        _telemetry = telemetry;
//    }

//    public Task StartAsync(CancellationToken cancellationToken)
//    {
//        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
//        _loop = RunAsync(_cts.Token);
//        return Task.CompletedTask;
//    }

//    public async Task StopAsync(CancellationToken cancellationToken)
//    {
//        if (_cts is null) return;

//        try
//        {
//            _cts.Cancel();
//            if (_loop is not null)
//                await _loop.WaitAsync(cancellationToken);
//        }
//        catch (OperationCanceledException) { }
//    }

//    public void Dispose()
//    {
//        _cts?.Dispose();
//    }

//    private async Task RunAsync(CancellationToken stoppingToken)
//    {
//        await SafeRefresh(stoppingToken);

//        while (!stoppingToken.IsCancellationRequested)
//        {
//            try
//            {
//                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
//                await SafeRefresh(stoppingToken);
//            }
//            catch (OperationCanceledException) { }
//        }
//    }

//    private async Task SafeRefresh(CancellationToken ct)
//    {
//        try
//        {
//            await _accessor.RefreshAsync(ct);
//        }
//        catch (OperationCanceledException) { }
//        catch (Exception ex)
//        {
//            _telemetry.TrackException(ex);
//        }
//    }
//}
