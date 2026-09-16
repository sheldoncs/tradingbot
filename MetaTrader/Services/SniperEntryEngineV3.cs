using MetaTrader.Data.Database;
using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.Services.Contract;
using MetaTrader.Util;
using MetaTrader.Util.Trading.Execution;
using MtApi5;
using System;
using static MetaTrader.Services.TradeServiceEngine;

namespace MetaTrader.Services
{
    using System;
    using System.Linq;

    public static class SniperEntryEngineV3
    {
        // =========================
        // GENERAL SETTINGS
        // =========================
        public static double MinAtrPercentile = 0.40;
        public static int StructureLookback = 4;

        public static double ImpulseRangeAtr = 0.75;
        public static double ImpulseBodyRatio = 0.48;

        public static double PullbackMaxAtr = 1.05;
        public static double PullbackWickRatioMax = 0.88;
        public static double PullbackNearEmaAtr = 0.80;

        public static double ContinuationRangeAtr = 0.45;
        public static double ContinuationBodyRatio = 0.42;

        public static double EmaDistanceAtrMin = 0.06;
        public static double MaxSpreadAtrFactor = 0.30;

        private const int DefaultMinScore = 5;

        private class SniperSettings
        {
            public double MaxDistanceAtr { get; set; }
            public double LateImpulseAtr { get; set; }
            public double LateImpulseDistanceFactor { get; set; }
            public int MinScore { get; set; }
            public double MinAtrPercentile { get; set; }
        }

