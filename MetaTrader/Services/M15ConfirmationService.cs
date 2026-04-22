namespace MetaTrader.Services
{
    using global::MetaTrader.Data.Entities;
    using global::MetaTrader.Enums;
    using MtApi5;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using static global::MetaTrader.Services.TradeServiceEngine;

    public class M15ConfirmationService
    {
        private readonly MtApi5Client _client;
        private readonly ConcurrentDictionary<string, M15Snapshot> _cache = new();

        private const int BarsToFetch = 30;
        private const ENUM_TIMEFRAMES TF = ENUM_TIMEFRAMES.PERIOD_M15;

        public M15ConfirmationService(MtApi5Client client)
        {
            _client = client;
        }

        // =====================================
        // 🧠 ASSET PROFILE
        // =====================================
        public class M15Profile
        {
            public double ImpulseRangeAtr { get; set; }
            public double BodyRatioMin { get; set; }
            public double EmaSeparationAtr { get; set; }
            public double EmaSlopeAtr { get; set; }
        }

        private static readonly Dictionary<string, M15Profile> Profiles = new()
    {
        { "XAUUSD", new M15Profile
            {
                ImpulseRangeAtr = 1.8,
                BodyRatioMin = 0.65,
                EmaSeparationAtr = 0.15,
                EmaSlopeAtr = 0.02   // 🔥 relaxed
            }
        },
        { "FX", new M15Profile
            {
                ImpulseRangeAtr = 1.4,
                BodyRatioMin = 0.55,
                EmaSeparationAtr = 0.08,
                EmaSlopeAtr = 0.015  // 🔥 relaxed
            }
        },
        { "CRYPTO", new M15Profile
            {
                ImpulseRangeAtr = 2.0,
                BodyRatioMin = 0.60,
                EmaSeparationAtr = 0.12,
                EmaSlopeAtr = 0.02   // 🔥 relaxed
            }
        },
        { "INDEX", new M15Profile
            {
                ImpulseRangeAtr = 1.6,
                BodyRatioMin = 0.60,
                EmaSeparationAtr = 0.10,
                EmaSlopeAtr = 0.02   // 🔥 relaxed
            }
        }
    };

        private static M15Profile GetProfile(string symbol)
        {
            if (symbol.Contains("XAU"))
                return Profiles["XAUUSD"];

            if (symbol.Contains("US100") || symbol.Contains("NAS") || symbol.Contains("SPX"))
                return Profiles["INDEX"];

            if (symbol.Length == 6 && symbol.EndsWith("USD"))
                return Profiles["FX"];

            return Profiles["CRYPTO"];
        }

        // =====================================
        // 🔄 UPDATE SNAPSHOT
        // =====================================
        public void Update(string symbol)
        {
            _client.CopyRates(symbol, TF, 0, BarsToFetch, out MqlRates[] rates);
            if (rates == null || rates.Length < 22)
                return;

            var profile = GetProfile(symbol);

            double atr = CalculateATR(rates, 14);
            double ema9 = CalculateEMA(rates, 9);
            double ema21 = CalculateEMA(rates, 21);

            var current = rates[0];
            double range = current.high - current.low;
            double body = Math.Abs(current.close - current.open);

            _cache.TryGetValue(symbol, out var prev);

            var snapshot = new M15Snapshot
            {
                Symbol = symbol,
                CandleTime = rates[^1].time,

                Atr = atr,

                PrevEmaFast = prev?.EmaFast ?? ema9,
                EmaFast = ema9,
                EmaSlow = ema21,

                IsImpulsive =
                    range >= atr * profile.ImpulseRangeAtr &&
                    (body / Math.Max(range, 0.00001)) >= profile.BodyRatioMin,

                Bullish = ema9 > ema21 && ema9 > (prev?.EmaFast ?? ema9),
                Bearish = ema9 < ema21 && ema9 < (prev?.EmaFast ?? ema9)
            };

            _cache[symbol] = snapshot;
        }

        // =====================================
        // 🧠 STRUCTURE READINESS
        // =====================================
        public bool IsStructureReady(string symbol, TradeAction direction)
        {
            if (!_cache.TryGetValue(symbol, out var s))
                return false;

            var profile = GetProfile(symbol);

            // 🔥 Allow impulse for TREND trades
            bool isTrendTrade =
                direction == TradeAction.Bullish ||
                direction == TradeAction.Bearish;

            if (s.IsImpulsive && !isTrendTrade)
                return false;

            // ❌ EMA compression
            if (Math.Abs(s.EmaFast - s.EmaSlow) < s.Atr * profile.EmaSeparationAtr)
                return false;

            // ❌ Weak slope (relaxed threshold)
            double emaSlope = s.EmaFast - s.PrevEmaFast;
            if (Math.Abs(emaSlope) < s.Atr * profile.EmaSlopeAtr)
                return false;

            return true;
        }

        // =====================================
        // 🎯 ENTRY CONFIRMATION
        // =====================================
        public bool IsConfirmed(string symbol, TradeAction bias)
        {
            if (!_cache.TryGetValue(symbol, out var snap))
                return false;

            return bias switch
            {
                TradeAction.Bullish => snap.Bullish,
                TradeAction.Bearish => snap.Bearish,
                _ => false
            };
        }

        // =====================================
        // 🚫 CHOP DETECTION (RELAXED)
        // =====================================
        public bool IsChoppy(string symbol)
        {
            _client.CopyRates(symbol, TF, 0, 5, out MqlRates[] rates);
            if (rates == null || rates.Length < 4)
                return true;

            double atr = CalculateATR(rates, 4);

            bool overlapping =
                Math.Abs(rates[^1].close - rates[^2].close) < atr * 0.2 &&   // 🔥 relaxed
                Math.Abs(rates[^2].close - rates[^3].close) < atr * 0.2;

            return overlapping;
        }

        // =====================================
        // 📊 SNAPSHOT ACCESS
        // =====================================
        public M15Snapshot GetSnapshot(string symbol)
        {
            _cache.TryGetValue(symbol, out var snap);
            return snap;
        }

        // =====================================
        // 🔧 ATR
        // =====================================
        static double CalculateATR(MqlRates[] rates, int period)
        {
            int len = rates.Length;
            if (len <= period)
                return 0;

            double sumTR = 0;

            for (int i = len - period; i < len; i++)
            {
                double high = rates[i].high;
                double low = rates[i].low;
                double prevClose = rates[i - 1].close;

                double tr = Math.Max(high - low,
                             Math.Max(Math.Abs(high - prevClose),
                                      Math.Abs(low - prevClose)));

                sumTR += tr;
            }

            return sumTR / period;
        }

        // =====================================
        // 🔧 EMA
        // =====================================
        private static double CalculateEMA(MqlRates[] rates, int period)
        {
            double k = 2.0 / (period + 1);
            double ema = rates[^period].close;

            for (int i = rates.Length - period + 1; i < rates.Length; i++)
            {
                ema = rates[i].close * k + ema * (1 - k);
            }

            return ema;
        }

        double GetMomentumThreshold(string symbol)
        {
            if (symbol.Contains("XAU")) return 4.2;
            if (symbol.Contains("US100")) return 4.4;
            if (symbol.Length == 6 && symbol.EndsWith("USD")) return 3.8; // FX
            return 4.8; // Crypto
        }

        // =====================================
        // 🎯 MASTER FILTER
        // =====================================
        public bool IsTradeAllowed(
        string symbol,
        TradeAction direction,
        EntryModel model,
        double momentum,
        out string reason)
        {
            reason = string.Empty;

            if (!_cache.TryGetValue(symbol, out var s))
            {
                reason = "No M15 snapshot";
                return false;
            }

            var profile = GetProfile(symbol);

            double emaSlope = s.EmaFast - s.PrevEmaFast;

            bool emaSeparated =
                Math.Abs(s.EmaFast - s.EmaSlow) >= s.Atr * profile.EmaSeparationAtr;

            // 🔥 slightly relaxed slope
            bool slopeStrong =
                Math.Abs(emaSlope) >= s.Atr * (profile.EmaSlopeAtr * 0.8);

            bool isChoppy = IsChoppy(symbol);
            bool isAligned = IsConfirmed(symbol, direction);

            // =====================================
            // 🛰️ SCOUT (EARLY REVERSAL / PROBE)
            // =====================================
            if (model == EntryModel.Scout)
            {
                // ❌ still avoid pure chop
                if (isChoppy)
                {
                    reason = "M15 choppy (scout)";
                    return false;
                }

                // ❌ avoid trading in strong impulse AGAINST scout
                if (s.IsImpulsive && momentum < GetMomentumThreshold(symbol))
                {
                    reason = "Impulse continuation - not reversal";
                    return false;
                }

                // 🔥 allow EMA compression (VERY IMPORTANT)
                // DO NOT block here

                // 🔥 allow weak slope (reversal phase)
                // DO NOT require slopeStrong

                // ⚠️ BUT still require SOME structure awareness
                bool minimalStructure =
                    Math.Abs(s.EmaFast - s.EmaSlow) >= s.Atr * (profile.EmaSeparationAtr * 0.2);

                if (!minimalStructure)
                {
                    reason = "No structure (scout)";
                    return false;
                }

                // 🔥 alignment is OPTIONAL for scout (do not block)

                return true;
            }

            // =====================================
            // 🟡 CONTINUATION (UPDATED)
            // =====================================
            if (model == EntryModel.Continuation)
            {
                // 🔥 allow strong impulse continuation
                if (s.IsImpulsive && momentum < 5.0)
                {
                    reason = "Impulse weak continuation";
                    return false;
                }

                if (!isAligned)
                {
                    reason = "M15 not aligned";
                    return false;
                }

                bool earlyTrend =
                 Math.Abs(s.EmaFast - s.EmaSlow) >= s.Atr * (profile.EmaSeparationAtr * 0.4);

                // 🔥 allow early trend OR strong breakout
                if (!emaSeparated && !earlyTrend && !(s.IsImpulsive && momentum >= GetMomentumThreshold(symbol)))
                {
                    reason = "EMA compressed";
                    return false;
                }

                if (!slopeStrong)
                {
                    reason = "Weak EMA slope";
                    return false;
                }

                if (isChoppy)
                {
                    reason = "M15 choppy";
                    return false;
                }

                return true;
            }

            // =====================================
            // 🎯 SNIPER / REVERSAL
            // =====================================
            if (model == EntryModel.Sniper || model == EntryModel.ExhaustionReversal)
            {
                // ❌ still block impulse for reversals
                if (s.IsImpulsive)
                {
                    reason = "Impulse - bad for reversal";
                    return false;
                }

                if (isChoppy)
                {
                    reason = "M15 choppy";
                    return false;
                }

                if (!emaSeparated)
                {
                    reason = "EMA compressed";
                    return false;
                }

                return true;
            }

            return false;
        }
    }
}
