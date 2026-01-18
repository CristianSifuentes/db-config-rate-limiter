using App.ConfigCatalog.Infrastructure.Entities;
using App.ConfigCatalog.Infrastructure.RateLimiting.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace App.ConfigCatalog.Infrastructure.RateLimiting.Store;

public sealed class SqlServerRateLimitAuditStore : IRateLimitAuditStore
{
    private const int DefaultBatchSize = 1000;

    private readonly IDbContextFactory<AppConfigDbContext> _dbFactory;

    public SqlServerRateLimitAuditStore(IDbContextFactory<AppConfigDbContext> dbFactory)
        => _dbFactory = dbFactory;

    public async Task UpsertMinuteAggAsync(RateLimitMinuteAgg row, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var sql = @"
MERGE RateLimitMinuteAgg WITH (HOLDLOCK) AS T
USING (VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6)) AS S
    (WindowStartUtc, Policy, IdentityKind, IdentityHash, Method, Requests, Rejected)
ON  T.WindowStartUtc = S.WindowStartUtc
AND T.Policy         = S.Policy
AND T.IdentityKind   = S.IdentityKind
AND T.IdentityHash   = S.IdentityHash
WHEN MATCHED THEN
    UPDATE SET
        T.Method    = COALESCE(S.Method, T.Method),
        T.Requests  = T.Requests + S.Requests,
        T.Rejected  = T.Rejected + S.Rejected
WHEN NOT MATCHED THEN
    INSERT (WindowStartUtc, Policy, IdentityKind, IdentityHash, Method, Requests, Rejected)
    VALUES (S.WindowStartUtc, S.Policy, S.IdentityKind, S.IdentityHash, S.Method, S.Requests, S.Rejected);";

