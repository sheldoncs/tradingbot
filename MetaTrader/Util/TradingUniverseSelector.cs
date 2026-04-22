using System;

namespace MetaTrader.Util
{
    public static class TradingUniverseSelector
    {
        public static bool IsCryptoWindow(DateTime nowAst)
        {
            var day = nowAst.DayOfWeek;
            var time = nowAst.TimeOfDay;

            // Friday after 7pm
            if (day == DayOfWeek.Friday && time >= TimeSpan.FromHours(19))
                return true;

            // Saturday full day
            if (day == DayOfWeek.Saturday)
                return true;

            // Sunday full day
            if (day == DayOfWeek.Sunday)
                return true;

            // Monday before 2pm
            if (day == DayOfWeek.Monday && time < TimeSpan.FromHours(14))
                return true;

            return true;
        }

        public static bool IsGoldWindow(DateTime nowAst)
        {
            return !IsCryptoWindow(nowAst);
        }
    }

}