        public static TradeAction Decide(
            string symbol,
            MtApi5Client client,
            MqlRates[] ratesH1,
            double atr,
            double atrPercentile,
            double[] emaFastH1,
            double[] emaSlowH1,
            SymbolContext ctx,
            M15ConfirmationService M15Service,
            ITradingPairProperties _tradingPairProperties,
            out SniperEvent ev)
        {
            ev = new SniperEvent
            {
                Regime = "Hold",
                ActionDescription = "No setup",
                SniperAction = TradeAction.Hold.ToString()
            };

            int score = 0;

            try
            {
                if (string.IsNullOrWhiteSpace(symbol) ||
                    client == null ||
                    ratesH1 == null || ratesH1.Length < 10 ||
                    emaFastH1 == null || emaFastH1.Length < 3 ||
                    emaSlowH1 == null || emaSlowH1.Length < 3 ||
                    ctx == null ||
                    atr <= 0)
                {
                    return TradeAction.Hold;
                }

                var settings = GetSettings(symbol);

                double normalizedAtrPercentile = atrPercentile > 1.0
                    ? atrPercentile / 100.0
                    : atrPercentile;

                // =========================
                // MARKET STATE GATE
                // =========================
                if (ctx.State != MarketState.Armed)
                {
                    LogFile.Write($"[SNIPER] {symbol} blocked: market state is not Armed. State={ctx.State}");
                    return TradeAction.Hold;
                }

                // =========================
                // ATR REGIME
                // =========================
                double requiredAtrPercentile = Math.Max(MinAtrPercentile, settings.MinAtrPercentile);

                if (normalizedAtrPercentile < requiredAtrPercentile)
                {
                    LogFile.Write(
                        $"[SNIPER] {symbol} blocked: ATR percentile too low | " +
                        $"ATR%={normalizedAtrPercentile:F2}, need>={requiredAtrPercentile:F2}"
                    );
                    return TradeAction.Hold;
                }

                // =========================
                // SPREAD
                // =========================
                if (ExecutionGuards.IsSpreadTooWide(client, symbol, atr, MaxSpreadAtrFactor))
                {
                    LogFile.Write($"[SNIPER] {symbol} blocked: spread too wide");
                    return TradeAction.Hold;
                }

                var c0 = ratesH1[^1]; // current / latest candle
                var c1 = ratesH1[^2]; // pullback / previous candle
                var c2 = ratesH1[^3]; // impulse candle candidate

                if (VolatilityGuard.IsShockBlocking(symbol, c0.time, atr))
                {
                    LogFile.Write($"[SNIPER] {symbol} blocked: shock guard active");
                    return TradeAction.Hold;
                }

                // =========================
                // TREND
                // =========================
                double ema9 = emaFastH1[^1];
                double ema21 = emaSlowH1[^1];

                bool bullish = ema9 > ema21;
                bool bearish = ema9 < ema21;

                if (!bullish && !bearish)
                {
                    LogFile.Write($"[SNIPER] {symbol} blocked: no EMA trend");
                    return TradeAction.Hold;
                }

                double emaSlope = emaFastH1[^1] - emaFastH1[^2];

                bool slopeOk = bullish
                    ? emaSlope > 0
                    : emaSlope < 0;

                if (!slopeOk)
                {
                    LogFile.Write(
                        $"[SNIPER] {symbol} blocked: EMA slope not aligned | " +
                        $"slope={emaSlope:F5}"
                    );
                    return TradeAction.Hold;
                }

                double emaDist = Math.Abs(ema9 - ema21);

                if (emaDist < atr * EmaDistanceAtrMin)
                {
                    LogFile.Write(
                        $"[SNIPER] {symbol} blocked: EMA compression | " +
                        $"dist={emaDist:F5}, need>={atr * EmaDistanceAtrMin:F5}"
                    );
                    return TradeAction.Hold;
                }

                // =========================
                // EMA EXTENSION — SYMBOL ADAPTIVE
                // =========================
                double distanceFromEma = Math.Abs(c0.close - ema9);
                double maxDistance = atr * settings.MaxDistanceAtr;

                if (distanceFromEma > maxDistance)
                {
                    LogFile.Write(
                        $"[SNIPER] {symbol} blocked: too extended from EMA9 | " +
                        $"dist={distanceFromEma:F5}, max={maxDistance:F5}"
                    );
                    return TradeAction.Hold;
                }

                // =========================
                // LATE IMPULSE BLOCK — SOFTENED
                // Old version blocked any previous candle > 1.3 ATR.
                // New version only blocks when previous impulse is large AND price is already stretched.
                // =========================
                double prevRange = c1.high - c1.low;
                bool previousImpulseTooLarge = prevRange > atr * settings.LateImpulseAtr;
                bool alreadyStretched = distanceFromEma > maxDistance * settings.LateImpulseDistanceFactor;

                if (previousImpulseTooLarge && alreadyStretched)
                {
                    LogFile.Write(
                        $"[SNIPER] {symbol} blocked: late impulse + extended | " +
                        $"prevRange={prevRange:F5}, distEMA9={distanceFromEma:F5}"
                    );
                    return TradeAction.Hold;
                }

                // =========================
                // STRUCTURE PATTERN SCORING
                // =========================
                TradeDirection direction = bullish
                    ? TradeDirection.Bullish
                    : TradeDirection.Bearish;

                bool impulseOk = IsImpulse(c2, atr, direction);
                if (impulseOk)
                    score += 2;

                bool pullbackOk = IsControlledPullback(c1, atr, ema9);
                if (pullbackOk)
                    score += 2;

                bool continuationOk = IsContinuation(c0, atr, direction);
                if (continuationOk)
                    score += 2;

                bool brokeStructure = BreaksStructure(ratesH1, c0, atr, direction);
                if (brokeStructure)
                    score += 1;

                // =========================
                // MOMENTUM
                // =========================
                bool strongMomentum = MomentumEvaluator.IsStrong(c0, atr, 0.55, 0.42);

                if (strongMomentum)
                    score += 1;

                // If neither continuation nor momentum is present, do not enter.
                if (!strongMomentum && !continuationOk)
                {
                    LogFile.Write(
                        $"[SNIPER] {symbol} blocked: no continuation or strong momentum | " +
                        $"score={score}"
                    );
                    return TradeAction.Hold;
                }

                // =========================
                // M15 CONFIRMATION
                // =========================
                TradeAction m15Direction = bullish
                    ? TradeAction.Bullish
                    : TradeAction.Bearish;

                bool m15Allowed = false;
                string reason = "";

                if (M15Service != null)
                {
                    m15Allowed = M15Service.IsTradeAllowed(
                        symbol,
                        m15Direction,
                        EntryModel.Sniper,
                        ctx.CurrentMomentum,
                        out reason
                    );

                    if (m15Allowed)
                        score += 1;
                }

                // =========================
                // SCORE ADJUSTMENT
                // =========================
                int minScore = settings.MinScore;

                // If M15 confirms, allow slightly more flexibility.
                if (m15Allowed && minScore > 4)
                    minScore -= 1;

                // If impulse + pullback + continuation are all valid,
                // allow the trade even if structure score is missing.
                bool textbookSniper =
                    impulseOk &&
                    pullbackOk &&
                    continuationOk;

                if (textbookSniper && minScore > 4)
                    minScore -= 1;

                if (score < minScore)
                {
                    LogFile.Write(
                        $"[SNIPER] {symbol} blocked: score too low | " +
                        $"score={score}, need>={minScore}, " +
                        $"impulse={impulseOk}, pullback={pullbackOk}, continuation={continuationOk}, " +
                        $"breakStructure={brokeStructure}, momentum={strongMomentum}, m15={m15Allowed}"
                    );

                    ev.Regime = "Hold";
                    ev.ActionDescription =
                        $"Sniper score too low: {score}/{minScore}. " +
                        $"Impulse={impulseOk}, Pullback={pullbackOk}, Continuation={continuationOk}, " +
                        $"Structure={brokeStructure}, Momentum={strongMomentum}, M15={m15Allowed}";
                    ev.SniperAction = TradeAction.Hold.ToString();

                    return TradeAction.Hold;
                }

                var action = bullish
                    ? TradeAction.EnterBuy
                    : TradeAction.EnterSell;

                LogFile.Write(
                    $"[SNIPER V3] {symbol} ✅ Action={action} | " +
                    $"Score={score}/{minScore}, " +
                    $"Impulse={impulseOk}, Pullback={pullbackOk}, Continuation={continuationOk}, " +
                    $"Structure={brokeStructure}, Momentum={strongMomentum}, M15={m15Allowed}, " +
                    $"ATR%={normalizedAtrPercentile:F2}, DistEMA9={distanceFromEma:F5}"
                );

                ev.Regime = "ContinuationEntry";
                ev.ActionDescription =
                    $"Sniper score = {score}/{minScore}. " +
                    $"Impulse={impulseOk}, Pullback={pullbackOk}, Continuation={continuationOk}, " +
                    $"Structure={brokeStructure}, Momentum={strongMomentum}, M15={m15Allowed}";
                ev.SniperAction = action.ToString();

                return action;
            }
            catch (Exception ex)
            {
                LogFile.Write($"[SNIPER] {symbol} exception: {ex.Message}");
                return TradeAction.Hold;
            }
        }

