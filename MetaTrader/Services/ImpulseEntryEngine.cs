using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;

namespace MetaTrader.Services
{
    public static class ImpulseEntryEngine
    {
        private class ImpulseProfile
        {
            public double BodyThreshold { get; set; }
            public double CounterTrendBoost { get; set; }

            public double EarlyBody { get; set; }
            public double EarlyDistance { get; set; }
            public double MinEmaDistance { get; set; }
            public double MaxEmaDistance { get; set; }
            public double EarlyBreakAllowance { get; set; }

            public double TransitionBody { get; set; }
            public double TransitionMaxEmaDistance { get; set; }
            public double TransitionBeforeBreakout { get; set; }
            public double TransitionBeyondBreakout { get; set; }

            public double NormalMaxBeyondBreakout { get; set; }
        }

        public static bool TryEnter(
            string symbol,
            MqlRates liveCandle,
            double atr,
            double ema9,
            double ema9Previous,
            double ema21,
            double recentHigh,
            double recentLow,
            double bid,
            double ask,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            try
            {
                if (atr <= 0)
                    return false;

                symbol = symbol.ToUpperInvariant();

                var profile = GetProfile(symbol);

                // ==================================================
                // 📊 LIVE DIRECTIONAL BODY
                // ==================================================

                double liveBullBody =
                    Math.Max(0.0, ask - liveCandle.open);

                double liveBearBody =
                    Math.Max(0.0, liveCandle.open - bid);

                double bodyAtrBuy =
                    liveBullBody / atr;

                double bodyAtrSell =
                    liveBearBody / atr;


                // ==================================================
                // 📏 DISTANCE FROM EMA9
                // ==================================================

                double distFromEmaBuy =
                    Math.Abs(ask - ema9) / atr;

                double distFromEmaSell =
                    Math.Abs(bid - ema9) / atr;


                // ==================================================
                // 📈 EMA DIRECTION / TREND ALIGNMENT
                // ==================================================

                bool ema9Rising =
                    ema9 > ema9Previous;

                bool ema9Falling =
                    ema9 < ema9Previous;

                bool overallBullish =
                    ema9 > ema21;

                bool overallBearish =
                    ema9 < ema21;


                // ==================================================
                // 📍 BREAKOUT DISTANCES
                //
                // Positive:
                // price has already broken structure
                //
                // Negative:
                // price is still approaching structure
                // ==================================================

                double breakoutDistanceBuy =
                    (ask - recentHigh) / atr;

                double breakoutDistanceSell =
                    (recentLow - bid) / atr;

                double distToBreakout =
                    (recentHigh - ask) / atr;

                double distToBreakdown =
                    (bid - recentLow) / atr;


                // ==================================================
                // ⚙️ NORMAL IMPULSE BODY REQUIREMENT
                // ==================================================

                double buyRequiredBody =
                    overallBullish
                        ? profile.BodyThreshold
                        : profile.BodyThreshold +
                          profile.CounterTrendBoost;

                double sellRequiredBody =
                    overallBearish
                        ? profile.BodyThreshold
                        : profile.BodyThreshold +
                          profile.CounterTrendBoost;


                // ==================================================
                // 🚀 TRANSITION IMPULSE BUY
                //
                // Designed to catch the START of a new expansion
                // before EMA9 / EMA21 are fully aligned.
                // ==================================================

                bool bullishLiveCandle =
                    ask > liveCandle.open;

                bool nearOrBreakingHigh =
                    breakoutDistanceBuy >=
                        -profile.TransitionBeforeBreakout &&
                    breakoutDistanceBuy <=
                        profile.TransitionBeyondBreakout;

                bool transitionImpulseBuy =
                    bullishLiveCandle &&
                    bodyAtrBuy >= profile.TransitionBody &&
                    ema9Rising &&
                    ask > ema9 &&
                    distFromEmaBuy >= profile.MinEmaDistance &&
                    distFromEmaBuy <=
                        profile.TransitionMaxEmaDistance &&
                    nearOrBreakingHigh;

                if (transitionImpulseBuy)
                {
                    action = TradeAction.EnterBuy;

                    LogFile.Write(
                        $"[{symbol}] 🚀 TRANSITION IMPULSE BUY | " +
                        $"bodyATR={bodyAtrBuy:F2} | " +
                        $"required={profile.TransitionBody:F2} | " +
                        $"emaDist={distFromEmaBuy:F2} | " +
                        $"breakDist={breakoutDistanceBuy:F2} | " +
                        $"ema9Rising={ema9Rising} | " +
                        $"trendAligned={overallBullish} | " +
                        $"ema9={ema9:F5} | " +
                        $"ema21={ema21:F5}"
                    );

                    return true;
                }


                // ==================================================
                // 🚀 TRANSITION IMPULSE SELL
                // ==================================================

                bool bearishLiveCandle =
                    bid < liveCandle.open;

                bool nearOrBreakingLow =
                    breakoutDistanceSell >=
                        -profile.TransitionBeforeBreakout &&
                    breakoutDistanceSell <=
                        profile.TransitionBeyondBreakout;

                bool transitionImpulseSell =
                    bearishLiveCandle &&
                    bodyAtrSell >= profile.TransitionBody &&
                    ema9Falling &&
                    bid < ema9 &&
                    distFromEmaSell >= profile.MinEmaDistance &&
                    distFromEmaSell <=
                        profile.TransitionMaxEmaDistance &&
                    nearOrBreakingLow;

                if (transitionImpulseSell)
                {
                    action = TradeAction.EnterSell;

                    LogFile.Write(
                        $"[{symbol}] 🚀 TRANSITION IMPULSE SELL | " +
                        $"bodyATR={bodyAtrSell:F2} | " +
                        $"required={profile.TransitionBody:F2} | " +
                        $"emaDist={distFromEmaSell:F2} | " +
                        $"breakDist={breakoutDistanceSell:F2} | " +
                        $"ema9Falling={ema9Falling} | " +
                        $"trendAligned={overallBearish} | " +
                        $"ema9={ema9:F5} | " +
                        $"ema21={ema21:F5}"
                    );

                    return true;
                }


                // ==================================================
                // ⚡ EARLY IMPULSE BUY
                // ==================================================

                bool earlyImpulseBuy =
                    bullishLiveCandle &&
                    bodyAtrBuy >= profile.EarlyBody &&
                    ask > ema9 &&
                    distFromEmaBuy >
                        profile.MinEmaDistance &&
                    distFromEmaBuy <
                        profile.MaxEmaDistance &&
                    distToBreakout >
                        -profile.EarlyBreakAllowance &&
                    distToBreakout <
                        profile.EarlyDistance;

                if (earlyImpulseBuy)
                {
                    action = TradeAction.EnterBuy;

                    LogFile.Write(
                        $"[{symbol}] ⚡ EARLY IMPULSE BUY | " +
                        $"distToBreakout={distToBreakout:F2} | " +
                        $"breakAllowance={profile.EarlyBreakAllowance:F2} | " +
                        $"bodyATR={bodyAtrBuy:F2} | " +
                        $"required={profile.EarlyBody:F2} | " +
                        $"emaDist={distFromEmaBuy:F2} | " +
                        $"ema9Rising={ema9Rising}"
                    );

                    return true;
                }


                // ==================================================
                // ⚡ EARLY IMPULSE SELL
                // ==================================================

                bool earlyImpulseSell =
                    bearishLiveCandle &&
                    bodyAtrSell >= profile.EarlyBody &&
                    bid < ema9 &&
                    distFromEmaSell >
                        profile.MinEmaDistance &&
                    distFromEmaSell <
                        profile.MaxEmaDistance &&
                    distToBreakdown >
                        -profile.EarlyBreakAllowance &&
                    distToBreakdown <
                        profile.EarlyDistance;

                if (earlyImpulseSell)
                {
                    action = TradeAction.EnterSell;

                    LogFile.Write(
                        $"[{symbol}] ⚡ EARLY IMPULSE SELL | " +
                        $"distToBreakdown={distToBreakdown:F2} | " +
                        $"breakAllowance={profile.EarlyBreakAllowance:F2} | " +
                        $"bodyATR={bodyAtrSell:F2} | " +
                        $"required={profile.EarlyBody:F2} | " +
                        $"emaDist={distFromEmaSell:F2} | " +
                        $"ema9Falling={ema9Falling}"
                    );

                    return true;
                }


                // ==================================================
                // 🔥 NORMAL IMPULSE BUY
                //
                // Anti-chase is measured from breakout level,
                // NOT from an old swing low.
                // ==================================================

                bool impulseBuy =
                    bullishLiveCandle &&
                    bodyAtrBuy >= buyRequiredBody &&
                    ask > recentHigh &&
                    ask > ema9 &&
                    distFromEmaBuy <
                        profile.MaxEmaDistance &&
                    breakoutDistanceBuy <=
                        profile.NormalMaxBeyondBreakout;

                if (impulseBuy)
                {
                    action = TradeAction.EnterBuy;

                    LogFile.Write(
                        $"[{symbol}] ⚡ IMPULSE BUY | " +
                        $"bodyATR={bodyAtrBuy:F2} | " +
                        $"required={buyRequiredBody:F2} | " +
                        $"beyondBreakout={breakoutDistanceBuy:F2} | " +
                        $"maxBeyond={profile.NormalMaxBeyondBreakout:F2} | " +
                        $"distEMA={distFromEmaBuy:F2} | " +
                        $"trendAligned={overallBullish} | " +
                        $"ema9Rising={ema9Rising}"
                    );

                    return true;
                }


                // ==================================================
                // 🔻 NORMAL IMPULSE SELL
                // ==================================================

                bool impulseSell =
                    bearishLiveCandle &&
                    bodyAtrSell >= sellRequiredBody &&
                    bid < recentLow &&
                    bid < ema9 &&
                    distFromEmaSell <
                        profile.MaxEmaDistance &&
                    breakoutDistanceSell <=
                        profile.NormalMaxBeyondBreakout;

                if (impulseSell)
                {
                    action = TradeAction.EnterSell;

                    LogFile.Write(
                        $"[{symbol}] ⚡ IMPULSE SELL | " +
                        $"bodyATR={bodyAtrSell:F2} | " +
                        $"required={sellRequiredBody:F2} | " +
                        $"beyondBreakout={breakoutDistanceSell:F2} | " +
                        $"maxBeyond={profile.NormalMaxBeyondBreakout:F2} | " +
                        $"distEMA={distFromEmaSell:F2} | " +
                        $"trendAligned={overallBearish} | " +
                        $"ema9Falling={ema9Falling}"
                    );

                    return true;
                }


                // ==================================================
                // 🧾 DIAGNOSTIC
                //
                // Useful when a strong move was visible but
                // the engine did not enter.
                // ==================================================

                if (bodyAtrBuy >= 0.45 ||
                    bodyAtrSell >= 0.45)
                {
                    LogFile.Write(
                        $"[{symbol}] Impulse not triggered | " +
                        $"buyBodyATR={bodyAtrBuy:F2} | " +
                        $"sellBodyATR={bodyAtrSell:F2} | " +
                        $"transitionReq={profile.TransitionBody:F2} | " +
                        $"earlyReq={profile.EarlyBody:F2} | " +
                        $"normalBuyReq={buyRequiredBody:F2} | " +
                        $"normalSellReq={sellRequiredBody:F2} | " +
                        $"emaBuyDist={distFromEmaBuy:F2} | " +
                        $"emaSellDist={distFromEmaSell:F2} | " +
                        $"breakBuy={breakoutDistanceBuy:F2} | " +
                        $"breakSell={breakoutDistanceSell:F2} | " +
                        $"ema9Rising={ema9Rising} | " +
                        $"ema9Falling={ema9Falling} | " +
                        $"bullAligned={overallBullish} | " +
                        $"bearAligned={overallBearish}"
                    );
                }

                return false;
            }
            catch (Exception ex)
            {
                LogFile.Write(
                    $"[{symbol}] ImpulseEntryEngine error: " +
                    $"{ex.Message}"
                );

                action = TradeAction.Hold;
                return false;
            }
        }


