using Microsoft.Extensions.Caching.Distributed;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace App.ConfigCatalog.Infrastructure.Token
{
    public sealed class DistributedTokenRevocationStore : ITokenRevocationStore
    {
        private readonly IDistributedCache _cache;

        public DistributedTokenRevocationStore(IDistributedCache cache) => _cache = cache;

        private static string RevokedKey(string jti) => $"revoked:jti:{jti}";
        private static string SeenKey(string jti) => $"seen:jti:{jti}";

        public async Task RevokeAsync(string jti, TimeSpan ttl, CancellationToken ct)
        {
            await _cache.SetStringAsync(
                RevokedKey(jti),
                "1",
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                ct);
        }

        public async Task<bool> IsRevokedAsync(string jti, CancellationToken ct)
            => (await _cache.GetStringAsync(RevokedKey(jti), ct)) is not null;

        public async Task<bool> TryMarkSeenAsync(string jti, TimeSpan ttl, CancellationToken ct)
        {
            // naive “first write wins”: if already exists -> replay
            var key = SeenKey(jti);
            var existing = await _cache.GetStringAsync(key, ct);
            if (existing is not null) return false;

            await _cache.SetStringAsync(
                key,
                "1",
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                ct);

            return true;
        }
    }

}