        // =========================
        // HELPERS
        // =========================

        private static bool IsImpulse(MqlRates c, double atr, TradeDirection dir)
        {
            double range = c.high - c.low;
            if (range <= 0) return false;

            double body = Math.Abs(c.close - c.open);
            double ratio = body / range;

            bool dirOk = dir == TradeDirection.Bullish
                ? c.close > c.open
                : c.close < c.open;

            return dirOk &&
                   range >= atr * ImpulseRangeAtr &&
                   ratio >= ImpulseBodyRatio;
        }

        private static bool IsControlledPullback(MqlRates c, double atr, double ema9)
        {
            double range = c.high - c.low;
            if (range <= 0) return false;

            double body = Math.Abs(c.close - c.open);
            double wickRatio = (range - body) / range;

            double mid = (c.open + c.close) / 2.0;

            bool rangeOk = range <= atr * PullbackMaxAtr;
            bool wickOk = wickRatio <= PullbackWickRatioMax;
            bool nearEma = Math.Abs(mid - ema9) <= atr * PullbackNearEmaAtr;

            return rangeOk && wickOk && nearEma;
        }

        private static bool IsContinuation(MqlRates c, double atr, TradeDirection dir)
        {
            double range = c.high - c.low;
            if (range <= 0) return false;

            double body = Math.Abs(c.close - c.open);
            double ratio = body / range;

            bool dirOk = dir == TradeDirection.Bullish
                ? c.close > c.open
                : c.close < c.open;

            return dirOk &&
                   range >= atr * ContinuationRangeAtr &&
                   ratio >= ContinuationBodyRatio;
        }

        private static bool BreaksStructure(MqlRates[] rates, MqlRates c0, double atr, TradeDirection dir)
        {
            if (rates == null || rates.Length < StructureLookback + 3)
                return false;

            double hh = double.MinValue;
            double ll = double.MaxValue;

            // Exclude c0/live candle from structure calculation.
            // Start from rates[^2], then walk back.
            for (int i = 2; i < 2 + StructureLookback; i++)
            {
                var c = rates[^i];
                hh = Math.Max(hh, c.high);
                ll = Math.Min(ll, c.low);
            }

            double buffer = atr * 0.05;

            return dir == TradeDirection.Bullish
                ? c0.high > hh - buffer
                : c0.low < ll + buffer;
        }

        private static SniperSettings GetSettings(string symbol)
        {
            string s = symbol.ToUpperInvariant();

            // =========================
            // GOLD / METALS
            // =========================
            if (s.Contains("XAU") ||
                s.Contains("GOLD") ||
                s.Contains("XAG") ||
                s.Contains("SILVER"))
            {
                return new SniperSettings
                {
                    MaxDistanceAtr = 1.15,
                    LateImpulseAtr = 1.55,
                    LateImpulseDistanceFactor = 0.90,
                    MinScore = 5,
                    MinAtrPercentile = 0.40
                };
            }

            // =========================
            // CRYPTO
            // =========================
            if (s.Contains("BTC") ||
                s.Contains("ETH") ||
                s.Contains("SOL") ||
                s.Contains("BNB") ||
                s.Contains("XRP") ||
                s.Contains("ADA") ||
                s.Contains("DOGE") ||
                s.Contains("LTC"))
            {
                return new SniperSettings
                {
                    MaxDistanceAtr = 1.35,
                    LateImpulseAtr = 1.70,
                    LateImpulseDistanceFactor = 0.90,
                    MinScore = 5,
                    MinAtrPercentile = 0.42
                };
            }

            // =========================
            // INDICES / CFDs
            // =========================
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
                return new SniperSettings
                {
                    MaxDistanceAtr = 1.05,
                    LateImpulseAtr = 1.55,
                    LateImpulseDistanceFactor = 0.90,
                    MinScore = 5,
                    MinAtrPercentile = 0.40
                };
            }

            // =========================
            // FX DEFAULT
            // =========================
            return new SniperSettings
            {
                MaxDistanceAtr = 0.85,
                LateImpulseAtr = 1.45,
                LateImpulseDistanceFactor = 0.90,
                MinScore = 4,
                MinAtrPercentile = 0.38
            };
        }
    }
}
