using MetaTrader.Data.Entities;
using MetaTrader.Util;
using MtApi5;
using System;

namespace MetaTrader.Services.Helper
{
    public static class LiveAdverseMoveGuard
    {
        private class Profile
        {
            public double HardAdverseAtr;
            public double BodyRatio;
            public double MinProfitUsdForEma9Exit;
            public bool UseEma21HardExit;

            // NEW:
            // How far price must move through EMA21
            // against the trade before EMA21 can force an exit.
            public double Ema21ReclaimAtr;
        }

        public static bool ShouldClose(
            TrackedTrade trade,
            string symbol,
            double bid,
            double ask,
            double atr,
            MqlRates[] rates,
            double[] emaFast,
            double[] emaSlow)
        {
            if (trade == null ||
                atr <= 0 ||
                rates == null ||
                rates.Length < 3 ||
                emaFast == null ||
                emaFast.Length < 2 ||
                emaSlow == null ||
                emaSlow.Length < 2)
            {
                return false;
            }

            try
            {
                var profile = GetProfile(symbol);

                // IMPORTANT:
                // This assumes rates[^1] is the latest candle
                // available to this engine.
                //
                // If rates only contains CLOSED candles,
                // then this is the latest CLOSED candle.
                var c0 = rates[^1];

                double ema9 =
                    emaFast[^1];

                double ema21 =
                    emaSlow[^1];

                // For BUY, use bid as executable exit-side price.
                // For SELL, use ask.
                double livePrice =
                    trade.Side == TradeSide.Buy
                        ? bid
                        : ask;


                // ==================================================
                // 1. ADVERSE MOVE FROM ENTRY
                // ==================================================

                double adverseMove =
                    trade.Side == TradeSide.Buy
                        ? trade.EntryPrice - livePrice
                        : livePrice - trade.EntryPrice;

                // Do not treat favorable movement as adverse.
                adverseMove =
                    Math.Max(0.0, adverseMove);

                double adverseAtr =
                    adverseMove / atr;


                // ==================================================
                // 2. CANDLE BODY QUALITY
                // ==================================================

                double range =
                    c0.high - c0.low;

                double body =
                    Math.Abs(
                        c0.close - c0.open
                    );

                double bodyRatio =
                    range > 0
                        ? body / range
                        : 0;


                bool oppositeStrongCandle =
                    trade.Side == TradeSide.Buy
                        ? c0.close < c0.open &&
                          bodyRatio >= profile.BodyRatio

                        : c0.close > c0.open &&
                          bodyRatio >= profile.BodyRatio;


                // ==================================================
                // 3. EMA POSITION AGAINST TRADE
                // ==================================================

                bool ema9AgainstTrade =
                    trade.Side == TradeSide.Buy
                        ? livePrice < ema9
                        : livePrice > ema9;


                bool ema21AgainstTrade =
                    trade.Side == TradeSide.Buy
                        ? livePrice < ema21
                        : livePrice > ema21;


                // ==================================================
                // 4. EMA21 RECLAIM DISTANCE
                //
                // BUY:
                // price must move meaningfully BELOW EMA21.
                //
                // SELL:
                // price must move meaningfully ABOVE EMA21.
                // ==================================================

                double ema21AdverseDistanceAtr =
                    trade.Side == TradeSide.Buy
                        ? (ema21 - livePrice) / atr
                        : (livePrice - ema21) / atr;

                ema21AdverseDistanceAtr =
                    Math.Max(
                        0.0,
                        ema21AdverseDistanceAtr
                    );


                bool decisiveEma21Reclaim =
                    ema21AgainstTrade &&
                    ema21AdverseDistanceAtr >=
                        profile.Ema21ReclaimAtr;


                // ==================================================
                // 🔴 5. HARD ADVERSE MOVE
                //
                // This is still available as an emergency
                // adverse-structure exit.
                //
                // It requires BOTH:
                //
                // - enough adverse ATR movement
                // - a strong opposite candle
                // ==================================================

                if (adverseAtr >=
                        profile.HardAdverseAtr &&
                    oppositeStrongCandle)
                {
                    LogFile.Write(
                        $"[{symbol}] LiveAdverse EXIT: hard adverse move | " +
                        $"side={trade.Side}, " +
                        $"adverseATR={adverseAtr:F2}, " +
                        $"requiredATR={profile.HardAdverseAtr:F2}, " +
                        $"bodyRatio={bodyRatio:F2}, " +
                        $"requiredBody={profile.BodyRatio:F2}, " +
                        $"price={livePrice:F5}"
                    );

                    return true;
                }


                // ==================================================
                // 🟠 6. DECISIVE EMA21 FAILURE
                //
                // OLD:
                //
                // price crosses EMA21
                // +
                // opposite candle
                // = exit
                //
                // NEW:
                //
                // price must move meaningfully through EMA21
                // by an ATR-based amount.
                //
                // This helps Gold survive normal EMA21 pullbacks.
                // ==================================================

                if (profile.UseEma21HardExit &&
                    decisiveEma21Reclaim &&
                    oppositeStrongCandle)
                {
                    LogFile.Write(
                        $"[{symbol}] LiveAdverse EXIT: decisive EMA21 reclaim | " +
                        $"side={trade.Side}, " +
                        $"price={livePrice:F5}, " +
                        $"ema21={ema21:F5}, " +
                        $"reclaimATR={ema21AdverseDistanceAtr:F2}, " +
                        $"requiredATR={profile.Ema21ReclaimAtr:F2}, " +
                        $"bodyRatio={bodyRatio:F2}, " +
                        $"requiredBody={profile.BodyRatio:F2}"
                    );

                    return true;
                }


                // ==================================================
                // 🟡 7. EMA9 FAILURE AFTER MEANINGFUL PROFIT
                //
                // This only becomes active after the trade
                // has already made enough peak profit.
                //
                // It still requires a strong opposite candle.
                // ==================================================

                if (trade.MaxProfitUsd >=
                        profile.MinProfitUsdForEma9Exit &&
                    ema9AgainstTrade &&
                    oppositeStrongCandle)
                {
                    LogFile.Write(
                        $"[{symbol}] LiveAdverse EXIT: EMA9 failure after profit | " +
                        $"side={trade.Side}, " +
                        $"price={livePrice:F5}, " +
                        $"ema9={ema9:F5}, " +
                        $"maxProfit={trade.MaxProfitUsd:F2}, " +
                        $"minProfitRequired={profile.MinProfitUsdForEma9Exit:F2}, " +
                        $"bodyRatio={bodyRatio:F2}"
                    );

                    return true;
                }


                // ==================================================
                // OPTIONAL DIAGNOSTIC
                //
                // Useful when price crosses an EMA but the
                // guard correctly decides NOT to exit.
                // ==================================================

                if (ema21AgainstTrade ||
                    ema9AgainstTrade)
                {
                    LogFile.Write(
                        $"[{symbol}] LiveAdverse HOLD | " +
                        $"side={trade.Side}, " +
                        $"price={livePrice:F5}, " +
                        $"ema9={ema9:F5}, " +
                        $"ema21={ema21:F5}, " +
                        $"ema9Against={ema9AgainstTrade}, " +
                        $"ema21Against={ema21AgainstTrade}, " +
                        $"ema21ReclaimATR={ema21AdverseDistanceAtr:F2}/" +
                        $"{profile.Ema21ReclaimAtr:F2}, " +
                        $"adverseATR={adverseAtr:F2}/" +
                        $"{profile.HardAdverseAtr:F2}, " +
                        $"bodyRatio={bodyRatio:F2}/" +
                        $"{profile.BodyRatio:F2}, " +
                        $"maxProfit={trade.MaxProfitUsd:F2}"
                    );
                }

                return false;
            }
            catch (Exception ex)
            {
                LogFile.Write(
                    $"[{symbol}] LiveAdverseMoveGuard error: {ex.Message}"
                );

                return false;
            }
        }


