using Microsoft.Extensions.Hosting;
using App.ConfigCatalog.Domain;
using App.ConfigCatalog.Infrastructure.Entities;
using App.ConfigCatalog.Infrastructure.RateLimiting.Interfaces;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

public sealed class RateLimitAuditWriterHostedService : BackgroundService
{
    private readonly ChannelReader<RateLimitAuditEvent> _reader;
    private readonly IRateLimitAuditStore _store;

    public RateLimitAuditWriterHostedService(
        ChannelReader<RateLimitAuditEvent> reader,
        IRateLimitAuditStore store)
    {
        _reader = reader;
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<RateLimitAuditEvent>(capacity: 2000);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var item = await _reader.ReadAsync(stoppingToken);
                batch.Add(item);

                while (_reader.TryRead(out var next))
                {
                    batch.Add(next);
                    if (batch.Count >= 2000) break;
                }

                await FlushAsync(batch, stoppingToken);
                batch.Clear();
            }
            catch (OperationCanceledException) { }
        }
    }

    private async Task FlushAsync(List<RateLimitAuditEvent> events, CancellationToken ct)
    {
        if (events.Count == 0) return;

        // 1) agregación por minuto
        var groups = events.GroupBy(e => new
        {
            Window = TruncateToMinute(e.AtUtc.UtcDateTime),
            e.Policy,
            e.IdentityKind,
            IdentityHash = Sha256(e.IdentityKey),
            e.Method
        });

        foreach (var g in groups)
        {
            var agg = new RateLimitMinuteAgg
            {
                WindowStartUtc = g.Key.Window,
                Policy = g.Key.Policy,
                IdentityKind = g.Key.IdentityKind,
                IdentityHash = g.Key.IdentityHash,
                Method = g.Key.Method,
                Requests = g.LongCount(),
                Rejected = g.LongCount(x => x.Rejected),
            };

            await _store.UpsertMinuteAggAsync(agg, ct);
        }

        // 2) violaciones (solo 429)
        var violations = events
            .Where(e => e.Rejected)
            .Select(v => new RateLimitViolation
            {
                AtUtc = v.AtUtc.UtcDateTime,
                Policy = v.Policy,
                IdentityKind = v.IdentityKind,
                IdentityHash = Sha256(v.IdentityKey),
                TraceId = v.TraceId,
                CorrelationId = v.CorrelationId,
                Path = v.Path,
                Method = v.Method,
                StatusCode = v.StatusCode,
                RetryAfterSeconds = v.RetryAfterSeconds,
                Reason = v.Reason
            })
            .ToList();

        if (violations.Count > 0)
            await _store.AddViolationsAsync(violations, ct);

        static DateTime TruncateToMinute(DateTime utc)
            => new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);

        static byte[] Sha256(string s)
            => SHA256.HashData(Encoding.UTF8.GetBytes(s));
    }
}


//using Microsoft.EntityFrameworkCore;
//using Microsoft.Extensions.Hosting;
//using App.ConfigCatalog.Domain;
//using App.ConfigCatalog.Infrastructure;
//using App.ConfigCatalog.Infrastructure.RateLimiting.ReadModels;
//using System.Security.Cryptography;
//using System.Text;
//using System.Threading.Channels;

//public sealed class RateLimitAuditWriterHostedService : BackgroundService
//{
//    private readonly ChannelReader<RateLimitAuditEvent> _reader;
//    private readonly IDbContextFactory<AppConfigDbContext> _dbFactory;

//    public RateLimitAuditWriterHostedService(
//        ChannelReader<RateLimitAuditEvent> reader,
//        IDbContextFactory<AppConfigDbContext> dbFactory)
//    {
//        _reader = reader;
//        _dbFactory = dbFactory;
//    }

//    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
//    {
//        // buffer in-memory (batch)
//        var batch = new List<RateLimitAuditEvent>(capacity: 2000);

//        while (!stoppingToken.IsCancellationRequested)
//        {
//            try
//            {
//                // espera al menos 1
//                var item = await _reader.ReadAsync(stoppingToken);
//                batch.Add(item);

//                // drena rápido
//                while (_reader.TryRead(out var next))
//                {
//                    batch.Add(next);
//                    if (batch.Count >= 2000) break;
//                }

//                await FlushAsync(batch, stoppingToken);
//                batch.Clear();
//            }
//            catch (OperationCanceledException) { }
//        }
//    }

//    private async Task FlushAsync(List<RateLimitAuditEvent> events, CancellationToken ct)
//    {
//        if (events.Count == 0) return;

//        await using var db = await _dbFactory.CreateDbContextAsync(ct);

//        // 1) agregación por minuto
//        var groups = events.GroupBy(e => new
//        {
//            Window = TruncateToMinute(e.AtUtc.UtcDateTime),
//            e.Policy,
//            e.IdentityKind,
//            IdentityHash = Sha256(e.IdentityKey),
//            e.Method
//        });

//        foreach (var g in groups)
//        {
//            var requests = g.LongCount();
//            var rejected = g.LongCount(x => x.Rejected);

//             //Upsert: depende de tu DB. (SQL Server -> MERGE / SQLite -> INSERT ON CONFLICT)
//             //Aquí lo dejo como pseudo, tú lo adaptas.
//            db.RateLimitMinuteAggs.Upsert(new RateLimitMinuteAggRow
//            {
//                WindowStartUtc = g.Key.Window,
//                Policy = g.Key.Policy,
//                IdentityKind = g.Key.IdentityKind,
//                IdentityHash = g.Key.IdentityHash,
//                Method = g.Key.Method,
//                Requests = requests,
//                Rejected = rejected
//            });
//        }

//        // 2) eventos de violación (solo 429)
//        foreach (var v in events.Where(e => e.Rejected))
//        {
//            //Console.WriteLine("Value", v.Rejected);
//            db.RateLimitViolations.Add(new RateLimitViolationRow
//            {
//                AtUtc = v.AtUtc.UtcDateTime,
//                Policy = v.Policy,
//                IdentityKind = v.IdentityKind,
//                IdentityHash = Sha256(v.IdentityKey),
//                TraceId = v.TraceId,
//                CorrelationId = v.CorrelationId,
//                Path = v.Path,
//                Method = v.Method,
//                StatusCode = v.StatusCode,
//                RetryAfterSeconds = v.RetryAfterSeconds,
//                Reason = v.Reason
//            });
//        }

//        await db.SaveChangesAsync(ct);

//        static DateTime TruncateToMinute(DateTime utc)
//            => new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);

//        static byte[] Sha256(string s)
//            => SHA256.HashData(Encoding.UTF8.GetBytes(s));
//    }
//}