        await db.Database.ExecuteSqlRawAsync(
            sql,
            parameters: new object[]
            {
                row.WindowStartUtc,
                row.Policy,
                row.IdentityKind,
                row.IdentityHash,
                (object?)row.Method ?? DBNull.Value,
                row.Requests,
                row.Rejected
            },
            cancellationToken: ct);
    }

    public async Task AddViolationsAsync(IEnumerable<RateLimitViolation> rows, CancellationToken ct)
    {
        // Materializa una sola vez para evitar enumeraciones múltiples
        var list = rows as IList<RateLimitViolation> ?? rows.ToList();
        if (list.Count == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Estrategia de resiliencia de EF (si tienes EnableRetryOnFailure en SqlServer)
        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            try
            {
                // Inserta en batches para reducir memoria / locks / timeouts
                for (var i = 0; i < list.Count; i += DefaultBatchSize)
                {
                    var batch = list.Skip(i).Take(DefaultBatchSize).ToList();

                    db.RateLimitViolations.AddRange(batch);
                    await db.SaveChangesAsync(ct);

                    // Limpia el ChangeTracker para evitar crecimiento (muy importante en servicios de background)
                    db.ChangeTracker.Clear();
                }

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        });
    }

    // Opcional (pero muy recomendado): guarda agg + violations en una sola transacción
    public async Task PersistAsync(
        IEnumerable<RateLimitMinuteAgg> minuteAggs,
        IEnumerable<RateLimitViolation> violations,
        CancellationToken ct)
    {
        var aggs = minuteAggs as IList<RateLimitMinuteAgg> ?? minuteAggs.ToList();
        var vios = violations as IList<RateLimitViolation> ?? violations.ToList();

        if (aggs.Count == 0 && vios.Count == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            try
            {
                // 1) Upsert aggs (MERGE por fila; si quieres ultra-perf, haz TVP)
                foreach (var a in aggs)
                    await UpsertMinuteAggInternalAsync(db, a, ct);

                // 2) Insert violations por batch
                if (vios.Count > 0)
                {
                    for (var i = 0; i < vios.Count; i += DefaultBatchSize)
                    {
                        var batch = vios.Skip(i).Take(DefaultBatchSize).ToList();
                        db.RateLimitViolations.AddRange(batch);
                        await db.SaveChangesAsync(ct);
                        db.ChangeTracker.Clear();
                    }
                }

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        });
    }

    public async Task PersistAsync(
        IEnumerable<RateLimitIdentity> identities,
        IEnumerable<RateLimitMinuteAgg> minuteAggs,
        IEnumerable<RateLimitViolation> violations,
        CancellationToken ct)
    {
        var ids = identities as IList<RateLimitIdentity> ?? identities.ToList();
        var aggs = minuteAggs as IList<RateLimitMinuteAgg> ?? minuteAggs.ToList();
        var vios = violations as IList<RateLimitViolation> ?? violations.ToList();

        if (ids.Count == 0 && aggs.Count == 0 && vios.Count == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            try
            {
                // 1) Ensure identities (idempotente)
                // Dedup por Kind+Hash para reducir roundtrips
                foreach (var id in ids
                    .GroupBy(x => new { x.Kind, Hash = Convert.ToBase64String(x.KeyHash) })
                    .Select(g => g.First()))
                {
                    await EnsureIdentityInternalAsync(db, id, ct);
                }

                // 2) Upsert aggs
                foreach (var a in aggs)
                    await UpsertMinuteAggInternalAsync(db, a, ct);

                // 3) Insert violations en batch
                for (var i = 0; i < vios.Count; i += DefaultBatchSize)
                {
                    var batch = vios.Skip(i).Take(DefaultBatchSize).ToList();
                    db.RateLimitViolations.AddRange(batch);
                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                }

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        });
    }

    private static async Task EnsureIdentityInternalAsync(
    AppConfigDbContext db,
    RateLimitIdentity row,
    CancellationToken ct)
    {
        const string sql = @"
                            IF NOT EXISTS (
                                SELECT 1 FROM RateLimitIdentity WITH (UPDLOCK, HOLDLOCK)
                                WHERE [Kind] = @Kind AND KeyHash = @KeyHash
                            )
                            BEGIN
                                INSERT INTO RateLimitIdentity
                                    ([Kind], KeyHash, KeyPlain, TenantId, ClientId, UserId, Ip)
                                VALUES
                                    (@Kind, @KeyHash, @KeyPlain, @TenantId, @ClientId, @UserId, @Ip);
                            END";

        // IMPORTANTE: SqlParameter.Value = DBNull.Value (aquí sí es válido)
        // porque EF recibe SqlParameter (no un object DBNull suelto).
        var pKind = new SqlParameter("@Kind", SqlDbType.NVarChar, 20) { Value = row.Kind };
        var pKeyHash = new SqlParameter("@KeyHash", SqlDbType.VarBinary, 32) { Value = row.KeyHash };

        var pKeyPlain = new SqlParameter("@KeyPlain", SqlDbType.NVarChar, 256)
        { Value = (object?)row.KeyPlain ?? DBNull.Value };

        var pTenantId = new SqlParameter("@TenantId", SqlDbType.NVarChar, 64)
        { Value = (object?)row.TenantId ?? DBNull.Value };

        var pClientId = new SqlParameter("@ClientId", SqlDbType.NVarChar, 64)
        { Value = (object?)row.ClientId ?? DBNull.Value };

        var pUserId = new SqlParameter("@UserId", SqlDbType.NVarChar, 64)
        { Value = (object?)row.UserId ?? DBNull.Value };

        var pIp = new SqlParameter("@Ip", SqlDbType.NVarChar, 64)
        { Value = (object?)row.Ip ?? DBNull.Value };

        await db.Database.ExecuteSqlRawAsync(
            sql,
            new[] { pKind, pKeyHash, pKeyPlain, pTenantId, pClientId, pUserId, pIp },
            ct);
    }

    //    private static async Task EnsureIdentityInternalAsync(AppConfigDbContext db, RateLimitIdentity row, CancellationToken ct)
    //    {
    //        // IF NOT EXISTS con UPDLOCK/HOLDLOCK evita duplicados bajo concurrencia
    //        var sql = @"
    //IF NOT EXISTS (
    //    SELECT 1 FROM RateLimitIdentity WITH (UPDLOCK, HOLDLOCK)
    //    WHERE [Kind] = @p0 AND KeyHash = @p1
    //)
    //BEGIN
    //    INSERT INTO RateLimitIdentity([Kind], KeyHash, KeyPlain, TenantId, ClientId, UserId, Ip)
    //    VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6);
    //END";

    //        await db.Database.ExecuteSqlRawAsync(sql, new object[]
    //        {
    //            row.Kind,
    //            row.KeyHash,
    //            (object?)row.KeyPlain ?? DBNull.Value,
    //            (object?)row.TenantId ?? DBNull.Value,
    //            (object?)row.ClientId ?? DBNull.Value,
    //            (object?)row.UserId ?? DBNull.Value,
    //            (object?)row.Ip ?? DBNull.Value
    //        }, ct);
    //    }

    // Reusa el MERGE pero usando el MISMO DbContext/Tx (clave para atomicidad)
    private static async Task UpsertMinuteAggInternalAsync(AppConfigDbContext db, RateLimitMinuteAgg row, CancellationToken ct)
    {
        var sql = @"
                    MERGE RateLimitMinuteAgg WITH (HOLDLOCK) AS T
                    USING (VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6)) AS S
                        (WindowStartUtc, Policy, IdentityKind, IdentityHash, Method, Requests, Rejected)
                    ON  T.WindowStartUtc = S.WindowStartUtc
                    AND T.Policy         = S.Policy
                    AND T.IdentityKind   = S.IdentityKind
                    AND T.IdentityHash   = S.IdentityHash
                    WHEN MATCHED THEN
                        UPDATE SET
                            T.Method    = COALESCE(S.Method, T.Method),
                            T.Requests  = T.Requests + S.Requests,
                            T.Rejected  = T.Rejected + S.Rejected
                    WHEN NOT MATCHED THEN
                        INSERT (WindowStartUtc, Policy, IdentityKind, IdentityHash, Method, Requests, Rejected)
                        VALUES (S.WindowStartUtc, S.Policy, S.IdentityKind, S.IdentityHash, S.Method, S.Requests, S.Rejected);";

        await db.Database.ExecuteSqlRawAsync(
            sql,
            parameters: new object[]
            {
                row.WindowStartUtc,
                row.Policy,
                row.IdentityKind,
                row.IdentityHash,
                (object?)row.Method ?? DBNull.Value,
                row.Requests,
                row.Rejected
            },
            cancellationToken: ct);
    }

    public Task PersistAsync(IEnumerable<RateLimitIdentity> identities, IEnumerable<RateLimitMinuteAgg> minuteAggs, IEnumerable<RateLimitViolation> violations, IEnumerable<RateLimitBlock> blocks, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}





