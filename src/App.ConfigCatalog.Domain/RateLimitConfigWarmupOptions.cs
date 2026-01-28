using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace App.ConfigCatalog.Domain
{
    public sealed class RateLimitConfigWarmupOptions
    {
        public const string SectionName = "RateLimit:Warmup";

        /// <summary>
        /// Enabled/disabled without removing the hosted service.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// FixedInterval: runs every Interval.
        /// Calendar: runs at a specific time of day and can repeat every N months/days/hours/minutes.
        /// </summary>
        public WarmupScheduleMode Mode { get; set; } = WarmupScheduleMode.FixedInterval;

        /// <summary>
        /// Used when Mode = FixedInterval.
        /// Accepts TimeSpan strings: "00:01:00" (1 min), "01:00:00" (1 hour), etc.
        /// </summary>
        [Required]
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Used when Mode = Calendar.
        /// Executes at this local time (server time unless you also add timezone handling).
        /// Example: 02:30:00
        /// </summary>
        public TimeSpan? RunAtTimeOfDay { get; set; } = TimeSpan.FromHours(2);

        /// <summary>
        /// Used when Mode = Calendar.
        /// Example: Months=1 means “every month at RunAtTimeOfDay”.
        /// </summary>
        public int Months { get; set; } = 0;

        /// <summary>
        /// Used when Mode = Calendar.
        /// Example: Days=1 means “daily at RunAtTimeOfDay”.
        /// </summary>
        public int Days { get; set; } = 0;

        public int Hours { get; set; } = 0;
        public int Minutes { get; set; } = 0;

        /// <summary>
        /// Adds a random delay (0..JitterMax) to spread load across instances.
        /// </summary>
        public TimeSpan JitterMax { get; set; } = TimeSpan.FromSeconds(0);

        /// <summary>
        /// Safety: prevents the service from sleeping too long if misconfigured.
        /// </summary>
        public TimeSpan MaxDelayCap { get; set; } = TimeSpan.FromDays(31);
    }

    public enum WarmupScheduleMode
    {
        FixedInterval = 0,
        Calendar = 1
    }

}