        // ==================================================
        // ⚙️ ASSET-SPECIFIC PROFILE
        // ==================================================

        private static ImpulseProfile GetProfile(string symbol)
        {
            symbol = symbol.ToUpperInvariant();

            // ==================================================
            // 🥇 GOLD / METALS
            // ==================================================

            if (IsGold(symbol))
            {
                return new ImpulseProfile
                {
                    BodyThreshold = 1.00,
                    CounterTrendBoost = 0.35,

                    EarlyBody = 0.60,
                    EarlyDistance = 0.50,
                    MinEmaDistance = 0.10,
                    MaxEmaDistance = 1.80,
                    EarlyBreakAllowance = 0.12,

                    TransitionBody = 0.65,
                    TransitionMaxEmaDistance = 1.35,
                    TransitionBeforeBreakout = 0.35,
                    TransitionBeyondBreakout = 0.50,

                    NormalMaxBeyondBreakout = 0.60
                };
            }


            // ==================================================
            // ₿ CRYPTO
            // ==================================================

            if (IsCrypto(symbol))
            {
                return new ImpulseProfile
                {
                    BodyThreshold = 0.85,
                    CounterTrendBoost = 0.25,

                    EarlyBody = 0.70,
                    EarlyDistance = 0.40,
                    MinEmaDistance = 0.12,
                    MaxEmaDistance = 2.50,
                    EarlyBreakAllowance = 0.12,

                    TransitionBody = 0.75,
                    TransitionMaxEmaDistance = 1.75,
                    TransitionBeforeBreakout = 0.30,
                    TransitionBeyondBreakout = 0.60,

                    NormalMaxBeyondBreakout = 0.80
                };
            }


            // ==================================================
            // 📈 INDICES
            // ==================================================

            if (IsIndex(symbol))
            {
                return new ImpulseProfile
                {
                    BodyThreshold = 0.90,
                    CounterTrendBoost = 0.30,

                    EarlyBody = 0.50,
                    EarlyDistance = 0.60,
                    MinEmaDistance = 0.10,
                    MaxEmaDistance = 2.00,
                    EarlyBreakAllowance = 0.12,

                    TransitionBody = 0.60,
                    TransitionMaxEmaDistance = 1.50,
                    TransitionBeforeBreakout = 0.40,
                    TransitionBeyondBreakout = 0.60,

                    NormalMaxBeyondBreakout = 0.70
                };
            }


            // ==================================================
            // 💱 FOREX
            // ==================================================

            if (IsFx(symbol))
            {
                return new ImpulseProfile
                {
                    BodyThreshold = 0.60,
                    CounterTrendBoost = 0.20,

                    EarlyBody = 0.50,
                    EarlyDistance = 0.50,
                    MinEmaDistance = 0.06,
                    MaxEmaDistance = 1.60,
                    EarlyBreakAllowance = 0.08,

                    TransitionBody = 0.55,
                    TransitionMaxEmaDistance = 1.20,
                    TransitionBeforeBreakout = 0.25,
                    TransitionBeyondBreakout = 0.40,

                    NormalMaxBeyondBreakout = 0.50
                };
            }


            // ==================================================
            // DEFAULT
            // ==================================================

            return new ImpulseProfile
            {
                BodyThreshold = 0.90,
                CounterTrendBoost = 0.25,

                EarlyBody = 0.60,
                EarlyDistance = 0.50,
                MinEmaDistance = 0.10,
                MaxEmaDistance = 2.00,
                EarlyBreakAllowance = 0.10,

                TransitionBody = 0.65,
                TransitionMaxEmaDistance = 1.50,
                TransitionBeforeBreakout = 0.30,
                TransitionBeyondBreakout = 0.50,

                NormalMaxBeyondBreakout = 0.60
            };
        }


