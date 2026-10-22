namespace MetaTrader.Services.Helper
{
    using global::MetaTrader.Util;
    using System;
    using System.Collections.Generic;

    // Diagnostic only. Does NOT authorize trades or call stateful S/R methods.
    public static class GoldEntryTimingShadowDiagnostics
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, DateTime> LastMinute = new Dictionary<string, DateTime>();

        public static void Observe(
            string symbol, double bid, double ask, double liveOpen, double atr,
            double ema9, double ema9Previous, double ema21,
            double recentHigh, double recentLow, double momentum)
        {
            if (string.IsNullOrWhiteSpace(symbol) ||
                !(symbol.IndexOf("XAU", StringComparison.OrdinalIgnoreCase) >= 0 ||
                  symbol.IndexOf("GOLD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                  symbol.IndexOf("XAG", StringComparison.OrdinalIgnoreCase) >= 0 ||
                  symbol.IndexOf("SILVER", StringComparison.OrdinalIgnoreCase) >= 0) ||
                atr <= 0 || bid <= 0 || ask < bid || liveOpen <= 0 ||
                ema9 <= 0 || ema9Previous <= 0 || ema21 <= 0)
                return;

            // Throttle a busy tick loop to one snapshot per symbol per UTC minute.
            DateTime minute = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute *
                TimeSpan.TicksPerMinute, DateTimeKind.Utc);
            lock (Sync)
            {
                DateTime previous;
                if (LastMinute.TryGetValue(symbol, out previous) && previous == minute)
                    return;
                LastMinute[symbol] = minute;
            }

            double mid = (bid + ask) / 2.0;
            double slopeAtr = Math.Abs(ema9 - ema9Previous) / atr;
            double spreadAtr = Math.Abs(ema9 - ema21) / atr;
            double buyBodyAtr = Math.Max(0.0, mid - liveOpen) / atr;
            double sellBodyAtr = Math.Max(0.0, liveOpen - mid) / atr;
            double buyDistanceAtr = Math.Abs(ask - ema9) / atr;
            double sellDistanceAtr = Math.Abs(bid - ema9) / atr;
            double buyBreakAtr = recentHigh > 0 ? (ask - recentHigh) / atr : double.NaN;
            double sellBreakAtr = recentLow > 0 ? (recentLow - bid) / atr : double.NaN;
            bool trendBuy = ema9 > ema21 && ema9 > ema9Previous && ask > ema9 && ask > ema21;
            bool trendSell = ema9 < ema21 && ema9 < ema9Previous && bid < ema9 && bid < ema21;
            bool buyGeometry = mid > liveOpen && trendBuy && buyBodyAtr >= .50 &&
                momentum >= .35 && buyDistanceAtr >= .20 && buyDistanceAtr <= 1.35 &&
                spreadAtr >= .15 && slopeAtr >= .05 &&
                buyBreakAtr >= -.65 && buyBreakAtr <= .35;
            bool sellGeometry = mid < liveOpen && trendSell && sellBodyAtr >= .50 &&
                momentum >= .35 && sellDistanceAtr >= .20 && sellDistanceAtr <= 1.35 &&
                spreadAtr >= .15 && slopeAtr >= .05 &&
                sellBreakAtr >= -.65 && sellBreakAtr <= .35;

            // Intentionally NOT labelled a full impulse pass: real engine has
            // rejection-wick, Gold location and other gates; S/R is not evaluated here.
            LogFile.Write($"[{symbol}] GOLD TIMING SHADOW | utc={minute:O} | " +
                $"syntheticH1=True | mid={mid:F5} | atr={atr:F5} | momentum={momentum:F3} | " +
                $"buyBodyATR={buyBodyAtr:F3} | sellBodyATR={sellBodyAtr:F3} | " +
                $"buyEmaDistATR={buyDistanceAtr:F3} | sellEmaDistATR={sellDistanceAtr:F3} | " +
                $"emaSpreadATR={spreadAtr:F3} | emaSlopeATR={slopeAtr:F3} | " +
                $"buyBreakATR={buyBreakAtr:F3} | sellBreakATR={sellBreakAtr:F3} | " +
                $"trendBuy={trendBuy} | trendSell={trendSell} | " +
                $"accelBuyGeometry={buyGeometry} | accelSellGeometry={sellGeometry} | " +
                "srGate=NOT_EVALUATED | ordersEnabled=False");
        }
    }

}