//using Microsoft.EntityFrameworkCore;
//using App.ConfigCatalog.Infrastructure.Entities;
//using App.ConfigCatalog.Infrastructure.RateLimiting.Interfaces;

//namespace App.ConfigCatalog.Infrastructure.RateLimiting.Store;

//public sealed class SqlServerRateLimitAuditStore : IRateLimitAuditStore
//{
//    private readonly IDbContextFactory<AppConfigDbContext> _dbFactory;

//    public SqlServerRateLimitAuditStore(IDbContextFactory<AppConfigDbContext> dbFactory)
//        => _dbFactory = dbFactory;

//    public async Task UpsertMinuteAggAsync(RateLimitMinuteAgg row, CancellationToken ct)
//    {
//        await using var db = await _dbFactory.CreateDbContextAsync(ct);

//        // MERGE incrementa contadores (Requests/Rejected) en vez de overwrite.
//        var sql = @"
//MERGE RateLimitMinuteAgg WITH (HOLDLOCK) AS T
//USING (VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6)) AS S
//    (WindowStartUtc, Policy, IdentityKind, IdentityHash, Method, Requests, Rejected)
//ON  T.WindowStartUtc = S.WindowStartUtc
//AND T.Policy         = S.Policy
//AND T.IdentityKind   = S.IdentityKind
//AND T.IdentityHash   = S.IdentityHash
//WHEN MATCHED THEN
//    UPDATE SET
//        T.Method    = COALESCE(S.Method, T.Method),
//        T.Requests  = T.Requests + S.Requests,
//        T.Rejected  = T.Rejected + S.Rejected
//WHEN NOT MATCHED THEN
//    INSERT (WindowStartUtc, Policy, IdentityKind, IdentityHash, Method, Requests, Rejected)
//    VALUES (S.WindowStartUtc, S.Policy, S.IdentityKind, S.IdentityHash, S.Method, S.Requests, S.Rejected);";

