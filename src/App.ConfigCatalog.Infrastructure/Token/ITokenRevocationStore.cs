using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace App.ConfigCatalog.Infrastructure.Token
{
    public interface ITokenRevocationStore
    {
        Task RevokeAsync(string jti, TimeSpan ttl, CancellationToken ct);
        Task<bool> IsRevokedAsync(string jti, CancellationToken ct);

        // Optional: anti-replay (token reuse detection)
        Task<bool> TryMarkSeenAsync(string jti, TimeSpan ttl, CancellationToken ct);
    }
}
