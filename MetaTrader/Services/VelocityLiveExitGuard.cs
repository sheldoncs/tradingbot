using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Collections.Generic;

namespace MetaTrader.Services
{
    public enum VelocityLiveExitDecision
    {
        None,
        ExitToSniper,
        ExitAndDisarm
    }

    public static class VelocityLiveExitGuard
    {
        // ---------------------------
        // Tunables (start values)
        // ---------------------------

        // How far past EMA9 counts as acceptance (in ATR units)
        public static double SoftBreachAtr = 0.12;   // small acceptance past EMA9
        public static double HardBreachAtr = 0.25;   // strong break past EMA9

        // Require price to stay past EMA9 for N seconds before acting (avoids wick chop)
        public static int ConfirmSeconds = 20;

        // If live candle is expanding hard against the trade, treat as hard failure
        public static double HardRangeAtr = 0.90;
        public static double HardBodyRatio = 0.55;

        // Per-symbol “breach since” timestamp
        private static readonly Dictionary<string, DateTime> _breachSince = new();

        public static VelocityLiveExitDecision Evaluate(
            string symbol,
            MtApi5Client client,
            double atr,
            double ema9,
            TradeAction side,          // EnterBuy or EnterSell (or your position side)
            bool isVelocityTrade,
            out string reason)
        {
            reason = "";

            if (!isVelocityTrade || atr <= 0)
                return VelocityLiveExitDecision.None;

            bool isBuy = side == TradeAction.EnterBuy || side == TradeAction.Buy;
            bool isSell = side == TradeAction.EnterSell || side == TradeAction.Sell;

            if (!isBuy && !isSell)
                return VelocityLiveExitDecision.None;

            // Live price
            double bid = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);

            // LIVE H1 candle (shift 0)
            client.CopyRates(symbol, ENUM_TIMEFRAMES.PERIOD_H1, 0, 2, out MqlRates[] rates);
            if (rates == null || rates.Length < 1)
                return VelocityLiveExitDecision.None;

            var live = rates[0];

            double liveRange = live.high - live.low;
            if (liveRange <= 0) liveRange = 0.0000001;

            double liveBody = Math.Abs(live.close - live.open);
            double liveBodyRatio = liveBody / liveRange;

            // -----------------------------------------
            // Direction-aware breach definition
            // BUY: breach when price is BELOW EMA9
            // SELL: breach when price is ABOVE EMA9
            // -----------------------------------------
            double breach =
                isBuy ? (ema9 - bid)     // positive => below EMA9 (bad for buy)
                     : (bid - ema9);    // positive => above EMA9 (bad for sell)

            bool isBreached = breach > 0;

            // -----------------------------------------
            // HARD FAIL CONDITIONS
            // -----------------------------------------
            bool hardDistance = breach >= atr * HardBreachAtr;

            // Candle expanding AGAINST the position while breached
            // BUY gets hurt by bearish expansion; SELL gets hurt by bullish expansion
            bool candleAgainst =
                isBuy ? (live.close < live.open) : (live.close > live.open);

            bool hardExpansion =
                isBreached &&
                candleAgainst &&
                liveRange >= atr * HardRangeAtr &&
                liveBodyRatio >= HardBodyRatio;

            if (hardDistance || hardExpansion)
            {
                _breachSince.Remove(symbol);

                reason = hardDistance
                    ? $"Live HARD EMA9 break against trade: breach={breach:F5} (>= {atr * HardBreachAtr:F5})"
                    : $"Live HARD expansion against trade while breached: range={liveRange:F5}, bodyRatio={liveBodyRatio:F2}";
                LogFile.Write(reason);
                return VelocityLiveExitDecision.ExitAndDisarm;
            }

            // -----------------------------------------
            // SOFT FAIL: small breach + time confirmation
            // -----------------------------------------
            bool softDistance = breach >= atr * SoftBreachAtr;

            if (isBreached && softDistance)
            {
                if (!_breachSince.TryGetValue(symbol, out var since))
                    _breachSince[symbol] = DateTime.UtcNow;

                var elapsed = DateTime.UtcNow - _breachSince[symbol];

                if (elapsed.TotalSeconds >= ConfirmSeconds)
                {
                    _breachSince.Remove(symbol);

                    reason = $"Live SOFT EMA9 loss confirmed ({elapsed.TotalSeconds:F0}s) breach={breach:F5}";
                    LogFile.Write(reason);
                    return VelocityLiveExitDecision.ExitToSniper;
                }

                reason = $"Live EMA9 breach confirming {elapsed.TotalSeconds:F0}s breach={breach:F5}";
                LogFile.Write(reason);
                return VelocityLiveExitDecision.None;
            }

            // If price recovered back to the correct side of EMA9, clear timer
            if (!isBreached)
                _breachSince.Remove(symbol);

            return VelocityLiveExitDecision.None;
        }
    }
}
