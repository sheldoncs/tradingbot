using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Collections.Generic;

namespace MetaTrader.Services
{
    public static class CompressionBreakoutEngine
    {
        private class Profile
        {
            public int CompressionLookback { get; set; }
            public double MaxCompressionRangeAtr { get; set; }
            public double MaxEmaSpreadAtr { get; set; }
            public double BreakoutRangeAtr { get; set; }
            public double MinBodyRatio { get; set; }
            public double BreakoutBufferAtr { get; set; }
            public double MaxDistanceFromEma9Atr { get; set; }
            public double MinMomentum { get; set; }
        }

        private static Profile GetProfile(string symbol)
        {
            string s = NormalizeSymbol(symbol);

            if (IsGold(s))
            {
                return new Profile
                {
                    CompressionLookback = 6,
                    MaxCompressionRangeAtr = 2.20,
                    MaxEmaSpreadAtr = 0.55,
                    BreakoutRangeAtr = 1.15,
                    MinBodyRatio = 0.58,
                    BreakoutBufferAtr = 0.12,
                    MaxDistanceFromEma9Atr = 2.20,
                    MinMomentum = 1.35
                };
            }

            if (IsCrypto(s))
            {
                return new Profile
                {
                    CompressionLookback = 6,
                    MaxCompressionRangeAtr = 2.40,
                    MaxEmaSpreadAtr = 0.65,
                    BreakoutRangeAtr = 1.10,
                    MinBodyRatio = 0.55,
                    BreakoutBufferAtr = 0.10,
                    MaxDistanceFromEma9Atr = 2.50,
                    MinMomentum = 1.25
                };
            }

            // FX default
            return new Profile
            {
                CompressionLookback = 7,
                MaxCompressionRangeAtr = 1.80,
                MaxEmaSpreadAtr = 0.45,
                BreakoutRangeAtr = 0.95,
                MinBodyRatio = 0.55,
                BreakoutBufferAtr = 0.08,
                MaxDistanceFromEma9Atr = 1.70,
                MinMomentum = 1.05
            };
        }

        public static bool ShouldEnter(
            string symbol,
            MqlRates[] rates,
            double atr,
            double[] emaFast,
            double[] emaSlow,
            double momentum,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            try
            {
                if (string.IsNullOrWhiteSpace(symbol) ||
                    rates == null ||
                    emaFast == null ||
                    emaSlow == null ||
                    atr <= 0 ||
                    rates.Length < 20 ||
                    emaFast.Length < 3 ||
                    emaSlow.Length < 3)
                {
                    return false;
                }

                string s = NormalizeSymbol(symbol);
                var profile = GetProfile(s);

                var current = rates[^1];

                double ema9 = emaFast[^1];
                double ema21 = emaSlow[^1];

                double emaSpreadAtr = Math.Abs(ema9 - ema21) / atr;

                if (emaSpreadAtr > profile.MaxEmaSpreadAtr)
                {
                    LogFile.Write(
                        $"[{symbol}] CompressionBreakout blocked: EMAs not compressed | " +
                        $"emaSpreadAtr={emaSpreadAtr:F2}, max={profile.MaxEmaSpreadAtr:F2}"
                    );
                    return false;
                }

                int lookback = Math.Min(profile.CompressionLookback, rates.Length - 2);

                double compressionHigh = double.MinValue;
                double compressionLow = double.MaxValue;

                for (int i = 2; i <= lookback + 1; i++)
                {
                    compressionHigh = Math.Max(compressionHigh, rates[^i].high);
                    compressionLow = Math.Min(compressionLow, rates[^i].low);
                }

                double compressionRangeAtr = (compressionHigh - compressionLow) / atr;

                if (compressionRangeAtr > profile.MaxCompressionRangeAtr)
                {
                    LogFile.Write(
                        $"[{symbol}] CompressionBreakout blocked: range not compressed | " +
                        $"rangeAtr={compressionRangeAtr:F2}, max={profile.MaxCompressionRangeAtr:F2}"
                    );
                    return false;
                }

                double currentRange = current.high - current.low;
                if (currentRange <= 0)
                    return false;

                double body = Math.Abs(current.close - current.open);
                double bodyRatio = body / currentRange;

                bool strongBreakoutCandle =
                    currentRange >= atr * profile.BreakoutRangeAtr &&
                    bodyRatio >= profile.MinBodyRatio;

                if (!strongBreakoutCandle)
                {
                    LogFile.Write(
                        $"[{symbol}] CompressionBreakout blocked: weak breakout candle | " +
                        $"rangeAtr={(currentRange / atr):F2}, bodyRatio={bodyRatio:F2}"
                    );
                    return false;
                }

                if (Math.Abs(momentum) < profile.MinMomentum)
                {
                    LogFile.Write(
                        $"[{symbol}] CompressionBreakout blocked: momentum too weak | " +
                        $"momentum={momentum:F2}, need>={profile.MinMomentum:F2}"
                    );
                    return false;
                }

                double breakoutBuffer = atr * profile.BreakoutBufferAtr;

                bool bullishBreakout =
                    current.close > compressionHigh + breakoutBuffer &&
                    current.close > current.open &&
                    ema9 >= ema21;

                bool bearishBreakout =
                    current.close < compressionLow - breakoutBuffer &&
                    current.close < current.open &&
                    ema9 <= ema21;

                if (!bullishBreakout && !bearishBreakout)
                {
                    LogFile.Write(
                        $"[{symbol}] CompressionBreakout blocked: no clean break | " +
                        $"close={current.close:F5}, high={compressionHigh:F5}, low={compressionLow:F5}"
                    );
                    return false;
                }

                double distanceFromEma9Atr = Math.Abs(current.close - ema9) / atr;

                if (distanceFromEma9Atr > profile.MaxDistanceFromEma9Atr)
                {
                    LogFile.Write(
                        $"[{symbol}] CompressionBreakout blocked: too far from EMA9 | " +
                        $"distEMA9Atr={distanceFromEma9Atr:F2}, max={profile.MaxDistanceFromEma9Atr:F2}"
                    );
                    return false;
                }

                if (bullishBreakout)
                {
                    action = TradeAction.EnterBuy;

                    LogFile.Write(
                        $"[{symbol}] ✅ CompressionBreakout BUY | " +
                        $"close={current.close:F5}, compressionHigh={compressionHigh:F5}, " +
                        $"rangeAtr={compressionRangeAtr:F2}, bodyRatio={bodyRatio:F2}, " +
                        $"momentum={momentum:F2}, distEMA9Atr={distanceFromEma9Atr:F2}"
                    );

                    return true;
                }

                if (bearishBreakout)
                {
                    action = TradeAction.EnterSell;

                    LogFile.Write(
                        $"[{symbol}] ✅ CompressionBreakout SELL | " +
                        $"close={current.close:F5}, compressionLow={compressionLow:F5}, " +
                        $"rangeAtr={compressionRangeAtr:F2}, bodyRatio={bodyRatio:F2}, " +
                        $"momentum={momentum:F2}, distEMA9Atr={distanceFromEma9Atr:F2}"
                    );

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                LogFile.Write($"[{symbol}] CompressionBreakout error: {ex.Message}");
                return false;
            }
        }

        private class LiveBreakState
        {
            public bool BullBreakDetected;
            public bool BearBreakDetected;
            public DateTime FirstBreakTimeUtc;
        }

        private static readonly Dictionary<string, LiveBreakState> _liveBreakStates = new();

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
                atr <= 0 ||
                rates.Length < 20 ||
                emaFast.Length < 3 ||
                emaSlow.Length < 3)
                return false;

