using App.ConfigCatalog.Infrastructure.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace App.ConfigCatalog.Infrastructure.RateLimiting.Interfaces
{
    public interface IRateLimitAuditStore
    {
        Task UpsertMinuteAggAsync(RateLimitMinuteAgg row, CancellationToken ct);

        Task AddViolationsAsync(IEnumerable<RateLimitViolation> rows, CancellationToken ct);

        // (Opcional y recomendado) Para atomicidad: agrega todo en 1 transacción
        //Task PersistAsync(
        //    IEnumerable<RateLimitMinuteAgg> minuteAggs,
        //    IEnumerable<RateLimitViolation> violations,
        //    CancellationToken ct);

        Task PersistAsync(
                  IEnumerable<RateLimitIdentity> identities,
                  IEnumerable<RateLimitMinuteAgg> minuteAggs,
                  IEnumerable<RateLimitViolation> violations,
                  CancellationToken ct);
    }
}
