using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Collections.Generic;

namespace MetaTrader.Services
{
    public static class BreakoutContinuationEngine
    {
        private class Profile
        {
            public int StructureLookback { get; set; }
            public double BreakBufferAtr { get; set; }
            public double MaxDistanceFromEma9Atr { get; set; }
            public double MaxDistanceFromEma21Atr { get; set; }
            public double MinMomentum { get; set; }
            public double MinEmaSpreadAtr { get; set; }
            public int HoldSeconds { get; set; }
        }

        private class LiveBreakState
        {
            public bool BullBreakDetected;
            public bool BearBreakDetected;
            public DateTime FirstBreakTimeUtc;
        }

        private static readonly Dictionary<string, LiveBreakState> _states = new();

        public static bool ShouldEnterLive(
            string symbol,
            MqlRates[] rates,
            double atr,
            double[] emaFast,
            double[] emaSlow,
            double momentum,
            double bid,
            double ask,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            if (string.IsNullOrWhiteSpace(symbol) ||
                rates == null ||
                emaFast == null ||
                emaSlow == null ||
                rates.Length < 20 ||
                emaFast.Length < 3 ||
                emaSlow.Length < 3 ||
                atr <= 0)
            {
                return false;
            }

            string s = NormalizeSymbol(symbol);
            var profile = GetProfile(s);

            if (!_states.ContainsKey(s))
                _states[s] = new LiveBreakState();

            var state = _states[s];

            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];

            bool trendUp =
                ema9 > ema21 &&
                emaFast[^1] > emaFast[^2];

            bool trendDown =
                ema9 < ema21 &&
                emaFast[^1] < emaFast[^2];

            if (!trendUp && !trendDown)
            {
                ResetState(s);
                return false;
            }

            double emaSpreadAtr = Math.Abs(ema9 - ema21) / atr;

            if (emaSpreadAtr < profile.MinEmaSpreadAtr)
            {
                ResetState(s);
                LogFile.Write(
                    $"[{symbol}] BreakoutContinuation blocked: EMA spread too small | " +
                    $"spreadAtr={emaSpreadAtr:F2}, need>={profile.MinEmaSpreadAtr:F2}"
                );
                return false;
            }

            if (Math.Abs(momentum) < profile.MinMomentum)
            {
                ResetState(s);
                LogFile.Write(
                    $"[{symbol}] BreakoutContinuation blocked: momentum too weak | " +
                    $"momentum={momentum:F2}, need>={profile.MinMomentum:F2}"
                );
                return false;
            }

            int lookback = Math.Min(profile.StructureLookback, rates.Length - 2);

            double recentHigh = double.MinValue;
            double recentLow = double.MaxValue;

            for (int i = 2; i <= lookback + 1; i++)
            {
                recentHigh = Math.Max(recentHigh, rates[^i].high);
                recentLow = Math.Min(recentLow, rates[^i].low);
            }

            double buffer = atr * profile.BreakBufferAtr;

            double liveBuyPrice = ask;
            double liveSellPrice = bid;

            bool liveBullBreak =
                trendUp &&
                liveBuyPrice > recentHigh + buffer;

            bool liveBearBreak =
                trendDown &&
                liveSellPrice < recentLow - buffer;

            double distBuyFromEma9Atr = Math.Abs(liveBuyPrice - ema9) / atr;
            double distBuyFromEma21Atr = Math.Abs(liveBuyPrice - ema21) / atr;

            double distSellFromEma9Atr = Math.Abs(liveSellPrice - ema9) / atr;
            double distSellFromEma21Atr = Math.Abs(liveSellPrice - ema21) / atr;

            bool buyTooExtended =
                distBuyFromEma9Atr > profile.MaxDistanceFromEma9Atr ||
                distBuyFromEma21Atr > profile.MaxDistanceFromEma21Atr;

            bool sellTooExtended =
                distSellFromEma9Atr > profile.MaxDistanceFromEma9Atr ||
                distSellFromEma21Atr > profile.MaxDistanceFromEma21Atr;

            // ============================
            // LIVE BULLISH BREAKOUT CONTINUATION
            // ============================
            if (liveBullBreak && !buyTooExtended)
            {
                if (!state.BullBreakDetected)
                {
                    state.BullBreakDetected = true;
                    state.BearBreakDetected = false;
                    state.FirstBreakTimeUtc = DateTime.UtcNow;

                    LogFile.Write(
                        $"[{symbol}] BreakoutContinuation BUY detected — confirming hold | " +
                        $"ask={ask:F5}, recentHigh={recentHigh:F5}, " +
                        $"momentum={momentum:F2}, hold={profile.HoldSeconds}s"
                    );

                    return false;
                }

                double heldFor =
                    (DateTime.UtcNow - state.FirstBreakTimeUtc).TotalSeconds;

                if (heldFor >= profile.HoldSeconds)
                {
                    action = TradeAction.EnterBuy;
                    ResetState(s);

                    LogFile.Write(
                        $"[{symbol}] ✅ LIVE BreakoutContinuation BUY CONFIRMED | " +
                        $"ask={ask:F5}, recentHigh={recentHigh:F5}, " +
                        $"heldFor={heldFor:F1}s, momentum={momentum:F2}, " +
                        $"distEMA9Atr={distBuyFromEma9Atr:F2}, distEMA21Atr={distBuyFromEma21Atr:F2}"
                    );

                    return true;
                }

                return false;
            }

