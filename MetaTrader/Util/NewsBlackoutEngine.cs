using System;
using System.Collections.Generic;

namespace MetaTrader.Util
{
    public static class NewsBlackoutEngine
    {
        // FTMO requires 2 min before + 2 min after
        private static readonly TimeSpan BlackoutBefore = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan BlackoutAfter = TimeSpan.FromMinutes(2);

        // Hard-coded FTMO news (UTC times!)
        private static readonly List<NewsEvent> Events = new()
    {
        // CPI GBP — 17 Dec 2025 08:00 CET = 07:00 UTC
        new NewsEvent(
            new DateTime(2025, 12, 17, 7, 0, 0, DateTimeKind.Utc),
            "GBP"),

        // Crude Oil Inventories — 16:30 CET = 15:30 UTC
        new NewsEvent(
            new DateTime(2025, 12, 17, 15, 30, 0, DateTimeKind.Utc),
            "OIL"),

        // NZD GDP — 22:45 CET = 21:45 UTC
        new NewsEvent(
            new DateTime(2025, 12, 17, 21, 45, 0, DateTimeKind.Utc),
            "NZD")
    };

        public static bool IsBlackout(string symbol)
        {
            DateTime now = DateTime.UtcNow;

            foreach (var e in Events)
            {
                if (!SymbolMatches(symbol, e.Affected))
                    continue;

                DateTime start = e.TimeUtc - BlackoutBefore;
                DateTime end = e.TimeUtc + BlackoutAfter;

                if (now >= start && now <= end)
                    return true;
            }

            return false;
        }

        private static bool SymbolMatches(string symbol, string affected)
        {
            if (affected == "OIL")
                return symbol.Contains("WTI") || symbol.Contains("BRENT") || symbol.Contains("OIL");

            return symbol.Contains(affected);
        }

        private record NewsEvent(DateTime TimeUtc, string Affected);
    }

}
