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

    public static class CascadeBreakdownEngine
    {
        // ===============================
        // SYMBOL SETTINGS
        // ===============================
        private class CascadeSettings
        {
            public double BreakRangeAtr { get; set; }
            public double FollowThroughFactor { get; set; }
            public double BodyRatio { get; set; }
            public double LateMoveAtr { get; set; }
            public double ResistanceBufferAtr { get; set; }
            public double MaxEmaDistanceAtr { get; set; }
            public double StrongBreakBufferAtr { get; set; }
            public double MomentumFactor { get; set; }
            public int MaxSameDirectionCandles { get; set; }

            // Anti-chop / structure escape
            public double MinEmaSpreadAtr { get; set; }
            public double StructureBreakBufferAtr { get; set; }
            public double MinLiveRangeAtr { get; set; }
            public double MinLiveBodyRatio { get; set; }
        }

        private static readonly CascadeSettings GoldProfile = new()
        {
            BreakRangeAtr = 0.75,
            FollowThroughFactor = 0.65,
            BodyRatio = 0.50,
            LateMoveAtr = 1.35,
            ResistanceBufferAtr = 0.35,
            MaxEmaDistanceAtr = 1.25,
            StrongBreakBufferAtr = 0.05,
            MomentumFactor = 0.55,
            MaxSameDirectionCandles = 5,

            MinEmaSpreadAtr = 0.10,
            StructureBreakBufferAtr = 0.08,
            MinLiveRangeAtr = 0.45,
            MinLiveBodyRatio = 0.42
        };

        private static readonly CascadeSettings CryptoProfile = new()
        {
            BreakRangeAtr = 0.85,
            FollowThroughFactor = 0.70,
            BodyRatio = 0.50,
            LateMoveAtr = 1.60,
            ResistanceBufferAtr = 0.45,
            MaxEmaDistanceAtr = 1.45,
            StrongBreakBufferAtr = 0.06,
            MomentumFactor = 0.55,
            MaxSameDirectionCandles = 5,

            MinEmaSpreadAtr = 0.12,
            StructureBreakBufferAtr = 0.10,
            MinLiveRangeAtr = 0.50,
            MinLiveBodyRatio = 0.40
        };

        private static readonly CascadeSettings IndexProfile = new()
        {
            BreakRangeAtr = 0.80,
            FollowThroughFactor = 0.70,
            BodyRatio = 0.52,
            LateMoveAtr = 1.35,
            ResistanceBufferAtr = 0.35,
            MaxEmaDistanceAtr = 1.15,
            StrongBreakBufferAtr = 0.05,
            MomentumFactor = 0.55,
            MaxSameDirectionCandles = 5,

            MinEmaSpreadAtr = 0.10,
            StructureBreakBufferAtr = 0.08,
            MinLiveRangeAtr = 0.45,
            MinLiveBodyRatio = 0.40
        };

        private static readonly CascadeSettings FxProfile = new()
        {
            BreakRangeAtr = 0.65,
            FollowThroughFactor = 0.60,
            BodyRatio = 0.48,
            LateMoveAtr = 1.05,
            ResistanceBufferAtr = 0.25,
            MaxEmaDistanceAtr = 0.95,
            StrongBreakBufferAtr = 0.04,
            MomentumFactor = 0.50,
            MaxSameDirectionCandles = 5,

            MinEmaSpreadAtr = 0.08,
            StructureBreakBufferAtr = 0.06,
            MinLiveRangeAtr = 0.35,
            MinLiveBodyRatio = 0.38
        };

        public static int StructureLookback = 6;

        public static bool ShouldEnter(
            string symbol,
            MtApi5Client client,
            MqlRates[] rates,
            double atr,
            double[] emaFast,
            double[] emaSlow,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            if (string.IsNullOrWhiteSpace(symbol) ||
                client == null ||
                rates == null || rates.Length < StructureLookback + 4 ||
                emaFast == null || emaSlow == null ||
                emaFast.Length < 3 || emaSlow.Length < 3 ||
                atr <= 0)
            {
                return false;
            }

            var settings = GetSettings(symbol);

            double bid = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);
            double ask = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);

            if (bid <= 0 || ask <= 0 || ask <= bid)
            {
                LogFile.Write($"[{symbol}] ❌ Cascade blocked: invalid bid/ask | bid={bid}, ask={ask}");
                return false;
            }

            var c0 = rates[^1]; // live/current candle
            var c1 = rates[^2]; // last closed candle

            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];

            // ===============================
            // TREND — POSITION + SLOPE AGREEMENT
            // Prevent Cascade from selling during bullish transition
            // or buying during bearish transition.
            // ===============================
            
            double ema9Prev = emaFast[^2];
            double ema21Prev = emaSlow[^2];

            double fastSlope = ema9 - ema9Prev;
            double slowSlope = ema21 - ema21Prev;

            double fastSlopeAtr = fastSlope / atr;
            double slowSlopeAtr = slowSlope / atr;

            bool trendUp =
                ema9 > ema21 &&
                fastSlope > 0;

            bool trendDown =
                ema9 < ema21 &&
                fastSlope < 0;

            // Transition / crossover danger zone
            bool bullishTransition =
                ema9 < ema21 &&
                fastSlope > atr * 0.03;

            bool bearishTransition =
                ema9 > ema21 &&
                fastSlope < -atr * 0.03;

            if (bullishTransition)
            {
                LogFile.Write(
                    $"[{symbol}] ❌ Cascade SELL blocked: bullish transition | " +
                    $"ema9={ema9:F5}, ema21={ema21:F5}, fastSlope={fastSlope:F5}, slopeAtr={fastSlopeAtr:F2}"
                );
                return false;
            }

            if (bearishTransition)
            {
                LogFile.Write(
                    $"[{symbol}] ❌ Cascade BUY blocked: bearish transition | " +
                    $"ema9={ema9:F5}, ema21={ema21:F5}, fastSlope={fastSlope:F5}, slopeAtr={fastSlopeAtr:F2}"
                );
                return false;
            }

            if (!trendUp && !trendDown)
            {
                LogFile.Write(
                    $"[{symbol}] ❌ Cascade blocked: EMA trend not aligned | " +
                    $"ema9={ema9:F5}, ema21={ema21:F5}, fastSlope={fastSlope:F5}, slowSlope={slowSlope:F5}"
                );
                return false;
            }

            // ===============================
            // EMA COMPRESSION / ANTI-CHOP CHECK
            // ===============================
            double emaSpread = Math.Abs(ema9 - ema21);
            double emaSpreadAtr = emaSpread / atr;

            bool emaCompressed = emaSpreadAtr < settings.MinEmaSpreadAtr;

            // ===============================
            // STRUCTURE — EXCLUDE CURRENT LIVE CANDLE
            // ===============================
            var structureCandles = rates
                .Take(rates.Length - 1)
                .Skip(Math.Max(0, rates.Length - 1 - StructureLookback))
                .ToArray();

            if (structureCandles.Length < StructureLookback)
            {
                LogFile.Write($"[{symbol}] ❌ Cascade blocked: insufficient structure candles");
                return false;
            }

            double recentHigh = structureCandles.Max(r => r.high);
            double recentLow = structureCandles.Min(r => r.low);

            double structureBuffer = atr * settings.StructureBreakBufferAtr;

            bool breakUp = ask > recentHigh;
            bool breakDown = bid < recentLow;

            bool trueBreakUp = ask > recentHigh + structureBuffer;
            bool trueBreakDown = bid < recentLow - structureBuffer;

            bool strongBreakUp = ask > recentHigh + atr * settings.StrongBreakBufferAtr;
            bool strongBreakDown = bid < recentLow - atr * settings.StrongBreakBufferAtr;

            bool insideStructure =
                ask <= recentHigh + structureBuffer &&
                bid >= recentLow - structureBuffer;

            // ===============================
            // ANTI-CHOP / STRUCTURE ESCAPE GUARD
            // Cascade must not fire inside EMA chop.
            // ===============================
            if (emaCompressed && insideStructure)
            {
                LogFile.Write(
                    $"[{symbol}] ❌ Cascade blocked: EMA compression / chop | " +
                    $"emaSpreadAtr={emaSpreadAtr:F2}, need>={settings.MinEmaSpreadAtr:F2}, " +
                    $"recentHigh={recentHigh:F5}, recentLow={recentLow:F5}, " +
                    $"ask={ask:F5}, bid={bid:F5}"
                );

                return false;
            }

            if (trendUp && !trueBreakUp)
            {
                LogFile.Write(
                    $"[{symbol}] ❌ Cascade BUY blocked: no true structure escape | " +
                    $"ask={ask:F5}, recentHigh={recentHigh:F5}, need>{recentHigh + structureBuffer:F5}, " +
                    $"emaSpreadAtr={emaSpreadAtr:F2}"
                );

                return false;
            }

            if (trendDown && !trueBreakDown)
            {
                LogFile.Write(
                    $"[{symbol}] ❌ Cascade SELL blocked: no true structure escape | " +
                    $"bid={bid:F5}, recentLow={recentLow:F5}, need<{recentLow - structureBuffer:F5}, " +
                    $"emaSpreadAtr={emaSpreadAtr:F2}"
                );

                return false;
            }

            // Direction must agree with trend.
            if (trendUp && !breakUp)
            {
                LogFile.Write($"[{symbol}] ❌ Cascade BUY blocked: no upside structure break");
                return false;
            }

            if (trendDown && !breakDown)
            {
                LogFile.Write($"[{symbol}] ❌ Cascade SELL blocked: no downside structure break");
                return false;
            }

            if (trendUp && !strongBreakUp)
            {
                LogFile.Write($"[{symbol}] ❌ Cascade BUY blocked: weak breakout");
                return false;
            }

            if (trendDown && !strongBreakDown)
            {
                LogFile.Write($"[{symbol}] ❌ Cascade SELL blocked: weak breakdown");
                return false;
            }

            // ===============================
            // LIVE CANDLE BREAKOUT QUALITY
            // ===============================
            double range0 = c0.high - c0.low;
            double body0 = Math.Abs(c0.close - c0.open);
            double ratio0 = body0 / Math.Max(range0, 0.0000001);

            if (range0 <= 0)
            {
                LogFile.Write($"[{symbol}] ❌ Cascade blocked: invalid live candle range");
                return false;
            }

            bool liveBreakoutQuality =
                range0 >= atr * settings.BreakRangeAtr &&
                ratio0 >= settings.BodyRatio;

            bool liveRangeQuality =
                range0 >= atr * settings.MinLiveRangeAtr;

            bool liveBodyQuality =
                ratio0 >= settings.MinLiveBodyRatio;

            if (!liveBreakoutQuality || !liveRangeQuality || !liveBodyQuality)
            {
                LogFile.Write(
                    $"[{symbol}] ❌ Cascade blocked: live candle not strong enough | " +
                    $"rangeAtr={(range0 / atr):F2}, need>={settings.BreakRangeAtr:F2}, " +
                    $"bodyRatio={ratio0:F2}, need>={settings.BodyRatio:F2}, " +
                    $"minRangeAtr={settings.MinLiveRangeAtr:F2}, minBody={settings.MinLiveBodyRatio:F2}"
                );
                return false;
            }

            // ===============================
            // FOLLOW-THROUGH
            // Allows either:
            // 1. Previous candle already showed expansion
            // 2. Current live candle is showing strong continuation
            // ===============================
            double range1 = c1.high - c1.low;
            double body1 = Math.Abs(c1.close - c1.open);

            bool previousFollowThrough =
                range1 >= atr * settings.FollowThroughFactor;

            bool liveFollowThrough =
                range0 >= atr * settings.FollowThroughFactor &&
                body0 >= body1 * 0.60;

            if (!previousFollowThrough && !liveFollowThrough)
            {
                LogFile.Write(
                    $"[{symbol}] ❌ Cascade blocked: no follow-through | " +
                    $"prevRange={range1:F5}, liveRange={range0:F5}, need>={atr * settings.FollowThroughFactor:F5}"
                );
                return false;
            }

            // ===============================
            // MOMENTUM
            // ===============================
            double prevMove = Math.Abs(c1.close - c1.open);
            double moveSell = Math.Abs(bid - c0.open);
            double moveBuy = Math.Abs(ask - c0.open);

            double minSellMomentum = Math.Max(prevMove * settings.MomentumFactor, atr * 0.20);
            double minBuyMomentum = Math.Max(prevMove * settings.MomentumFactor, atr * 0.20);

            bool momentumSell = moveSell >= minSellMomentum;
            bool momentumBuy = moveBuy >= minBuyMomentum;

            if (trendDown && !momentumSell)
            {
                LogFile.Write(
                    $"[{symbol}] ❌ Cascade SELL blocked: weak live momentum | " +
                    $"move={moveSell:F5}, need>={minSellMomentum:F5}"
                );
                return false;
            }

            if (trendUp && !momentumBuy)
            {
                LogFile.Write(
                    $"[{symbol}] ❌ Cascade BUY blocked: weak live momentum | " +
                    $"move={moveBuy:F5}, need>={minBuyMomentum:F5}"
                );
                return false;
            }

            // ===============================
            // SOFT EXHAUSTION FILTER
            // Blocks only if move is old AND stretched.
            // ===============================
            int bullCount = 0;
            int bearCount = 0;

            for (int i = rates.Length - 1; i >= 0; i--)
            {
                var c = rates[i];

                if (c.close > c.open)
                {
                    if (bearCount > 0) break;
                    bullCount++;
                }
                else if (c.close < c.open)
                {
                    if (bullCount > 0) break;
                    bearCount++;
                }
                else
                {
                    break;
                }
            }

            double distanceSell = Math.Abs(bid - ema9);
            double distanceBuy = Math.Abs(ask - ema9);

            bool stretchedSell = distanceSell > atr * settings.LateMoveAtr;
            bool stretchedBuy = distanceBuy > atr * settings.LateMoveAtr;

            if (trendUp &&
                bullCount >= settings.MaxSameDirectionCandles &&
                stretchedBuy)
            {
                LogFile.Write(
                    $"[{symbol}] ❌ Cascade BUY blocked: late bullish exhaustion | " +
                    $"bullCount={bullCount}, distEMA9={distanceBuy:F5}, max={atr * settings.LateMoveAtr:F5}"
                );
                return false;
            }

            if (trendDown &&
                bearCount >= settings.MaxSameDirectionCandles &&
                stretchedSell)
            {
                LogFile.Write(
                    $"[{symbol}] ❌ Cascade SELL blocked: late bearish exhaustion | " +
                    $"bearCount={bearCount}, distEMA9={distanceSell:F5}, max={atr * settings.LateMoveAtr:F5}"
                );
                return false;
            }

            // ===============================
            // EMA EXTENSION FILTER
            // ===============================
            double maxDistance = atr * settings.MaxEmaDistanceAtr;

            if (trendDown && distanceSell > maxDistance)
            {
                LogFile.Write(
                    $"[{symbol}] ❌ Cascade SELL blocked: too extended from EMA9 | " +
                    $"dist={distanceSell:F5}, max={maxDistance:F5}"
                );
                return false;
            }

            if (trendUp && distanceBuy > maxDistance)
            {
                LogFile.Write(
                    $"[{symbol}] ❌ Cascade BUY blocked: too extended from EMA9 | " +
                    $"dist={distanceBuy:F5}, max={maxDistance:F5}"
                );
                return false;
            }

            // ===============================
            // SMART SUPPORT / RESISTANCE
            // Only block if price is near structure WITHOUT a confirmed break.
            // ===============================
            double buffer = atr * settings.ResistanceBufferAtr;

            bool nearResistance = ask >= recentHigh - buffer && ask <= recentHigh + buffer;
            bool nearSupport = bid >= recentLow - buffer && bid <= recentLow + buffer;

            if (trendUp && nearResistance && !trueBreakUp)
            {
                LogFile.Write($"[{symbol}] ❌ Cascade BUY blocked: buying into resistance / no clean escape");
                return false;
            }

            if (trendDown && nearSupport && !trueBreakDown)
            {
                LogFile.Write($"[{symbol}] ❌ Cascade SELL blocked: selling into support / no clean escape");
                return false;
            }

            // ===============================
            // CLIMAX / LATE EXPANSION FILTER
            // Prevent Cascade from buying/selling
            // after emotional vertical acceleration.
            // ===============================

            double liveRangeAtr = range0 / atr;

            double accelerationFactor =
                body1 > 0
                    ? body0 / body1
                    : 1.0;

            // Detect extremely emotional live candle
            bool emotionalBullishExpansion =
                trendUp &&
                liveRangeAtr >= 2.2 &&
                ratio0 >= 0.75 &&
                distanceBuy > atr * (settings.LateMoveAtr * 0.90);

            bool emotionalBearishExpansion =
                trendDown &&
                liveRangeAtr >= 2.2 &&
                ratio0 >= 0.75 &&
                distanceSell > atr * (settings.LateMoveAtr * 0.90);

            // Detect acceleration climax
            bool bullishAccelerationClimax =
                trendUp &&
                bullCount >= 3 &&
                accelerationFactor >= 1.8 &&
                distanceBuy > atr * (settings.LateMoveAtr * 0.80);

            bool bearishAccelerationClimax =
                trendDown &&
                bearCount >= 3 &&
                accelerationFactor >= 1.8 &&
                distanceSell > atr * (settings.LateMoveAtr * 0.80);

            // Block late emotional continuation
            if (emotionalBullishExpansion || bullishAccelerationClimax)
            {
                LogFile.Write(
                    $"[{symbol}] ❌ Cascade BUY blocked: climax expansion / emotional acceleration | " +
                    $"rangeAtr={liveRangeAtr:F2}, " +
                    $"bodyRatio={ratio0:F2}, " +
                    $"bullCount={bullCount}, " +
                    $"accel={accelerationFactor:F2}, " +
                    $"distEMA9={distanceBuy:F5}"
                );

                return false;
            }

            if (emotionalBearishExpansion || bearishAccelerationClimax)
            {
                LogFile.Write(
                    $"[{symbol}] ❌ Cascade SELL blocked: climax expansion / emotional acceleration | " +
                    $"rangeAtr={liveRangeAtr:F2}, " +
                    $"bodyRatio={ratio0:F2}, " +
                    $"bearCount={bearCount}, " +
                    $"accel={accelerationFactor:F2}, " +
                    $"distEMA9={distanceSell:F5}"
                );

                return false;
            }
            // ===============================
            // FINAL CASCADE CONDITIONS
            // ===============================
            bool bearishCascade =
                trendDown &&
                trueBreakDown &&
                strongBreakDown &&
                liveBreakoutQuality &&
                liveRangeQuality &&
                liveBodyQuality &&
                (previousFollowThrough || liveFollowThrough) &&
                momentumSell;

            bool bullishCascade =
                trendUp &&
                trueBreakUp &&
                strongBreakUp &&
                liveBreakoutQuality &&
                liveRangeQuality &&
                liveBodyQuality &&
                (previousFollowThrough || liveFollowThrough) &&
                momentumBuy;

            if (bearishCascade)
            {
                LogFile.Write(
                    $"[{symbol}] ⚡ Cascade SELL | " +
                    $"bid={bid:F5}, recentLow={recentLow:F5}, " +
                    $"breakNeed={recentLow - structureBuffer:F5}, " +
                    $"rangeAtr={(range0 / atr):F2}, bodyRatio={ratio0:F2}, " +
                    $"distEMA9={distanceSell:F5}, emaSpreadAtr={emaSpreadAtr:F2}"
                );

                action = TradeAction.EnterSell;
                return true;
            }

            if (bullishCascade)
            {
                LogFile.Write(
                    $"[{symbol}] ⚡ Cascade BUY | " +
                    $"ask={ask:F5}, recentHigh={recentHigh:F5}, " +
                    $"breakNeed={recentHigh + structureBuffer:F5}, " +
                    $"rangeAtr={(range0 / atr):F2}, bodyRatio={ratio0:F2}, " +
                    $"distEMA9={distanceBuy:F5}, emaSpreadAtr={emaSpreadAtr:F2}"
                );

                action = TradeAction.EnterBuy;
                return true;
            }

            return false;
        }

        // ===============================
        // SYMBOL PROFILE RESOLUTION
        // ===============================
        private static CascadeSettings GetSettings(string symbol)
        {
            string s = string.IsNullOrWhiteSpace(symbol)
                ? ""
                : symbol.ToUpperInvariant();

            // Metals
            if (s.Contains("XAU") ||
                s.Contains("XAG") ||
                s.Contains("GOLD") ||
                s.Contains("SILVER"))
                return GoldProfile;

            // Crypto
            if (s.Contains("BTC") ||
                s.Contains("ETH") ||
                s.Contains("SOL") ||
                s.Contains("BNB") ||
                s.Contains("XRP") ||
                s.Contains("ADA") ||
                s.Contains("DOGE") ||
                s.Contains("LTC") ||
                s.Contains("LINK") ||
                s.Contains("AVAX"))
                return CryptoProfile;

            // Indices / CFDs
            if (s.Contains("US100") ||
                s.Contains("US100.CASH") ||
                s.Contains("NAS") ||
                s.Contains("NAS100") ||
                s.Contains("USTEC") ||
                s.Contains("US30") ||
                s.Contains("DJ") ||
                s.Contains("SPX") ||
                s.Contains("US500") ||
                s.Contains("GER") ||
                s.Contains("DAX") ||
                s.Contains("UK100"))
                return IndexProfile;

            // FX default:
            // EURUSD, GBPUSD, USDJPY, EURJPY, USDCAD, AUDUSD, etc.
            return FxProfile;
        }
    }
}
