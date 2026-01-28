using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace App.ConfigCatalog.Infrastructure.Services
{
    public static class TimeProviderExtensions
    {
        public static Task DelayAsync(this TimeProvider timeProvider, TimeSpan delay, CancellationToken ct = default)
        {
            if (delay <= TimeSpan.Zero)
                return Task.CompletedTask;

            if (ct.IsCancellationRequested)
                return Task.FromCanceled(ct);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            ITimer? timer = null;
            CancellationTokenRegistration ctr = default;

            // Timer: completa el TCS al expirar
            timer = timeProvider.CreateTimer(_ =>
            {
                tcs.TrySetResult();
            }, state: null, dueTime: delay, period: Timeout.InfiniteTimeSpan);

            // Cancelación: cancela el TCS y limpia recursos
            if (ct.CanBeCanceled)
            {
                ctr = ct.Register(() =>
                {
                    tcs.TrySetCanceled(ct);
                });
            }

            return tcs.Task.ContinueWith(t =>
            {
                try { timer?.Dispose(); } catch { /* ignore */ }
                try { ctr.Dispose(); } catch { /* ignore */ }
                return t;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
        }
    }

}
