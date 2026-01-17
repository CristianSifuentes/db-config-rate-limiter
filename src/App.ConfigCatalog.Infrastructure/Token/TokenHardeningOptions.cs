using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace App.ConfigCatalog.Infrastructure.Token
{
    public sealed class TokenHardeningOptions
    {
        public int MaxAccessTokenAgeMinutes { get; set; } = 15;
        public int ClockSkewSeconds { get; set; } = 30;
        public bool EnableJtiReplayProtection { get; set; } = true;
        public int JtiCacheMinutes { get; set; } = 20;
    }
}
