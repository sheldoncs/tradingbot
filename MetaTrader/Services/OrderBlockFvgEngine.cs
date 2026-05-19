using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;

namespace MetaTrader.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public static class OrderBlockFvgEngine
    {
        private class Profile
        {
            public double MinImpulseAtr;
            public double MinFvgAtr;
            public double ZoneToleranceAtr;
            public double MaxDistanceFromEma9Atr;
            public double MinRejectionBodyRatio;
            public int ScanLookback;
        }

        private class ObFvgZone
        {
            public bool IsBullish;
            public double ZoneLow;
            public double ZoneHigh;
            public double FvgLow;
            public double FvgHigh;
            public DateTime Time;
        }

        private class EngineStats
        {
            public int Evaluations;
            public int Fires;
            public int Blocks;
            public DateTime FirstSeenUtc = DateTime.UtcNow;
            public DateTime LastSeenUtc = DateTime.UtcNow;
        }

        private static readonly Dictionary<string, EngineStats> _stats = new();

        private static Profile GetProfile(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                symbol = "";

            symbol = symbol.ToUpperInvariant();

            // =============================
            // GOLD / METALS
            // =============================
            if (symbol.Contains("XAU") ||
                symbol.Contains("XAG") ||
                symbol.Contains("GOLD") ||
                symbol.Contains("SILVER"))
            {
                return new Profile
                {
                    MinImpulseAtr = 1.00,
                    MinFvgAtr = 0.25,
                    ZoneToleranceAtr = 0.25,
                    MaxDistanceFromEma9Atr = 1.45,
                    MinRejectionBodyRatio = 0.35,
                    ScanLookback = 8
                };
            }

            // =============================
            // CRYPTO
            // =============================
            if (symbol.Contains("BTC") ||
                symbol.Contains("ETH") ||
                symbol.Contains("SOL") ||
                symbol.Contains("AVAX") ||
                symbol.Contains("BNB") ||
                symbol.Contains("XRP") ||
                symbol.Contains("ADA") ||
                symbol.Contains("DOGE") ||
                symbol.Contains("LTC") ||
                symbol.Contains("LINK"))
            {
                return new Profile
                {
                    MinImpulseAtr = 1.15,
                    MinFvgAtr = 0.30,
                    ZoneToleranceAtr = 0.30,
                    MaxDistanceFromEma9Atr = 1.70,
                    MinRejectionBodyRatio = 0.32,
                    ScanLookback = 10
                };
            }

            // =============================
            // INDICES / CFDs
            // =============================
            if (symbol.Contains("US100") ||
                symbol.Contains("NAS") ||
                symbol.Contains("USTEC") ||
                symbol.Contains("US30") ||
                symbol.Contains("DJ") ||
                symbol.Contains("SPX") ||
                symbol.Contains("US500") ||
                symbol.Contains("DAX") ||
                symbol.Contains("GER") ||
                symbol.Contains("UK100"))
            {
                return new Profile
                {
                    MinImpulseAtr = 1.10,
                    MinFvgAtr = 0.28,
                    ZoneToleranceAtr = 0.28,
                    MaxDistanceFromEma9Atr = 1.55,
                    MinRejectionBodyRatio = 0.33,
                    ScanLookback = 8
                };
            }

            // =============================
            // FX DEFAULT
            // =============================
            return new Profile
            {
                MinImpulseAtr = 0.85,
                MinFvgAtr = 0.20,
                ZoneToleranceAtr = 0.18,
                MaxDistanceFromEma9Atr = 1.10,
                MinRejectionBodyRatio = 0.32,
                ScanLookback = 8
            };
        }

        public static bool ShouldEnter(
            string symbol,
            MqlRates[] rates,
            double atr,
            double[] emaFast,
            double[] emaSlow,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            TrackEvaluation(symbol);

            if (string.IsNullOrWhiteSpace(symbol) ||
                rates == null ||
                rates.Length < 12 ||
                emaFast == null ||
                emaSlow == null ||
                emaFast.Length < 3 ||
                emaSlow.Length < 3 ||
                atr <= 0)
            {
                TrackBlock(symbol);
                return false;
            }

            var profile = GetProfile(symbol);

            var current = rates[^1];

            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];

            bool trendUp = ema9 > ema21;
            bool trendDown = ema9 < ema21;

            if (!trendUp && !trendDown)
            {
                LogFile.Write($"[{symbol}] OB/FVG blocked: no EMA trend | {GetFrequencySummary(symbol)}");
                TrackBlock(symbol);
                return false;
            }

            // =============================
            // FIND RECENT OB/FVG ZONE
            // =============================
            var zone = FindRecentZone(
                symbol,
                rates,
                atr,
                profile,
                trendUp,
                trendDown
            );

            if (zone == null)
            {
                LogFile.Write($"[{symbol}] OB/FVG blocked: no recent valid OB/FVG zone | {GetFrequencySummary(symbol)}");
                TrackBlock(symbol);
                return false;
            }

            // =============================
            // EXTENSION GUARD
            // Do not chase too far from EMA9.
            // =============================
            double distanceFromEma9 = Math.Abs(current.close - ema9);

            if (distanceFromEma9 > atr * profile.MaxDistanceFromEma9Atr)
            {
                LogFile.Write(
                    $"[{symbol}] OB/FVG blocked: too far from EMA9 | " +
                    $"dist={distanceFromEma9:F5}, max={atr * profile.MaxDistanceFromEma9Atr:F5} | " +
                    GetFrequencySummary(symbol)
                );

                TrackBlock(symbol);
                return false;
            }

            // =============================
            // RETURN INTO ZONE
            // =============================
            double zoneTol = atr * profile.ZoneToleranceAtr;

            bool returnedToBullZone =
                zone.IsBullish &&
                current.low <= zone.ZoneHigh + zoneTol &&
                current.high >= zone.ZoneLow - zoneTol;

            bool returnedToBearZone =
                !zone.IsBullish &&
                current.high >= zone.ZoneLow - zoneTol &&
                current.low <= zone.ZoneHigh + zoneTol;

            if (!returnedToBullZone && !returnedToBearZone)
            {
                LogFile.Write(
                    $"[{symbol}] OB/FVG blocked: price has not returned to zone | " +
                    $"zoneLow={zone.ZoneLow:F5}, zoneHigh={zone.ZoneHigh:F5}, " +
                    $"currentLow={current.low:F5}, currentHigh={current.high:F5} | " +
                    GetFrequencySummary(symbol)
                );

                TrackBlock(symbol);
                return false;
            }

            // =============================
            // REJECTION / CONTINUATION CONFIRMATION
            // =============================
            bool bullishRejection = IsBullishRejection(current, zone.ZoneLow, zone.ZoneHigh, atr, profile);
            bool bearishRejection = IsBearishRejection(current, zone.ZoneLow, zone.ZoneHigh, atr, profile);

            bool buy =
                trendUp &&
                zone.IsBullish &&
                returnedToBullZone &&
                bullishRejection;

            bool sell =
                trendDown &&
                !zone.IsBullish &&
                returnedToBearZone &&
                bearishRejection;

            if (buy)
            {
                action = TradeAction.EnterBuy;
                TrackFire(symbol);

                LogFile.Write(
                    $"[{symbol}] ✅ OB/FVG BUY | " +
                    $"zoneLow={zone.ZoneLow:F5}, zoneHigh={zone.ZoneHigh:F5}, " +
                    $"fvgLow={zone.FvgLow:F5}, fvgHigh={zone.FvgHigh:F5}, " +
                    $"distEMA9={distanceFromEma9:F5} | " +
                    GetFrequencySummary(symbol)
                );

                return true;
            }

            if (sell)
            {
                action = TradeAction.EnterSell;
                TrackFire(symbol);

                LogFile.Write(
                    $"[{symbol}] ✅ OB/FVG SELL | " +
                    $"zoneLow={zone.ZoneLow:F5}, zoneHigh={zone.ZoneHigh:F5}, " +
                    $"fvgLow={zone.FvgLow:F5}, fvgHigh={zone.FvgHigh:F5}, " +
                    $"distEMA9={distanceFromEma9:F5} | " +
                    GetFrequencySummary(symbol)
                );

                return true;
            }

            LogFile.Write(
                $"[{symbol}] OB/FVG blocked: zone touched but no rejection | " +
                $"bullReject={bullishRejection}, bearReject={bearishRejection}, " +
                $"zoneLow={zone.ZoneLow:F5}, zoneHigh={zone.ZoneHigh:F5} | " +
                GetFrequencySummary(symbol)
            );

            TrackBlock(symbol);
            return false;
        }

        // =============================
        // FIND RECENT OB/FVG ZONE
        // =============================
        private static ObFvgZone? FindRecentZone(
            string symbol,
            MqlRates[] rates,
            double atr,
            Profile profile,
            bool trendUp,
            bool trendDown)
        {
            int maxLookback = Math.Min(profile.ScanLookback, rates.Length - 4);

            // We scan from newest valid pattern backward.
            // Pattern:
            // c2 = order-block candle
            // c1 = impulse candle
            // c0 = displacement/FVG confirmation candle
            //
            // Later, the current candle can return into the zone.
            for (int offset = 2; offset <= maxLookback; offset++)
            {
                var c0 = rates[^offset];
                var c1 = rates[^(offset + 1)];
                var c2 = rates[^(offset + 2)];

                double impulseRange = c1.high - c1.low;
                double impulseBody = Math.Abs(c1.close - c1.open);

                if (impulseRange <= 0)
                    continue;

                double impulseBodyRatio = impulseBody / impulseRange;

                bool strongImpulse =
                    impulseRange >= atr * profile.MinImpulseAtr &&
                    impulseBodyRatio >= 0.45;

                if (!strongImpulse)
                    continue;

                // Bullish OB/FVG:
                // OB candle is bearish, impulse/displacement moves up,
                // and gap exists between c2 high and c0 low.
                bool bullishOB = c2.close < c2.open;
                bool bullishImpulse = c1.close > c1.open;
                double bullishGap = c0.low - c2.high;

                bool bullishFvg =
                    bullishGap >= atr * profile.MinFvgAtr;

                if (trendUp && bullishOB && bullishImpulse && bullishFvg)
                {
                    double zoneLow = Math.Min(c2.low, c2.high);
                    double zoneHigh = Math.Max(c2.high, c0.low);

                    return new ObFvgZone
                    {
                        IsBullish = true,
                        ZoneLow = zoneLow,
                        ZoneHigh = zoneHigh,
                        FvgLow = c2.high,
                        FvgHigh = c0.low,
                        Time = c1.time
                    };
                }

                // Bearish OB/FVG:
                // OB candle is bullish, impulse/displacement moves down,
                // and gap exists between c0 high and c2 low.
                bool bearishOB = c2.close > c2.open;
                bool bearishImpulse = c1.close < c1.open;
                double bearishGap = c2.low - c0.high;

                bool bearishFvg =
                    bearishGap >= atr * profile.MinFvgAtr;

                if (trendDown && bearishOB && bearishImpulse && bearishFvg)
                {
                    double zoneLow = Math.Min(c0.high, c2.low);
                    double zoneHigh = Math.Max(c2.high, c2.low);

                    return new ObFvgZone
                    {
                        IsBullish = false,
                        ZoneLow = zoneLow,
                        ZoneHigh = zoneHigh,
                        FvgLow = c0.high,
                        FvgHigh = c2.low,
                        Time = c1.time
                    };
                }
            }

            return null;
        }

        // =============================
        // REJECTION HELPERS
        // =============================
        private static bool IsBullishRejection(
            MqlRates c,
            double zoneLow,
            double zoneHigh,
            double atr,
            Profile profile)
        {
            double range = c.high - c.low;
            if (range <= 0) return false;

            double body = Math.Abs(c.close - c.open);
            double bodyRatio = body / range;

            bool touchedZone =
                c.low <= zoneHigh + atr * profile.ZoneToleranceAtr &&
                c.high >= zoneLow - atr * profile.ZoneToleranceAtr;

            bool closedAboveZoneMid =
                c.close >= (zoneLow + zoneHigh) / 2.0;

            bool bullishClose =
                c.close > c.open;

            bool decentBody =
                bodyRatio >= profile.MinRejectionBodyRatio;

            bool lowerWickReject =
                c.low <= zoneHigh &&
                c.close > zoneHigh - atr * 0.10;

            return touchedZone &&
                   closedAboveZoneMid &&
                   (bullishClose || lowerWickReject) &&
                   decentBody;
        }

        private static bool IsBearishRejection(
            MqlRates c,
            double zoneLow,
            double zoneHigh,
            double atr,
            Profile profile)
        {
            double range = c.high - c.low;
            if (range <= 0) return false;

            double body = Math.Abs(c.close - c.open);
            double bodyRatio = body / range;

            bool touchedZone =
                c.high >= zoneLow - atr * profile.ZoneToleranceAtr &&
                c.low <= zoneHigh + atr * profile.ZoneToleranceAtr;

            bool closedBelowZoneMid =
                c.close <= (zoneLow + zoneHigh) / 2.0;

            bool bearishClose =
                c.close < c.open;

            bool decentBody =
                bodyRatio >= profile.MinRejectionBodyRatio;

            bool upperWickReject =
                c.high >= zoneLow &&
                c.close < zoneLow + atr * 0.10;

            return touchedZone &&
                   closedBelowZoneMid &&
                   (bearishClose || upperWickReject) &&
                   decentBody;
        }

        // =============================
        // FREQUENCY TRACKING
        // =============================
        private static void TrackEvaluation(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            if (!_stats.ContainsKey(symbol))
                _stats[symbol] = new EngineStats();

            _stats[symbol].Evaluations++;
            _stats[symbol].LastSeenUtc = DateTime.UtcNow;
        }

        private static void TrackFire(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            if (!_stats.ContainsKey(symbol))
                _stats[symbol] = new EngineStats();

            _stats[symbol].Fires++;
            _stats[symbol].LastSeenUtc = DateTime.UtcNow;
        }

        private static void TrackBlock(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            if (!_stats.ContainsKey(symbol))
                _stats[symbol] = new EngineStats();

            _stats[symbol].Blocks++;
            _stats[symbol].LastSeenUtc = DateTime.UtcNow;
        }

        public static string GetFrequencySummary(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            if (!_stats.TryGetValue(symbol, out var stat) || stat.Evaluations <= 0)
                return "OB/FVG Frequency: no evaluations yet";

            double fireRate = (double)stat.Fires / stat.Evaluations * 100.0;
            double blockRate = (double)stat.Blocks / stat.Evaluations * 100.0;

            double hoursRunning = Math.Max(
                0.01,
                (stat.LastSeenUtc - stat.FirstSeenUtc).TotalHours
            );

            double firesPerHour = stat.Fires / hoursRunning;

            return
                $"OB/FVG Frequency: eval={stat.Evaluations}, fires={stat.Fires}, blocks={stat.Blocks}, " +
                $"fireRate={fireRate:F2}%, blockRate={blockRate:F2}%, fires/hr={firesPerHour:F3}";
        }

        private static string NormalizeSymbol(string symbol)
        {
            return string.IsNullOrWhiteSpace(symbol)
                ? "UNKNOWN"
                : symbol.ToUpperInvariant();
        }
    }
}