            string s = NormalizeSymbol(symbol);
            var profile = GetProfile(s);

            if (!_liveBreakStates.ContainsKey(s))
                _liveBreakStates[s] = new LiveBreakState();

            var state = _liveBreakStates[s];

            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];

            double emaSpreadAtr = Math.Abs(ema9 - ema21) / atr;

            if (emaSpreadAtr > profile.MaxEmaSpreadAtr)
            {
                ResetLiveBreakState(s);
                return false;
            }

            int lookback = Math.Min(profile.CompressionLookback, rates.Length - 2);

            double compressionHigh = double.MinValue;
            double compressionLow = double.MaxValue;

            for (int i = 2; i <= lookback + 1; i++)
            {
                compressionHigh = Math.Max(compressionHigh, rates[^i].high);
                compressionLow = Math.Min(compressionLow, rates[^i].low);
            }

            double compressionRangeAtr = (compressionHigh - compressionLow) / atr;

            if (compressionRangeAtr > profile.MaxCompressionRangeAtr)
            {
                ResetLiveBreakState(s);
                return false;
            }

            if (Math.Abs(momentum) < profile.MinMomentum)
            {
                ResetLiveBreakState(s);
                return false;
            }

            double breakoutBuffer = atr * profile.BreakoutBufferAtr;

            double liveBuyPrice = ask;
            double liveSellPrice = bid;

            bool liveBullBreak =
                liveBuyPrice > compressionHigh + breakoutBuffer &&
                ema9 >= ema21;

            bool liveBearBreak =
                liveSellPrice < compressionLow - breakoutBuffer &&
                ema9 <= ema21;

            double distBuyFromEma9Atr = Math.Abs(liveBuyPrice - ema9) / atr;
            double distSellFromEma9Atr = Math.Abs(liveSellPrice - ema9) / atr;

            bool buyTooExtended =
                distBuyFromEma9Atr > profile.MaxDistanceFromEma9Atr;

            bool sellTooExtended =
                distSellFromEma9Atr > profile.MaxDistanceFromEma9Atr;

            int holdSeconds = GetLiveHoldSeconds(s);

