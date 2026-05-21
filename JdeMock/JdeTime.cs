using System;
using System.Globalization;

namespace ActivityTester.JdeMock
{
    /// <summary>
    /// JDE Orchestrator timestamps use a specific format: ISO 8601 with millisecond
    /// precision and a timezone offset WITHOUT a colon (e.g. "+0400" not "+04:00").
    /// .NET's default "zzz" format inserts the colon, so we format manually.
    /// </summary>
    internal static class JdeTime
    {
        // JDE production server runs in Dubai → UTC+4. The mock responses match
        // the production timezone so downstream parsers don't need a separate
        // code path for "local dev" vs. "real JDE".
        private static readonly TimeSpan UaeOffset = TimeSpan.FromHours(4);

        public static DateTimeOffset NowUae() => DateTimeOffset.UtcNow.ToOffset(UaeOffset);

        /// <summary>"2026-05-21T08:30:08.298+0400"</summary>
        public static string Format(DateTimeOffset dt)
        {
            var inUae = dt.ToOffset(UaeOffset);
            char sign  = inUae.Offset.Ticks < 0 ? '-' : '+';
            int  hours = Math.Abs(inUae.Offset.Hours);
            int  mins  = Math.Abs(inUae.Offset.Minutes);
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-ddTHH:mm:ss.fff}{1}{2:00}{3:00}",
                inUae, sign, hours, mins);
        }
    }
}
