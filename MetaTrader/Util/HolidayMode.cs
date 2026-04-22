using System;
using System.Linq;

namespace MetaTrader.Util
{
    public static class HolidayMode
    {
        public static bool IsHolidayModeActive(DateTime now)
        {
            now = now.Date;

            // WEEKENDS
            if (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday)
                return true;

            // YEAR-END DEAD ZONE (Dec 21 - Jan 2)
            if ((now.Month == 12 && now.Day >= 21) ||
                (now.Month == 1 && now.Day <= 2))
                return true;

            // FIXED HOLIDAYS
            var fixedHolidays = new[]
            {
            new DateTime(now.Year, 1, 1),   // New Year's Day
            new DateTime(now.Year, 6, 19),  // Juneteenth
            new DateTime(now.Year, 7, 4),   // Independence Day
            new DateTime(now.Year, 12, 24), // Christmas Eve
            new DateTime(now.Year, 12, 25), // Christmas Day
            new DateTime(now.Year, 12, 31), // New Year's Eve
        };

            if (fixedHolidays.Any(d => d == now))
                return true;

            // FLOATING HOLIDAYS
            // Martin Luther King Jr (3rd Monday of Jan)
            if (IsNthWeekdayOfMonth(now, DayOfWeek.Monday, 3, 1))
                return true;

            // Presidents Day (3rd Monday of Feb)
            if (IsNthWeekdayOfMonth(now, DayOfWeek.Monday, 3, 2))
                return true;

            // Memorial Day (last Monday of May)
            if (IsLastWeekdayOfMonth(now, DayOfWeek.Monday, 5))
                return true;

            // Labor Day (1st Monday of Sep)
            if (IsNthWeekdayOfMonth(now, DayOfWeek.Monday, 1, 9))
                return true;

            // Thanksgiving (4th Thursday of Nov)
            if (IsNthWeekdayOfMonth(now, DayOfWeek.Thursday, 4, 11))
                return true;

            // Black Friday (day after Thanksgiving)
            var thanksgiving = GetNthWeekdayOfMonth(now.Year, 4, DayOfWeek.Thursday, 11);
            if (now == thanksgiving.AddDays(1))
                return true;

            return false;
        }

        private static bool IsNthWeekdayOfMonth(DateTime date, DayOfWeek day, int n, int month)
        {
            if (date.Month != month || date.DayOfWeek != day)
                return false;

            int count = 0;
            for (int i = 1; i <= date.Day; i++)
                if (new DateTime(date.Year, month, i).DayOfWeek == day)
                    count++;

            return count == n;
        }

        private static bool IsLastWeekdayOfMonth(DateTime date, DayOfWeek day, int month)
        {
            if (date.Month != month || date.DayOfWeek != day)
                return false;

            int daysInMonth = DateTime.DaysInMonth(date.Year, month);

            DateTime lastOfMonth = new DateTime(date.Year, month, daysInMonth);
            while (lastOfMonth.DayOfWeek != day)
                lastOfMonth = lastOfMonth.AddDays(-1);

            return date == lastOfMonth;
        }

        private static DateTime GetNthWeekdayOfMonth(int year, int n, DayOfWeek day, int month)
        {
            int count = 0;
            for (int i = 1; i <= DateTime.DaysInMonth(year, month); i++)
            {
                var dt = new DateTime(year, month, i);
                if (dt.DayOfWeek == day)
                {
                    count++;
                    if (count == n)
                        return dt;
                }
            }
            return DateTime.MinValue;
        }
    }

}
