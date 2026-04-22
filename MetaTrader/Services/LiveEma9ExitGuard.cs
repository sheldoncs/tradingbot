using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Collections.Generic;

namespace MetaTrader.Services
{
    public enum LiveExitDecision
    {
        None,
        ExitToNextRegime,   // close, allow other engine to evaluate next candle
        ExitAndDisarm       // close, disarm symbol
    }

    public static class LiveEma9ExitGuard
    {
        private static readonly Dictionary<string, DateTime> _breachSince = new();

        public static LiveExitDecision Evaluate(
            string symbol,
            MtApi5Client client,
            double atr,
            double ema9,
            double ema21,                 // pass emaSlowH1[^1] if you have it
            TradeAction side,             // Buy/Sell
            EntryExitProfile profile,
            bool tradeOpen,
            bool isShockActive,
            out string reason)
        {
            reason = "";
            if (!tradeOpen || atr <= 0) return LiveExitDecision.None;
            if (isShockActive) return LiveExitDecision.None; // optional: don’t force exit on shock logic

            var cfg = Ema9ExitProfiles.For(profile);

            bool isBuy = side == TradeAction.EnterBuy || side == TradeAction.Buy;
            bool isSell = side == TradeAction.EnterSell || side == TradeAction.Sell;
            if (!isBuy && !isSell) return LiveExitDecision.None;

            double bid = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);

            client.CopyRates(symbol, ENUM_TIMEFRAMES.PERIOD_H1, 0, 2, out MqlRates[] rates);
            if (rates == null || rates.Length < 1) return LiveExitDecision.None;

            var live = rates[0];

            double liveRange = live.high - live.low;
            if (liveRange <= 0) liveRange = 0.0000001;

            double liveBody = Math.Abs(live.close - live.open);
            double liveBodyRatio = liveBody / liveRange;

            // Direction-aware breach:
            // BUY harmed when price BELOW EMA9
            // SELL harmed when price ABOVE EMA9
            double breach = isBuy ? (ema9 - bid) : (bid - ema9);
            bool breached = breach > 0;

            // Optional grace for Sniper/Continuation if near EMA21 (it’s a “deeper pullback”, not failure)
            if (breached && cfg.AllowGraceIfNearEma21)
            {
                double distToEma21 = Math.Abs(bid - ema21);
                if (distToEma21 <= atr * cfg.NearEma21Atr)
                {
                    // Grace: don't exit yet (let the strategy play), but keep timer cleared
                    _breachSince.Remove(symbol);
                    reason = $"Grace active near EMA21 (dist={distToEma21:F5})";
                    LogFile.Write(reason);
                    return LiveExitDecision.None;
                }
            }

            // HARD: distance or expanding candle against the trade
            bool hardDistance = breach >= atr * cfg.HardBreachAtr;

            bool candleAgainst = isBuy ? (live.close < live.open) : (live.close > live.open);

            bool hardExpansion =
                breached &&
                candleAgainst &&
                liveRange >= atr * cfg.HardRangeAtr &&
                liveBodyRatio >= cfg.HardBodyRatio;

            if (hardDistance || hardExpansion)
            {
                _breachSince.Remove(symbol);

                reason = hardDistance
                    ? $"HARD EMA9 breach: breach={breach:F5} (>= {atr * cfg.HardBreachAtr:F5})"
                    : $"HARD expansion against trade while breached: range={liveRange:F5} bodyRatio={liveBodyRatio:F2}";
                LogFile.Write(reason);
                // For Velocity, disarm is reasonable on hard failure.
                // For Sniper/Continuation, you may choose ExitToNextRegime instead — but your request included disarm option.
                return LiveExitDecision.ExitAndDisarm;
            }

            // SOFT: small breach + time confirmation
            bool softDistance = breach >= atr * cfg.SoftBreachAtr;

            if (breached && softDistance)
            {
                // 🟢 NEW: Proven trades IGNORE soft EMA9 breaches
                if (cfg.IgnoreSoftBreachWhenProven)
                {
                    reason = "Soft EMA9 breach ignored (trade proven)";
                    _breachSince.Remove(symbol); // clear timer so it doesn't accumulate
                    return LiveExitDecision.None;
                }

                if (!_breachSince.TryGetValue(symbol, out _))
                    _breachSince[symbol] = DateTime.UtcNow;

                var elapsed = DateTime.UtcNow - _breachSince[symbol];

                if (elapsed.TotalSeconds >= cfg.ConfirmSeconds)
                {
                    _breachSince.Remove(symbol);
                    reason = $"SOFT EMA9 breach confirmed ({elapsed.TotalSeconds:F0}s) breach={breach:F5}";
                    LogFile.Write(reason);
                    return LiveExitDecision.ExitToNextRegime;
                }

                reason = $"EMA9 breach confirming {elapsed.TotalSeconds:F0}s breach={breach:F5}";
                return LiveExitDecision.None;
            }

            // Recovered => clear timer
            if (!breached)
                _breachSince.Remove(symbol);

            return LiveExitDecision.None;
        }
    }
}
