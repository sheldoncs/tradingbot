using MetaTrader.Data.Entities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MetaTrader.Util
{
    public static class WeeklyExpectancyTracker
    {
        private static readonly List<TradeRecord> _trades = new();

        // Call this ONCE when a trade closes
        public static void RecordTrade(
            DateTime openTime,
            DateTime closeTime,
            TradeSide side,
            double profit)
        {
            _trades.Add(new TradeRecord
            {
                OpenTime = openTime,
                CloseTime = closeTime,
                Side = side,
                Profit = profit
            });
        }

        // Call this anytime (or once per day / week)
        public static void PrintWeeklyStats()
        {
            var weekStart = DateTime.UtcNow.Date.AddDays(-(int)DateTime.UtcNow.DayOfWeek);
            var weekTrades = _trades.Where(t => t.CloseTime >= weekStart).ToList();

            if (!weekTrades.Any())
            {
                Console.WriteLine("📊 Weekly Expectancy: No trades this week.");
                LogFile.Write("📊 Weekly Expectancy: No trades this week.");
                return;
            }

            int total = weekTrades.Count;
            var wins = weekTrades.Where(t => t.Profit > 0).ToList();
            var losses = weekTrades.Where(t => t.Profit < 0).ToList();

            double winRate = (double)wins.Count / total;
            double lossRate = 1 - winRate;

            double avgWin = wins.Any() ? wins.Average(t => t.Profit) : 0;
            double avgLoss = losses.Any() ? Math.Abs(losses.Average(t => t.Profit)) : 0;

            double expectancy = (winRate * avgWin) - (lossRate * avgLoss);
            double netProfit = weekTrades.Sum(t => t.Profit);

            string report =
                $"📊 WEEKLY EXPECTANCY REPORT\n" +
                $"Trades: {total}\n" +
                $"WinRate: {winRate:P1}\n" +
                $"Avg Win: {avgWin:F2}\n" +
                $"Avg Loss: {avgLoss:F2}\n" +
                $"Expectancy: {expectancy:F2}\n" +
                $"Net P/L: {netProfit:F2}";

            Console.WriteLine(report);
            LogFile.Write(report);
        }
    }

}