            // ============================
            // LIVE BULLISH BREAKOUT
            // ============================
            if (liveBullBreak && !buyTooExtended)
            {
                if (!state.BullBreakDetected)
                {
                    state.BullBreakDetected = true;
                    state.BearBreakDetected = false;
                    state.FirstBreakTimeUtc = DateTime.UtcNow;

                    LogFile.Write(
                        $"[{symbol}] LIVE CompressionBreakout BUY detected — confirming hold | " +
                        $"ask={ask:F5}, compressionHigh={compressionHigh:F5}, " +
                        $"holdSeconds={holdSeconds}, momentum={momentum:F2}, " +
                        $"distEMA9Atr={distBuyFromEma9Atr:F2}"
                    );

                    return false;
                }

                double heldFor =
                    (DateTime.UtcNow - state.FirstBreakTimeUtc).TotalSeconds;

                if (heldFor >= holdSeconds)
                {
                    action = TradeAction.EnterBuy;
                    ResetLiveBreakState(s);

                    LogFile.Write(
                        $"[{symbol}] ✅ LIVE CompressionBreakout BUY CONFIRMED | " +
                        $"ask={ask:F5}, compressionHigh={compressionHigh:F5}, " +
                        $"heldFor={heldFor:F1}s, rangeAtr={compressionRangeAtr:F2}, " +
                        $"momentum={momentum:F2}, distEMA9Atr={distBuyFromEma9Atr:F2}"
                    );

                    return true;
                }

                LogFile.Write(
                    $"[{symbol}] LIVE CompressionBreakout BUY holding | " +
                    $"heldFor={heldFor:F1}s/{holdSeconds}s, ask={ask:F5}, " +
                    $"compressionHigh={compressionHigh:F5}"
                );

                return false;
            }

            // ============================
            // LIVE BEARISH BREAKOUT
            // ============================
            if (liveBearBreak && !sellTooExtended)
            {
                if (!state.BearBreakDetected)
                {
                    state.BearBreakDetected = true;
                    state.BullBreakDetected = false;
                    state.FirstBreakTimeUtc = DateTime.UtcNow;

                    LogFile.Write(
                        $"[{symbol}] LIVE CompressionBreakout SELL detected — confirming hold | " +
                        $"bid={bid:F5}, compressionLow={compressionLow:F5}, " +
                        $"holdSeconds={holdSeconds}, momentum={momentum:F2}, " +
                        $"distEMA9Atr={distSellFromEma9Atr:F2}"
                    );

                    return false;
                }

                double heldFor =
                    (DateTime.UtcNow - state.FirstBreakTimeUtc).TotalSeconds;

                if (heldFor >= holdSeconds)
                {
                    action = TradeAction.EnterSell;
                    ResetLiveBreakState(s);

                    LogFile.Write(
                        $"[{symbol}] ✅ LIVE CompressionBreakout SELL CONFIRMED | " +
                        $"bid={bid:F5}, compressionLow={compressionLow:F5}, " +
                        $"heldFor={heldFor:F1}s, rangeAtr={compressionRangeAtr:F2}, " +
                        $"momentum={momentum:F2}, distEMA9Atr={distSellFromEma9Atr:F2}"
                    );

                    return true;
                }

                LogFile.Write(
                    $"[{symbol}] LIVE CompressionBreakout SELL holding | " +
                    $"heldFor={heldFor:F1}s/{holdSeconds}s, bid={bid:F5}, " +
                    $"compressionLow={compressionLow:F5}"
                );

                return false;
            }

            // If price falls back inside compression, reset state
            if (state.BullBreakDetected &&
                liveBuyPrice <= compressionHigh)
            {
                LogFile.Write(
                    $"[{symbol}] LIVE CompressionBreakout BUY reset: price fell back inside compression | " +
                    $"ask={ask:F5}, compressionHigh={compressionHigh:F5}"
                );

                ResetLiveBreakState(s);
            }

            if (state.BearBreakDetected &&
                liveSellPrice >= compressionLow)
            {
                LogFile.Write(
                    $"[{symbol}] LIVE CompressionBreakout SELL reset: price returned inside compression | " +
                    $"bid={bid:F5}, compressionLow={compressionLow:F5}"
                );

                ResetLiveBreakState(s);
            }

            return false;
        }

        private static void ResetLiveBreakState(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            if (!_liveBreakStates.ContainsKey(symbol))
                _liveBreakStates[symbol] = new LiveBreakState();

            _liveBreakStates[symbol].BullBreakDetected = false;
            _liveBreakStates[symbol].BearBreakDetected = false;
            _liveBreakStates[symbol].FirstBreakTimeUtc = DateTime.MinValue;
        }

        private static int GetLiveHoldSeconds(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            if (IsGold(symbol)) return 18;
            if (IsCrypto(symbol)) return 12;

            return 15; // FX
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
