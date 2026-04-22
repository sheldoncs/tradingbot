using System;

namespace MetaTrader.Util
{
    public static class EveningShutdownFilter
    {
        private static readonly TimeZoneInfo EstZone =
            TimeZoneInfo.FindSystemTimeZoneById("Atlantic Standard Time");

        private static readonly TimeSpan ShutStart = new TimeSpan(18, 0, 0); // 5 PM EST
        private static readonly TimeSpan ShutEnd = new TimeSpan(19, 0, 0); // 7 PM EST

        public static bool IsEveningShutdown(DateTime nowUtc)
        {
            DateTime nowEst = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, EstZone);
            TimeSpan t = nowEst.TimeOfDay;

            // If the time is between 5 PM and 7 PM → shutdown
            if (t >= ShutStart && t < ShutEnd)
                return true;

            return false;
        }
    }

}
