using System;

namespace MetaTrader.Util
{
    public static class CryptoTradingWindow
    {
        // AST (UTC-4) – Windows time zone ID
        private static readonly TimeZoneInfo AstTimeZone =
            TimeZoneInfo.FindSystemTimeZoneById("SA Western Standard Time");

        /// <summary>
        /// Determines whether the current time falls within the
        /// crypto trading window: Saturday 7:00 PM → Sunday 6:00 PM (AST)
        /// </summary>
        public static bool IsWithinWeekendWindow(DateTime utcNow)
        {
            var astNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, AstTimeZone);

            var day = astNow.DayOfWeek;
            var time = astNow.TimeOfDay;

            // Saturday 7:00 PM onward
            if (day == DayOfWeek.Saturday && time >= new TimeSpan(19, 0, 0))
                return true;

            // Sunday before 6:00 PM
            if (day == DayOfWeek.Sunday && time < new TimeSpan(18, 0, 0))
                return true;

            return false;
        }
    }

}
