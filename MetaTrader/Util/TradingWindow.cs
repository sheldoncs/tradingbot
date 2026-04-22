using System;
using System.Threading.Tasks;

namespace MetaTrader.Util
{
    public class TradingWindow
    {
        /// <summary>
        /// True if we are between Monday 2:00 AM and Friday 12:00 PM (noon)
        /// </summary>
        public static bool IsTradingWindow(DateTime now)
        {
           
            var day = now.DayOfWeek;
            var time = now.TimeOfDay;

      
            // ❌ Stop all trading at 5 PM Monday–Thursday
            if ((day == DayOfWeek.Monday ||
                 day == DayOfWeek.Tuesday ||
                 day == DayOfWeek.Wednesday ||
                 day == DayOfWeek.Thursday)
                && time >= TimeSpan.FromHours(17) && time < TimeSpan.FromHours(18)) // between 17 = 5 PM and 18 = 6PM
            {
                return false;
            }

            // Monday: only after 02:00
            //if (day == DayOfWeek.Monday)
              //  return time >= TimeSpan.FromHours(2) && time < TimeSpan.FromHours(24);

            // Tuesday, Wednesday, Thursday: full 24 hours
            if (day == DayOfWeek.Monday || 
                day == DayOfWeek.Tuesday ||
                day == DayOfWeek.Wednesday ||
                day == DayOfWeek.Thursday || 
                day == DayOfWeek.Friday)
                return true;

            // Friday: only until 12:00 (midday)
            if (day == DayOfWeek.Friday)
                return time < TimeSpan.FromHours(12);

            // Saturday & Sunday: no trading
            return true;
           
        }

        /// <summary>
        /// Compute the next Monday at 02:00 AM from a given time.
        /// Used when we're outside the trading window.
        /// </summary>
        public static DateTime GetNextMondayAt2am(DateTime now)
        {
            int daysUntilMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;

            DateTime nextMonday = now.Date.AddDays(daysUntilMonday);
            DateTime nextMonday2am = nextMonday.AddHours(2);

            // If it's already Monday after 2 AM and we call this,
            // schedule for NEXT week's Monday 2 AM
            if (nextMonday2am <= now)
                nextMonday2am = nextMonday2am.AddDays(7);

            return nextMonday2am;
        }

        /// <summary>
        /// One "tick" of your trading logic.
        /// This is where you call CopyRates, evaluate signals, open/close trades, etc.
        /// </summary>
        public static async Task<bool> RunTradingTickAsync()
        {
            try
            {
                // TODO: plug in your real strategy here
                // e.g. await RunMyStrategy();
                Console.WriteLine("   → Trading logic executed.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in trading logic: {ex.Message}");
            }

            return await Task.FromResult(false);
            
        }
    }
}