        // ==================================================
        // 🥇 GOLD / METALS
        // ==================================================

        private static bool IsGold(string symbol)
        {
            return
                symbol.Contains("XAU") ||
                symbol.Contains("XAG") ||
                symbol.Contains("GOLD") ||
                symbol.Contains("SILVER");
        }


        // ==================================================
        // ₿ CRYPTO
        // ==================================================

        private static bool IsCrypto(string symbol)
        {
            return
                symbol.Contains("BTC") ||
                symbol.Contains("ETH") ||
                symbol.Contains("SOL") ||
                symbol.Contains("BNB") ||
                symbol.Contains("XRP") ||
                symbol.Contains("ADA") ||
                symbol.Contains("DOGE") ||
                symbol.Contains("LTC");
        }


        // ==================================================
        // 📈 INDICES
        // ==================================================

        private static bool IsIndex(string symbol)
        {
            return
                symbol.Contains("US100") ||
                symbol.Contains("NAS") ||
                symbol.Contains("SPX") ||
                symbol.Contains("US500") ||
                symbol.Contains("US30") ||
                symbol.Contains("DJI") ||
                symbol.Contains("GER40") ||
                symbol.Contains("UK100") ||
                symbol.Contains("JP225") ||
                symbol.Contains("INDEX") ||
                symbol.Contains(".CASH");
        }


        // ==================================================
        // 💱 FOREX
        // ==================================================

        private static bool IsFx(string symbol)
        {
            if (symbol.Length < 6)
                return false;

            string[] ccy =
            {
            "USD",
            "EUR",
            "GBP",
            "JPY",
            "AUD",
            "NZD",
            "CAD",
            "CHF"
        };

            foreach (var baseCcy in ccy)
            {
                foreach (var quoteCcy in ccy)
                {
                    if (baseCcy == quoteCcy)
                        continue;

                    if (symbol.StartsWith(baseCcy) &&
                        symbol.Contains(quoteCcy))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