            // ============================
            // LIVE BEARISH BREAKOUT CONTINUATION
            // ============================
            if (liveBearBreak && !sellTooExtended)
            {
                if (!state.BearBreakDetected)
                {
                    state.BearBreakDetected = true;
                    state.BullBreakDetected = false;
                    state.FirstBreakTimeUtc = DateTime.UtcNow;

                    LogFile.Write(
                        $"[{symbol}] BreakoutContinuation SELL detected — confirming hold | " +
                        $"bid={bid:F5}, recentLow={recentLow:F5}, " +
                        $"momentum={momentum:F2}, hold={profile.HoldSeconds}s"
                    );

                    return false;
                }

                double heldFor =
                    (DateTime.UtcNow - state.FirstBreakTimeUtc).TotalSeconds;

                if (heldFor >= profile.HoldSeconds)
                {
                    action = TradeAction.EnterSell;
                    ResetState(s);

                    LogFile.Write(
                        $"[{symbol}] ✅ LIVE BreakoutContinuation SELL CONFIRMED | " +
                        $"bid={bid:F5}, recentLow={recentLow:F5}, " +
                        $"heldFor={heldFor:F1}s, momentum={momentum:F2}, " +
                        $"distEMA9Atr={distSellFromEma9Atr:F2}, distEMA21Atr={distSellFromEma21Atr:F2}"
                    );

                    return true;
                }

                return false;
            }

            if (state.BullBreakDetected &&
                liveBuyPrice <= recentHigh)
            {
                LogFile.Write(
                    $"[{symbol}] BreakoutContinuation BUY reset: price fell back below breakout | " +
                    $"ask={ask:F5}, recentHigh={recentHigh:F5}"
                );

                ResetState(s);
            }

            if (state.BearBreakDetected &&
                liveSellPrice >= recentLow)
            {
                LogFile.Write(
                    $"[{symbol}] BreakoutContinuation SELL reset: price returned above breakdown | " +
                    $"bid={bid:F5}, recentLow={recentLow:F5}"
                );

                ResetState(s);
            }

            return false;
        }

        private static Profile GetProfile(string symbol)
        {
            string s = NormalizeSymbol(symbol);

            if (IsGold(s))
            {
                return new Profile
                {
                    StructureLookback = 5,
                    BreakBufferAtr = 0.12,
                    MaxDistanceFromEma9Atr = 2.30,
                    MaxDistanceFromEma21Atr = 3.20,
                    MinMomentum = 1.60,
                    MinEmaSpreadAtr = 0.20,
                    HoldSeconds = 18
                };
            }

            if (IsCrypto(s))
            {
                return new Profile
                {
                    StructureLookback = 5,
                    BreakBufferAtr = 0.10,
                    MaxDistanceFromEma9Atr = 2.60,
                    MaxDistanceFromEma21Atr = 3.50,
                    MinMomentum = 1.50,
                    MinEmaSpreadAtr = 0.18,
                    HoldSeconds = 12
                };
            }

            // FX
            return new Profile
            {
                StructureLookback = 5,
                BreakBufferAtr = 0.08,
                MaxDistanceFromEma9Atr = 1.90,
                MaxDistanceFromEma21Atr = 2.80,
                MinMomentum = 1.25,
                MinEmaSpreadAtr = 0.15,
                HoldSeconds = 15
            };
        }

        private static void ResetState(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            if (!_states.ContainsKey(symbol))
                _states[symbol] = new LiveBreakState();

            _states[symbol].BullBreakDetected = false;
            _states[symbol].BearBreakDetected = false;
            _states[symbol].FirstBreakTimeUtc = DateTime.MinValue;
        }

        private static string NormalizeSymbol(string symbol)
        {
            return string.IsNullOrWhiteSpace(symbol)
                ? ""
                : symbol.Replace(".s", "")
                        .Replace("_i", "")
                        .Replace("m", "")
                        .ToUpperInvariant();
        }

        private static bool IsGold(string symbol)
        {
            return symbol.Contains("XAU") ||
                   symbol.Contains("XAG") ||
                   symbol.Contains("GOLD") ||
                   symbol.Contains("SILVER");
        }

        private static bool IsCrypto(string symbol)
        {
            return symbol.Contains("BTC") ||
                   symbol.Contains("ETH") ||
                   symbol.Contains("SOL") ||
                   symbol.Contains("BNB") ||
                   symbol.Contains("XRP") ||
                   symbol.Contains("ADA") ||
                   symbol.Contains("AVAX") ||
                   symbol.Contains("LINK") ||
                   symbol.Contains("LTC") ||
                   symbol.Contains("DOGE");
        }
    }
}