        // ==================================================
        // SYMBOL-SENSITIVE PROFILE
        // ==================================================

        private static Profile GetProfile(
            string symbol)
        {
            string s =
                symbol?.ToUpperInvariant() ?? "";


            // ==================================================
            // 🥇 GOLD / METALS
            //
            // More breathing room.
            //
            // Gold frequently crosses EMA9 / EMA21 during
            // normal pullbacks without actually reversing.
            // ==================================================

            if (IsGold(s))
            {
                return new Profile
                {
                    HardAdverseAtr = 0.55,

                    BodyRatio = 0.62,

                    MinProfitUsdForEma9Exit = 5.00,

                    UseEma21HardExit = true,

                    Ema21ReclaimAtr = 0.20
                };
            }


            // ==================================================
            // ₿ CRYPTO
            // ==================================================

            if (IsCrypto(s))
            {
                return new Profile
                {
                    HardAdverseAtr = 0.70,

                    BodyRatio = 0.58,

                    MinProfitUsdForEma9Exit = 3.50,

                    UseEma21HardExit = true,

                    Ema21ReclaimAtr = 0.18
                };
            }


            // ==================================================
            // 📈 INDICES
            // ==================================================

            if (IsIndex(s))
            {
                return new Profile
                {
                    HardAdverseAtr = 0.65,

                    BodyRatio = 0.58,

                    MinProfitUsdForEma9Exit = 4.00,

                    UseEma21HardExit = true,

                    Ema21ReclaimAtr = 0.18
                };
            }


            // ==================================================
            // 💱 FX DEFAULT
            // ==================================================

            return new Profile
            {
                HardAdverseAtr = 0.55,

                BodyRatio = 0.55,

                MinProfitUsdForEma9Exit = 3.00,

                UseEma21HardExit = true,

                Ema21ReclaimAtr = 0.15
            };
        }


        // ==================================================
        // GOLD / METALS
        // ==================================================

        private static bool IsGold(
            string symbol)
        {
            return
                symbol.Contains("XAU") ||
                symbol.Contains("XAG") ||
                symbol.Contains("GOLD") ||
                symbol.Contains("SILVER");
        }


        // ==================================================
        // CRYPTO
        // ==================================================

        private static bool IsCrypto(
            string symbol)
        {
            return
                symbol.Contains("BTC") ||
                symbol.Contains("ETH") ||
                symbol.Contains("SOL") ||
                symbol.Contains("BNB") ||
                symbol.Contains("XRP") ||
                symbol.Contains("ADA") ||
                symbol.Contains("DOGE") ||
                symbol.Contains("LTC") ||
                symbol.Contains("AVAX") ||
                symbol.Contains("LINK");
        }


        // ==================================================
        // INDICES
        // ==================================================

        private static bool IsIndex(
            string symbol)
        {
            return
                symbol.Contains("US100") ||
                symbol.Contains("NAS") ||
                symbol.Contains("USTEC") ||
                symbol.Contains("US30") ||
                symbol.Contains("DJ") ||
                symbol.Contains("SPX") ||
                symbol.Contains("US500") ||
                symbol.Contains("GER") ||
                symbol.Contains("DAX") ||
                symbol.Contains("UK100");
        }
    }
}
