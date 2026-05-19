using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MetaTrader.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using System;
    using System.Collections.Generic;
    using System.Linq;

    public static class VelocityEntryEngine
    {
        private static readonly Dictionary<string, DateTime> _lastVelocityEntry = new();

        // ===============================
        // SYMBOL PROFILES
        // ===============================
        private class VelocitySettings
        {
            public double MinAtrPercentile { get; set; }
            public double MoveFromOpenFactor { get; set; }
            public double MaxDistanceAtr { get; set; }
            public double LateMoveAtr { get; set; }
            public double ResistanceBufferAtr { get; set; }
            public double MomentumFactor { get; set; }
            public double MinLiveMoveAtr { get; set; }
            public double PreviousImpulseBlockAtr { get; set; }
            public int StructureLookback { get; set; }

            // New anti-chop settings
            public double MinEmaSpreadAtr { get; set; }
            public double StructureBreakBufferAtr { get; set; }
            public double MinCandleRangeAtr { get; set; }
            public double MinCandleBodyRatio { get; set; }
        }

        private static readonly VelocitySettings GoldProfile = new()
        {
            MinAtrPercentile = 0.45,
            MoveFromOpenFactor = 0.50,
            MaxDistanceAtr = 1.45,
            LateMoveAtr = 1.45,
            ResistanceBufferAtr = 0.35,
            MomentumFactor = 0.55,
            MinLiveMoveAtr = 0.20,
            PreviousImpulseBlockAtr = 2.20,
            StructureLookback = 5,

            MinEmaSpreadAtr = 0.10,
            StructureBreakBufferAtr = 0.08,
            MinCandleRangeAtr = 0.45,
            MinCandleBodyRatio = 0.38
        };

        private static readonly VelocitySettings CryptoProfile = new()
        {
            MinAtrPercentile = 0.50,
            MoveFromOpenFactor = 0.60,
            MaxDistanceAtr = 1.65,
            LateMoveAtr = 1.65,
            ResistanceBufferAtr = 0.45,
            MomentumFactor = 0.55,
            MinLiveMoveAtr = 0.22,
            PreviousImpulseBlockAtr = 2.35,
            StructureLookback = 5,

            MinEmaSpreadAtr = 0.12,
            StructureBreakBufferAtr = 0.10,
            MinCandleRangeAtr = 0.50,
            MinCandleBodyRatio = 0.36
        };

        private static readonly VelocitySettings FxProfile = new()
        {
            MinAtrPercentile = 0.50,
            MoveFromOpenFactor = 0.45,
            MaxDistanceAtr = 1.05,
            LateMoveAtr = 1.05,
            ResistanceBufferAtr = 0.20,
            MomentumFactor = 0.50,
            MinLiveMoveAtr = 0.18,
            PreviousImpulseBlockAtr = 2.00,
            StructureLookback = 5,

            MinEmaSpreadAtr = 0.08,
            StructureBreakBufferAtr = 0.06,
            MinCandleRangeAtr = 0.35,
            MinCandleBodyRatio = 0.35
        };

        private static readonly VelocitySettings IndexProfile = new()
        {
            MinAtrPercentile = 0.50,
            MoveFromOpenFactor = 0.55,
            MaxDistanceAtr = 1.35,
            LateMoveAtr = 1.35,
            ResistanceBufferAtr = 0.30,
            MomentumFactor = 0.55,
            MinLiveMoveAtr = 0.20,
            PreviousImpulseBlockAtr = 2.20,
            StructureLookback = 5,

            MinEmaSpreadAtr = 0.10,
            StructureBreakBufferAtr = 0.08,
            MinCandleRangeAtr = 0.45,
            MinCandleBodyRatio = 0.36
        };

        public static int CooldownMinutes = 30;
        public static double MaxSlippageFactor = 0.30;

        public static bool ShouldEnter(
            string symbol,
            MtApi5Client client,
            double atr,
            double atrPercentile,
            double[] emaFast,
            double[] emaSlow,
            SymbolContext ctx,
            VelocityProfile profile,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            if (string.IsNullOrWhiteSpace(symbol) ||
                client == null ||
                ctx == null ||
                emaFast == null ||
                emaSlow == null ||
                emaFast.Length < 3 ||
                emaSlow.Length < 3 ||
                atr <= 0)
            {
                return false;
            }

            // ===============================
            // ARMED STATE GATE
            // ===============================
            if (ctx.State != MarketState.Armed)
            {
                LogFile.Write($"[Velocity] {symbol} blocked: market state is not Armed. State={ctx.State}");
                return false;
            }

            var settings = GetSettings(symbol);

            double normalizedAtrPercentile = atrPercentile > 1.0
                ? atrPercentile / 100.0
                : atrPercentile;

            // ===============================
            // ATR REGIME FILTER
            // ===============================
            if (normalizedAtrPercentile < settings.MinAtrPercentile)
            {
                LogFile.Write(
                    $"[Velocity] {symbol} blocked: ATR percentile too low | " +
                    $"atrPct={normalizedAtrPercentile:F2}, need>={settings.MinAtrPercentile:F2}"
                );
                return false;
            }

            double bid = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);
            double ask = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);

            if (bid <= 0 || ask <= 0 || ask <= bid)
            {
                LogFile.Write($"[Velocity] {symbol} blocked: invalid bid/ask | bid={bid}, ask={ask}");
                return false;
            }

            // ===============================
            // SPREAD FILTER
            // ===============================
            long spreadPoints = client.SymbolInfoInteger(symbol, ENUM_SYMBOL_INFO_INTEGER.SYMBOL_SPREAD);
            double point = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_POINT);
            double spread = spreadPoints * point;

            if (point <= 0 || spread <= 0)
            {
                LogFile.Write($"[Velocity] {symbol} blocked: invalid spread/point | spread={spread}, point={point}");
                return false;
            }

            if (spread > atr * MaxSlippageFactor)
            {
                LogFile.Write(
                    $"[Velocity] {symbol} blocked: spread too high | " +
                    $"spread={spread:F5}, max={atr * MaxSlippageFactor:F5}"
                );
                return false;
            }

            // ===============================
            // COOLDOWN
            // ===============================
            string symbolKey = symbol.ToUpperInvariant();

            if (_lastVelocityEntry.TryGetValue(symbolKey, out DateTime lastTime))
            {
                double minutesSinceLastEntry = (DateTime.UtcNow - lastTime).TotalMinutes;

                if (minutesSinceLastEntry < CooldownMinutes)
                {
                    LogFile.Write(
                        $"[Velocity] {symbol} blocked: cooldown active | " +
                        $"{minutesSinceLastEntry:F1}/{CooldownMinutes} minutes"
                    );
                    return false;
                }
            }

            // ===============================
            // RATES
            // ===============================
            client.CopyRates(symbol, ENUM_TIMEFRAMES.PERIOD_H1, 1, 12, out MqlRates[] rates);

            if (rates == null || rates.Length < settings.StructureLookback + 3)
            {
                LogFile.Write($"[Velocity] {symbol} blocked: insufficient H1 rates");
                return false;
            }

            var c0 = rates[^1]; // live/current H1 candle from copied data
            var c1 = rates[^2]; // previous candle

            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];

            double fastSlope = emaFast[^1] - emaFast[^2];
            double slowSlope = emaSlow[^1] - emaSlow[^2];

            bool trendUp = ema9 > ema21 && fastSlope > 0;
            bool trendDown = ema9 < ema21 && fastSlope < 0;

            if (!trendUp && !trendDown)
            {
                LogFile.Write(
                    $"[Velocity] {symbol} blocked: no clean EMA direction | " +
                    $"ema9={ema9:F5}, ema21={ema21:F5}, fastSlope={fastSlope:F5}"
                );
                return false;
            }

            // ===============================
            // EMA SPREAD / COMPRESSION CHECK
            // Prevent firing inside EMA chop.
            // ===============================
            double emaSpread = Math.Abs(ema9 - ema21);
            double emaSpreadAtr = emaSpread / atr;

            bool emaCompressed = emaSpreadAtr < settings.MinEmaSpreadAtr;

            // ===============================
            // PREVIOUS IMPULSE BLOCK — SOFTENED
            // ===============================
            double prevRange = c1.high - c1.low;

            double distanceBuy = Math.Abs(ask - ema9);
            double distanceSell = Math.Abs(bid - ema9);

            bool previousWasImpulse = prevRange > atr * settings.PreviousImpulseBlockAtr;
            bool buyStretched = distanceBuy > atr * settings.LateMoveAtr;
            bool sellStretched = distanceSell > atr * settings.LateMoveAtr;

            if (trendUp && previousWasImpulse && buyStretched)
            {
                LogFile.Write(
                    $"[Velocity] {symbol} blocked: previous impulse + stretched buy | " +
                    $"prevRange={prevRange:F5}, distEMA9={distanceBuy:F5}"
                );
                return false;
            }

            if (trendDown && previousWasImpulse && sellStretched)
            {
                LogFile.Write(
                    $"[Velocity] {symbol} blocked: previous impulse + stretched sell | " +
                    $"prevRange={prevRange:F5}, distEMA9={distanceSell:F5}"
                );
                return false;
            }

            // ===============================
            // LIVE MOVE
            // ===============================
            double liveOpen = c0.open;

            double moveBuy = Math.Abs(ask - liveOpen);
            double moveSell = Math.Abs(bid - liveOpen);

            bool liveBuyMove = ask > liveOpen && moveBuy >= atr * settings.MoveFromOpenFactor;
            bool liveSellMove = bid < liveOpen && moveSell >= atr * settings.MoveFromOpenFactor;

            // ===============================
            // LIVE CANDLE QUALITY
            // Velocity should not fire on tiny chop candles.
            // ===============================
            double liveRange = c0.high - c0.low;

            if (liveRange <= 0)
            {
                LogFile.Write($"[Velocity] {symbol} blocked: invalid live candle range");
                return false;
            }

            double liveBody = Math.Abs(c0.close - c0.open);
            double liveBodyRatio = liveBody / liveRange;

            bool liveCandleHasRange = liveRange >= atr * settings.MinCandleRangeAtr;
            bool liveCandleHasBody = liveBodyRatio >= settings.MinCandleBodyRatio;

            if (!liveCandleHasRange || !liveCandleHasBody)
            {
                LogFile.Write(
                    $"[Velocity] {symbol} blocked: weak live candle quality | " +
                    $"rangeAtr={(liveRange / atr):F2}, need>={settings.MinCandleRangeAtr:F2}, " +
                    $"bodyRatio={liveBodyRatio:F2}, need>={settings.MinCandleBodyRatio:F2}"
                );
                return false;
            }

            // ===============================
            // EMA EXTENSION FILTER
            // ===============================
            double maxDistance = atr * settings.MaxDistanceAtr;

            if (trendUp && distanceBuy > maxDistance)
            {
                LogFile.Write(
                    $"[Velocity] {symbol} blocked: buy too far from EMA9 | " +
                    $"dist={distanceBuy:F5}, max={maxDistance:F5}"
                );
                return false;
            }

            if (trendDown && distanceSell > maxDistance)
            {
                LogFile.Write(
                    $"[Velocity] {symbol} blocked: sell too far from EMA9 | " +
                    $"dist={distanceSell:F5}, max={maxDistance:F5}"
                );
                return false;
            }

            // ===============================
            // STRUCTURE FILTER — EXCLUDE CURRENT LIVE CANDLE
            // ===============================
            var structureCandles = rates
                .Take(rates.Length - 1)
                .Skip(Math.Max(0, rates.Length - 1 - settings.StructureLookback))
                .ToArray();

            if (structureCandles.Length < settings.StructureLookback)
            {
                LogFile.Write($"[Velocity] {symbol} blocked: insufficient structure candles");
                return false;
            }

            double recentHigh = structureCandles.Max(r => r.high);
            double recentLow = structureCandles.Min(r => r.low);

            double structureBuffer = atr * settings.StructureBreakBufferAtr;

            bool trueBreakHigh = ask > recentHigh + structureBuffer;
            bool trueBreakLow = bid < recentLow - structureBuffer;

            bool breakingHigh = ask > recentHigh;
            bool breakingLow = bid < recentLow;

            bool nearResistance = Math.Abs(ask - recentHigh) < atr * settings.ResistanceBufferAtr;
            bool nearSupport = Math.Abs(bid - recentLow) < atr * settings.ResistanceBufferAtr;

            bool insideStructure =
                ask <= recentHigh + structureBuffer &&
                bid >= recentLow - structureBuffer;

            // ===============================
            // ANTI-CHOP / STRUCTURE ESCAPE GUARD
            // This is the key upgrade.
            // ===============================
            if (emaCompressed && insideStructure)
            {
                LogFile.Write(
                    $"[Velocity] {symbol} blocked: EMA compression / chop | " +
                    $"emaSpreadAtr={emaSpreadAtr:F2}, need>={settings.MinEmaSpreadAtr:F2}, " +
                    $"ask={ask:F5}, bid={bid:F5}, recentHigh={recentHigh:F5}, recentLow={recentLow:F5}"
                );
                return false;
            }

            // Velocity BUY must escape recent highs.
            if (trendUp && !trueBreakHigh)
            {
                LogFile.Write(
                    $"[Velocity] {symbol} blocked: BUY has not escaped structure | " +
                    $"ask={ask:F5}, recentHigh={recentHigh:F5}, need>{recentHigh + structureBuffer:F5}, " +
                    $"emaSpreadAtr={emaSpreadAtr:F2}"
                );
                return false;
            }

            // Velocity SELL must escape recent lows.
            if (trendDown && !trueBreakLow)
            {
                LogFile.Write(
                    $"[Velocity] {symbol} blocked: SELL has not escaped structure | " +
                    $"bid={bid:F5}, recentLow={recentLow:F5}, need<{recentLow - structureBuffer:F5}, " +
                    $"emaSpreadAtr={emaSpreadAtr:F2}"
                );
                return false;
            }

            // Existing near-structure guard still applies, but does not block real breaks.
            if (trendUp && nearResistance && !breakingHigh)
            {
                LogFile.Write(
                    $"[Velocity] {symbol} blocked: buying into resistance | " +
                    $"ask={ask:F5}, recentHigh={recentHigh:F5}"
                );
                return false;
            }

            if (trendDown && nearSupport && !breakingLow)
            {
                LogFile.Write(
                    $"[Velocity] {symbol} blocked: selling into support | " +
                    $"bid={bid:F5}, recentLow={recentLow:F5}"
                );
                return false;
            }

            // ===============================
            // MOMENTUM
            // ===============================
            double prevMove = Math.Abs(c1.close - c1.open);

            double minBuyMomentum = Math.Max(prevMove * settings.MomentumFactor, atr * settings.MinLiveMoveAtr);
            double minSellMomentum = Math.Max(prevMove * settings.MomentumFactor, atr * settings.MinLiveMoveAtr);

            bool momentumBuy = moveBuy >= minBuyMomentum;
            bool momentumSell = moveSell >= minSellMomentum;

            if (trendUp && !momentumBuy)
            {
                LogFile.Write(
                    $"[Velocity] {symbol} blocked: weak buy momentum | " +
                    $"move={moveBuy:F5}, need>={minBuyMomentum:F5}"
                );
                return false;
            }

            if (trendDown && !momentumSell)
            {
                LogFile.Write(
                    $"[Velocity] {symbol} blocked: weak sell momentum | " +
                    $"move={moveSell:F5}, need>={minSellMomentum:F5}"
                );
                return false;
            }

            // ===============================
            // ENTRY CONDITIONS
            // ===============================
            bool buy =
                trendUp &&
                liveBuyMove &&
                momentumBuy &&
                trueBreakHigh &&
                distanceBuy <= maxDistance;

            bool sell =
                trendDown &&
                liveSellMove &&
                momentumSell &&
                trueBreakLow &&
                distanceSell <= maxDistance;

            if (buy)
            {
                action = TradeAction.EnterBuy;

                LogFile.Write(
                    $"[Velocity] 🚀 BUY {symbol} | " +
                    $"ask={ask:F5}, open={liveOpen:F5}, move={moveBuy:F5}, " +
                    $"ATR={atr:F5}, ATR%={normalizedAtrPercentile:F2}, " +
                    $"distEMA9={distanceBuy:F5}, recentHigh={recentHigh:F5}, " +
                    $"breakNeed={recentHigh + structureBuffer:F5}, emaSpreadAtr={emaSpreadAtr:F2}, " +
                    $"rangeAtr={(liveRange / atr):F2}, bodyRatio={liveBodyRatio:F2}"
                );

                _lastVelocityEntry[symbolKey] = DateTime.UtcNow;
                return true;
            }

            if (sell)
            {
                action = TradeAction.EnterSell;

                LogFile.Write(
                    $"[Velocity] 🚀 SELL {symbol} | " +
                    $"bid={bid:F5}, open={liveOpen:F5}, move={moveSell:F5}, " +
                    $"ATR={atr:F5}, ATR%={normalizedAtrPercentile:F2}, " +
                    $"distEMA9={distanceSell:F5}, recentLow={recentLow:F5}, " +
                    $"breakNeed={recentLow - structureBuffer:F5}, emaSpreadAtr={emaSpreadAtr:F2}, " +
                    $"rangeAtr={(liveRange / atr):F2}, bodyRatio={liveBodyRatio:F2}"
                );

                _lastVelocityEntry[symbolKey] = DateTime.UtcNow;
                return true;
            }

            return false;
        }

        // ===============================
        // SYMBOL PROFILE RESOLUTION
        // ===============================
        private static VelocitySettings GetSettings(string symbol)
        {
            string s = string.IsNullOrWhiteSpace(symbol)
                ? ""
                : symbol.ToUpperInvariant();

            // Metals
            if (s.Contains("XAU") ||
                s.Contains("XAG") ||
                s.Contains("GOLD") ||
                s.Contains("SILVER"))
            {
                return GoldProfile;
            }

            // Crypto
            if (s.Contains("BTC") ||
                s.Contains("ETH") ||
                s.Contains("SOL") ||
                s.Contains("BNB") ||
                s.Contains("XRP") ||
                s.Contains("ADA") ||
                s.Contains("DOGE") ||
                s.Contains("LTC") ||
                s.Contains("LINK"))
            {
                return CryptoProfile;
            }

            // Indices / CFDs
            if (s.Contains("US100") ||
                s.Contains("NAS") ||
                s.Contains("USTEC") ||
                s.Contains("US30") ||
                s.Contains("DJ") ||
                s.Contains("SPX") ||
                s.Contains("US500") ||
                s.Contains("GER") ||
                s.Contains("DAX") ||
                s.Contains("UK100"))
            {
                return IndexProfile;
            }

            // Default to FX
            return FxProfile;
        }
    }
}
