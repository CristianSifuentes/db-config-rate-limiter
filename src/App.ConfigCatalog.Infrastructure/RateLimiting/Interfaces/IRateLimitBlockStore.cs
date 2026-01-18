using App.ConfigCatalog.Infrastructure.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace App.ConfigCatalog.Infrastructure.RateLimiting.Interfaces
{
    public interface IRateLimitBlockStore
    {
        Task<RateLimitBlock?> GetActiveBlockAsync(string kind, byte[] identityHash, CancellationToken ct);
        Task UpsertBlockAsync(RateLimitBlock block, CancellationToken ct);
    }
}