//        await db.Database.ExecuteSqlRawAsync(
//            sql,
//            parameters: new object[]
//            {
//                row.WindowStartUtc,
//                row.Policy,
//                row.IdentityKind,
//                row.IdentityHash,
//                (object?)row.Method ?? DBNull.Value,
//                row.Requests,
//                row.Rejected
//            },
//            cancellationToken: ct);
//    }

//    public async Task AddViolationsAsync(IEnumerable<RateLimitViolation> rows, CancellationToken ct)
//    {
//        await using var db = await _dbFactory.CreateDbContextAsync(ct);

//        // Inserción normal; si necesitas ultra-perf: SqlBulkCopy / TVP.
//        db.RateLimitViolations.AddRange(rows);
//        await db.SaveChangesAsync(ct);
//    }
//}


//using Microsoft.EntityFrameworkCore;
//using App.ConfigCatalog.Infrastructure.Entities;
//using App.ConfigCatalog.Infrastructure.RateLimiting.Interfaces;
//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;

//namespace App.ConfigCatalog.Infrastructure.RateLimiting
//{
//    public sealed class SqlServerRateLimitAuditStore : IRateLimitAuditStore
//    {
//        private readonly IDbContextFactory<AppConfigDbContext> _dbFactory;

//        public SqlServerRateLimitAuditStore(IDbContextFactory<AppConfigDbContext> dbFactory)
//            => _dbFactory = dbFactory;

//        public Task AddViolationsAsync(IEnumerable<RateLimitViolation> rows, CancellationToken ct)
//        {
//            throw new NotImplementedException();
//        }

//        public async Task UpsertMinuteAggAsync(RateLimitMinuteAgg row, CancellationToken ct)
//        {
//            await using var db = await _dbFactory.CreateDbContextAsync(ct);

//            // Incremental aggregation (atomic)
//            await db.Database.ExecuteSqlInterpolatedAsync($@"
//MERGE RateLimitMinuteAgg AS T
//USING (SELECT
//    {row.WindowStartUtc} AS WindowStartUtc,
//    {row.Policy}         AS Policy,
//    {row.IdentityKind}   AS IdentityKind,
//    {row.IdentityHash}   AS IdentityHash,
//    {row.RouteTemplate}  AS RouteTemplate,
//    {row.Method}         AS Method,
//    {row.Requests}       AS Requests,
//    {row.Rejected}       AS Rejected,
//    {row.MaxObservedConcurrency} AS MaxObservedConcurrency,
//    {row.LastStatusCode} AS LastStatusCode
//) AS S
//ON  T.WindowStartUtc = S.WindowStartUtc
//AND T.Policy         = S.Policy
//AND T.IdentityKind   = S.IdentityKind
//AND T.IdentityHash   = S.IdentityHash
//WHEN MATCHED THEN UPDATE SET
//    Requests = T.Requests + S.Requests,
//    Rejected = T.Rejected + S.Rejected,
//    LastStatusCode = COALESCE(S.LastStatusCode, T.LastStatusCode),
//    MaxObservedConcurrency =
//        CASE
//            WHEN T.MaxObservedConcurrency IS NULL THEN S.MaxObservedConcurrency
//            WHEN S.MaxObservedConcurrency IS NULL THEN T.MaxObservedConcurrency
//            WHEN S.MaxObservedConcurrency > T.MaxObservedConcurrency THEN S.MaxObservedConcurrency
//            ELSE T.MaxObservedConcurrency
//        END
//WHEN NOT MATCHED THEN
//    INSERT (WindowStartUtc, Policy, IdentityKind, IdentityHash, RouteTemplate, Method, Requests, Rejected, MaxObservedConcurrency, LastStatusCode)
//    VALUES (S.WindowStartUtc, S.Policy, S.IdentityKind, S.IdentityHash, S.RouteTemplate, S.Method, S.Requests, S.Rejected, S.MaxObservedConcurrency, S.LastStatusCode);
//", ct);
//        }
//    }
//}
