using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.helper;
using MetaTrader.Util;
using MtApi5;
using System;

namespace MetaTrader.Services
{
    public static class SupportResistanceEngine
    {
        public static int Lookback = 12;

        private class SrLevel
        {
            public double Price;
            public int Touches;
            public bool Valid;
        }

        // ============================================================
        // SYMBOL TYPE DETECTION
        // ============================================================

        private static bool IsGold(string symbol)
        {
            symbol = symbol?.ToUpperInvariant() ?? "";

            return
                symbol.Contains("XAU") ||
                symbol.Contains("XAG") ||
                symbol.Contains("GOLD") ||
                symbol.Contains("SILVER");
        }

        private static bool IsCrypto(string symbol)
        {
            symbol = symbol?.ToUpperInvariant() ?? "";

            return
                symbol.Contains("BTC") ||
                symbol.Contains("ETH") ||
                symbol.Contains("SOL") ||
                symbol.Contains("BNB") ||
                symbol.Contains("XRP") ||
                symbol.Contains("ADA") ||
                symbol.Contains("AVAX") ||
                symbol.Contains("LINK") ||
                symbol.Contains("DOGE") ||
                symbol.Contains("LTC");
        }

        private static bool IsIndex(string symbol)
        {
            symbol = symbol?.ToUpperInvariant() ?? "";

            return
                symbol.Contains("US100") ||
                symbol.Contains("NAS") ||
                symbol.Contains("USTEC") ||
                symbol.Contains("US30") ||
                symbol.Contains("US500") ||
                symbol.Contains("SPX") ||
                symbol.Contains("DAX") ||
                symbol.Contains("GER") ||
                symbol.Contains("UK100") ||
                symbol.Contains(".CASH");
        }

        private static bool IsFx(string symbol)
        {
            return
                !IsGold(symbol) &&
                !IsCrypto(symbol) &&
                !IsIndex(symbol);
        }


        // ============================================================
        // ADAPTIVE PARAMETERS
        // ============================================================

        private static double GetLevelClusterAtr(string symbol)
        {
            if (IsGold(symbol))
                return 0.45;

            if (IsFx(symbol))
                return 0.35;

            if (IsIndex(symbol))
                return 0.40;

            if (IsCrypto(symbol))
                return 0.40;

            return 0.40;
        }


        private static double GetMomentumThreshold(string symbol)
        {
            if (IsGold(symbol))
                return 1.35;

            if (IsFx(symbol))
                return 1.10;

            if (IsIndex(symbol))
                return 1.15;

            if (IsCrypto(symbol))
                return 1.20;

            return 1.20;
        }


        // ============================================================
        // NORMAL MINIMUM RUNWAY
        //
        // If resistance/support is ahead, we generally want at least
        // this much room before entering.
        // ============================================================

        private static double GetMinimumSpaceAtr(string symbol)
        {
            if (IsGold(symbol))
                return 0.50;

            if (IsCrypto(symbol))
                return 0.60;

            if (IsIndex(symbol))
                return 0.50;

            if (IsFx(symbol))
                return 0.40;

            return 0.40;
        }


        // ============================================================
        // CRITICAL NO-SPACE LEVEL
        //
        // IMPORTANT:
        //
        // Below this amount of runway the setup is too close to a
        // structural wall.
        //
        // Strong impulse must NOT override this.
        //
        // The XAUEUR trade had only 0.07 ATR of space.
        // Gold critical floor = 0.25 ATR, therefore it would be blocked.
        // ============================================================

        public static double GetCriticalSpaceAtr(string symbol)
        {
            if (IsGold(symbol))
                return 0.25;

            if (IsCrypto(symbol))
                return 0.30;

            if (IsIndex(symbol))
                return 0.25;

            if (IsFx(symbol))
                return 0.20;

            return 0.20;
        }


        // ============================================================
        // MAXIMUM DISTANCE FROM EMA9
        // ============================================================

        private static double GetMaxEmaDistanceAtr(string symbol)
        {
            if (IsGold(symbol))
                return 1.25;

            if (IsCrypto(symbol))
                return 1.50;

            if (IsIndex(symbol))
                return 1.25;

            if (IsFx(symbol))
                return 1.10;

            return 1.10;
        }


        // ============================================================
        // RECENTLY-CROSSED SR MEMORY
        //
        // A level must not disappear the instant price moves a few
        // points through it.  This is the exact failure that allowed
        // BNBUSD to sell at 709.50 immediately after support at 709.54
        // had correctly blocked the trade one second earlier.
        // ============================================================

        private static double GetCrossedWallMemoryAtr(string symbol)
        {
            if (IsGold(symbol))
                return 0.35;

            if (IsCrypto(symbol))
                return 0.40;

            if (IsIndex(symbol))
                return 0.35;

            if (IsFx(symbol))
                return 0.30;

            return 0.35;
        }


        private static double GetBreakoutClearanceAtr(string symbol)
        {
            if (IsGold(symbol))
                return 0.20;

            if (IsCrypto(symbol))
                return 0.25;

            if (IsIndex(symbol))
                return 0.20;

            if (IsFx(symbol))
                return 0.15;

            return 0.20;
        }


        // ============================================================
        // BASIC HIGH / LOW LEVELS
        // ============================================================

        private static (double highest, double lowest)
            GetLevels(MqlRates[] rates)
        {
            double highest =
                double.MinValue;

            double lowest =
                double.MaxValue;


            for (int i = 2;
                 i < Lookback + 2 &&
                 i < rates.Length;
                 i++)
            {
                highest =
                    Math.Max(
                        highest,
                        rates[^i].high
                    );

                lowest =
                    Math.Min(
                        lowest,
                        rates[^i].low
                    );
            }


            return
                (highest, lowest);
        }


        // ============================================================
        // NEAREST STRUCTURAL WALL HELPERS
        //
        // IMPORTANT:
        // A support/resistance level remains relevant for a short ATR
        // distance AFTER price crosses it.  This prevents a tiny poke
        // through a level from turning:
        //
        //     "0.01 ATR to support -> BLOCK"
        //
        // into:
        //
        //     "no support -> huge runway -> ALLOW"
        //
        // on the very next tick.
        // ============================================================

        private static double GetNearestRelevantResistance(
            double currentPrice,
            double shelfResistance,
            bool shelfValid,
            double lookbackHighest,
            double recentHigh,
            double atr,
            double crossedWallMemoryAtr)
        {
            double nearest = 0.0;
            double nearestDistance = double.MaxValue;

            void Consider(double level, bool valid)
            {
                if (!valid || level <= 0)
                    return;

                bool abovePrice =
                    level >= currentPrice;

                bool recentlyCrossed =
                    level < currentPrice &&
                    (currentPrice - level) / atr <=
                        crossedWallMemoryAtr;

                if (!abovePrice && !recentlyCrossed)
                    return;

                double distance =
                    Math.Abs(level - currentPrice);

                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = level;
                }
            }

            Consider(
                shelfResistance,
                shelfValid
            );

            Consider(
                lookbackHighest,
                lookbackHighest > 0
            );

            Consider(
                recentHigh,
                recentHigh > 0
            );

            return nearest;
        }


        private static double GetNearestRelevantSupport(
            double currentPrice,
            double shelfSupport,
            bool shelfValid,
            double lookbackLowest,
            double recentLow,
            double atr,
            double crossedWallMemoryAtr)
        {
            double nearest = 0.0;
            double nearestDistance = double.MaxValue;

            void Consider(double level, bool valid)
            {
                if (!valid || level <= 0)
                    return;

                bool belowPrice =
                    level <= currentPrice;

                bool recentlyCrossed =
                    level > currentPrice &&
                    (level - currentPrice) / atr <=
                        crossedWallMemoryAtr;

                if (!belowPrice && !recentlyCrossed)
                    return;

                double distance =
                    Math.Abs(level - currentPrice);

                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = level;
                }
            }

            Consider(
                shelfSupport,
                shelfValid
            );

            Consider(
                lookbackLowest,
                lookbackLowest > 0
            );

            Consider(
                recentLow,
                recentLow > 0
            );

            return nearest;
        }


        // ============================================================
        // SUPPORT SHELF
        // ============================================================

        private static SrLevel GetSupportShelf(
            string symbol,
            MqlRates[] rates,
            double atr)
        {
            double cluster =
                atr *
                GetLevelClusterAtr(symbol);


            double bestLevel = 0;

            int bestTouches = 0;


            for (int i = 2;
                 i < Lookback + 2 &&
                 i < rates.Length;
                 i++)
            {
                double candidate =
                    rates[^i].low;


                int touches = 0;


                for (int j = 2;
                     j < Lookback + 2 &&
                     j < rates.Length;
                     j++)
                {
                    double candleBodyLow =
                        Math.Min(
                            rates[^j].open,
                            rates[^j].close
                        );


                    if (
                        Math.Abs(
                            rates[^j].low -
                            candidate
                        ) <= cluster
                        ||
                        Math.Abs(
                            candleBodyLow -
                            candidate
                        ) <= cluster
                    )
                    {
                        touches++;
                    }
                }


                if (touches >
                    bestTouches)
                {
                    bestTouches =
                        touches;

                    bestLevel =
                        candidate;
                }
            }


            return new SrLevel
            {
                Price =
                    bestLevel,

                Touches =
                    bestTouches,

                Valid =
                    bestTouches >= 2
            };
        }


        // ============================================================
        // RESISTANCE SHELF
        // ============================================================

        private static SrLevel GetResistanceShelf(
            string symbol,
            MqlRates[] rates,
            double atr)
        {
            double cluster =
                atr *
                GetLevelClusterAtr(symbol);


            double bestLevel = 0;

            int bestTouches = 0;


            for (int i = 2;
                 i < Lookback + 2 &&
                 i < rates.Length;
                 i++)
            {
                double candidate =
                    rates[^i].high;


                int touches = 0;


                for (int j = 2;
                     j < Lookback + 2 &&
                     j < rates.Length;
                     j++)
                {
                    double candleBodyHigh =
                        Math.Max(
                            rates[^j].open,
                            rates[^j].close
                        );


                    if (
                        Math.Abs(
                            rates[^j].high -
                            candidate
                        ) <= cluster
                        ||
                        Math.Abs(
                            candleBodyHigh -
                            candidate
                        ) <= cluster
                    )
                    {
                        touches++;
                    }
                }


                if (touches >
                    bestTouches)
                {
                    bestTouches =
                        touches;

                    bestLevel =
                        candidate;
                }
            }


            return new SrLevel
            {
                Price =
                    bestLevel,

                Touches =
                    bestTouches,

                Valid =
                    bestTouches >= 2
            };
        }


        // ============================================================
        // PUBLIC HELPER
        //
        // Allows FastEntryEngine to determine whether an SR rejection
        // is CRITICAL and therefore must NEVER be overridden.
        // ============================================================

        public static bool HasCriticalNoSpace(
            string symbol,
            TradeAction action,
            MqlRates[] rates,
            double atr,
            double currentPrice,
            out double spaceAtr,
            double recentHigh = 0.0,
            double recentLow = 0.0)
        {
            spaceAtr = double.MaxValue;

            if (
                rates == null ||
                rates.Length <
                    Lookback + 2 ||
                atr <= 0
            )
            {
                return false;
            }

            symbol =
                symbol?.ToUpperInvariant()
                ?? "";

            var levels =
                GetLevels(rates);

            var supportShelf =
                GetSupportShelf(
                    symbol,
                    rates,
                    atr
                );

            var resistanceShelf =
                GetResistanceShelf(
                    symbol,
                    rates,
                    atr
                );

            double crossedWallMemory =
                GetCrossedWallMemoryAtr(
                    symbol
                );

            double activeSupport =
                GetNearestRelevantSupport(
                    currentPrice,
                    supportShelf.Price,
                    supportShelf.Valid,
                    levels.lowest,
                    recentLow,
                    atr,
                    crossedWallMemory
                );

            double activeResistance =
                GetNearestRelevantResistance(
                    currentPrice,
                    resistanceShelf.Price,
                    resistanceShelf.Valid,
                    levels.highest,
                    recentHigh,
                    atr,
                    crossedWallMemory
                );

            double criticalSpace =
                GetCriticalSpaceAtr(symbol);

            // --------------------------------------------------------
            // BUY
            // --------------------------------------------------------
            if (action ==
                TradeAction.EnterBuy)
            {
                if (activeResistance > 0)
                {
                    spaceAtr =
                        Math.Abs(
                            activeResistance -
                            currentPrice
                        )
                        /
                        atr;

                    // Whether resistance is just above us OR was only
                    // just crossed, the setup is still too close to
                    // that structural wall to be considered safe.
                    return
                        spaceAtr <
                        criticalSpace;
                }

                return false;
            }

            // --------------------------------------------------------
            // SELL
            // --------------------------------------------------------
            if (action ==
                TradeAction.EnterSell)
            {
                if (activeSupport > 0)
                {
                    spaceAtr =
                        Math.Abs(
                            currentPrice -
                            activeSupport
                        )
                        /
                        atr;

                    // Same protection for support.  A tiny break below
                    // support does NOT make support disappear.
                    return
                        spaceAtr <
                        criticalSpace;
                }

                return false;
            }

            return false;
        }


        // ============================================================
        // MAIN BLOCK LOGIC
        //
        // NOTE:
        // ema9 is now passed in explicitly.
        //
        // Do NOT substitute rates[^1].close for EMA9.
        // ============================================================

        public static bool ShouldBlockTrade(
            string symbol,
            TradeAction action,
            MqlRates[] rates,
            double atr,
            double momentum,
            double currentPrice,
            double ema9,
            double recentHigh = 0.0,
            double recentLow = 0.0)
        {
            if (
                rates == null ||
                rates.Length <
                    Lookback + 2 ||
                atr <= 0
            )
            {
                return false;
            }


            symbol =
                symbol?.ToUpperInvariant()
                ?? "";


            var levels =
                GetLevels(rates);


            double highest =
                levels.highest;

            double lowest =
                levels.lowest;


            var supportShelf =
                GetSupportShelf(
                    symbol,
                    rates,
                    atr
                );


            var resistanceShelf =
                GetResistanceShelf(
                    symbol,
                    rates,
                    atr
                );


            double crossedWallMemory =
                GetCrossedWallMemoryAtr(
                    symbol
                );


            double activeSupport =
                GetNearestRelevantSupport(
                    currentPrice,
                    supportShelf.Price,
                    supportShelf.Valid,
                    lowest,
                    recentLow,
                    atr,
                    crossedWallMemory
                );


            double activeResistance =
                GetNearestRelevantResistance(
                    currentPrice,
                    resistanceShelf.Price,
                    resistanceShelf.Valid,
                    highest,
                    recentHigh,
                    atr,
                    crossedWallMemory
                );


            // ========================================================
            // DIRECTIONAL SPACE + RECENTLY-CROSSED WALL STATE
            // ========================================================

            double distToResistance =
                activeResistance > 0
                    ? Math.Abs(
                        activeResistance -
                        currentPrice
                      )
                      /
                      atr
                    : double.MaxValue;


            double distToSupport =
                activeSupport > 0
                    ? Math.Abs(
                        currentPrice -
                        activeSupport
                      )
                      /
                      atr
                    : double.MaxValue;


            bool resistanceAhead =
                activeResistance > 0 &&
                activeResistance >
                    currentPrice;


            bool supportAhead =
                activeSupport > 0 &&
                activeSupport <
                    currentPrice;


            bool resistanceRecentlyCrossed =
                activeResistance > 0 &&
                activeResistance <
                    currentPrice &&
                distToResistance <=
                    crossedWallMemory;


            bool supportRecentlyCrossed =
                activeSupport > 0 &&
                activeSupport >
                    currentPrice &&
                distToSupport <=
                    crossedWallMemory;


            double minimumSpace =
                GetMinimumSpaceAtr(
                    symbol
                );


            double criticalSpace =
                GetCriticalSpaceAtr(
                    symbol
                );


            // ========================================================
            // REAL EMA9 DISTANCE
            // ========================================================

            double distFromEma =
                ema9 > 0
                    ? Math.Abs(
                        currentPrice -
                        ema9
                      )
                      /
                      atr

                    : 0;


            double maxEmaDistance =
                GetMaxEmaDistanceAtr(
                    symbol
                );


            // ========================================================
            // STRONG BREAKOUT
            // ========================================================

            bool strongMomentum =
                Math.Abs(momentum) >=
                GetMomentumThreshold(
                    symbol
                );


            double breakoutClearance =
                GetBreakoutClearanceAtr(
                    symbol
                );


            // Use the RETAINED wall for breakout confirmation.
            //
            // A breakout is only confirmed when:
            // 1) price has genuinely crossed the wall,
            // 2) it has cleared the wall by a meaningful ATR amount,
            // 3) momentum is strong enough.
            bool confirmedBreakout =
                strongMomentum &&
                (
                    action ==
                    TradeAction.EnterBuy

                        ? resistanceRecentlyCrossed &&
                          currentPrice >=
                              activeResistance +
                              atr *
                              breakoutClearance

                        : supportRecentlyCrossed &&
                          currentPrice <=
                              activeSupport -
                              atr *
                              breakoutClearance
                );


            // ========================================================
            // BUY
            // ========================================================

            if (action ==
                TradeAction.EnterBuy)
            {
                // ----------------------------------------------------
                // HARD NO-SPACE BLOCK
                //
                // Strong impulse CANNOT bypass this.
                // ----------------------------------------------------

                if (
                    resistanceAhead &&
                    distToResistance <
                        criticalSpace
                )
                {
                    LogFile.Write(
                        $"[{symbol}] ⛔ HARD SR BLOCK BUY | " +
                        $"spaceATR={distToResistance:F2} | " +
                        $"critical={criticalSpace:F2} | " +
                        $"resistance={activeResistance:F5} | " +
                        $"price={currentPrice:F5} | " +
                        $"touches={resistanceShelf.Touches}"
                    );

                    return true;
                }


                // ----------------------------------------------------
                // JUST-BROKE-RESISTANCE PROTECTION
                //
                // Do not buy immediately after barely poking through
                // resistance.  Require a real breakout clearance.
                // ----------------------------------------------------

                if (
                    resistanceRecentlyCrossed &&
                    !confirmedBreakout
                )
                {
                    LogFile.Write(
                        $"[{symbol}] ⛔ BUY BLOCKED: resistance only just crossed | " +
                        $"wall={activeResistance:F5} | " +
                        $"price={currentPrice:F5} | " +
                        $"clearanceATR={distToResistance:F2} | " +
                        $"requiredBreakATR={breakoutClearance:F2} | " +
                        $"strongMomentum={strongMomentum}"
                    );

                    return true;
                }


                // ----------------------------------------------------
                // NORMAL SPACE BLOCK
                //
                // A legitimate confirmed breakout may bypass this.
                // ----------------------------------------------------

                if (
                    resistanceAhead &&
                    distToResistance <
                        minimumSpace &&
                    !confirmedBreakout
                )
                {
                    LogFile.Write(
                        $"[{symbol}] ❌ Buy blocked: no space above | " +
                        $"spaceATR={distToResistance:F2} | " +
                        $"required={minimumSpace:F2} | " +
                        $"critical={criticalSpace:F2} | " +
                        $"resistance={activeResistance:F5} | " +
                        $"price={currentPrice:F5}"
                    );

                    return true;
                }


                // ----------------------------------------------------
                // EMA CHASE PROTECTION
                // ----------------------------------------------------

                if (
                    ema9 > 0 &&
                    distFromEma >
                        maxEmaDistance
                )
                {
                    LogFile.Write(
                        $"[{symbol}] ❌ Buy blocked: too far from EMA9 | " +
                        $"distATR={distFromEma:F2} | " +
                        $"max={maxEmaDistance:F2} | " +
                        $"ema9={ema9:F5} | " +
                        $"price={currentPrice:F5}"
                    );

                    return true;
                }


                LogFile.Write(
                    $"[{symbol}] ✅ Buy allowed | " +
                    $"spaceATR={distToResistance:F2} | " +
                    $"resistanceAhead={resistanceAhead} | " +
                    $"activeResistance={activeResistance:F5} | " +
                    $"recentHigh={recentHigh:F5} | " +
                    $"emaDist={distFromEma:F2} | " +
                    $"strongMomentum={strongMomentum} | " +
                    $"resistanceRecentlyCrossed={resistanceRecentlyCrossed} | " +
                    $"confirmedBreakout={confirmedBreakout}"
                );


                return false;
            }


            // ========================================================
            // SELL
            // ========================================================

            if (action ==
                TradeAction.EnterSell)
            {
                // ----------------------------------------------------
                // HARD NO-SPACE BLOCK
                // ----------------------------------------------------

                if (
                    supportAhead &&
                    distToSupport <
                        criticalSpace
                )
                {
                    LogFile.Write(
                        $"[{symbol}] ⛔ HARD SR BLOCK SELL | " +
                        $"spaceATR={distToSupport:F2} | " +
                        $"critical={criticalSpace:F2} | " +
                        $"support={activeSupport:F5} | " +
                        $"price={currentPrice:F5} | " +
                        $"touches={supportShelf.Touches}"
                    );

                    return true;
                }


                // ----------------------------------------------------
                // JUST-BROKE-SUPPORT PROTECTION
                //
                // Do not sell immediately after barely poking through
                // support.  Require a real breakout clearance.
                // ----------------------------------------------------

                if (
                    supportRecentlyCrossed &&
                    !confirmedBreakout
                )
                {
                    LogFile.Write(
                        $"[{symbol}] ⛔ SELL BLOCKED: support only just crossed | " +
                        $"wall={activeSupport:F5} | " +
                        $"price={currentPrice:F5} | " +
                        $"clearanceATR={distToSupport:F2} | " +
                        $"requiredBreakATR={breakoutClearance:F2} | " +
                        $"strongMomentum={strongMomentum}"
                    );

                    return true;
                }


                // ----------------------------------------------------
                // NORMAL SPACE BLOCK
                // ----------------------------------------------------

                if (
                    supportAhead &&
                    distToSupport <
                        minimumSpace &&
                    !confirmedBreakout
                )
                {
                    LogFile.Write(
                        $"[{symbol}] ❌ Sell blocked: no space below | " +
                        $"spaceATR={distToSupport:F2} | " +
                        $"required={minimumSpace:F2} | " +
                        $"critical={criticalSpace:F2} | " +
                        $"support={activeSupport:F5} | " +
                        $"price={currentPrice:F5}"
                    );

                    return true;
                }


                // ----------------------------------------------------
                // EMA CHASE PROTECTION
                // ----------------------------------------------------

                if (
                    ema9 > 0 &&
                    distFromEma >
                        maxEmaDistance
                )
                {
                    LogFile.Write(
                        $"[{symbol}] ❌ Sell blocked: too far from EMA9 | " +
                        $"distATR={distFromEma:F2} | " +
                        $"max={maxEmaDistance:F2} | " +
                        $"ema9={ema9:F5} | " +
                        $"price={currentPrice:F5}"
                    );

                    return true;
                }


                LogFile.Write(
                    $"[{symbol}] ✅ Sell allowed | " +
                    $"spaceATR={distToSupport:F2} | " +
                    $"supportAhead={supportAhead} | " +
                    $"activeSupport={activeSupport:F5} | " +
                    $"recentLow={recentLow:F5} | " +
                    $"emaDist={distFromEma:F2} | " +
                    $"strongMomentum={strongMomentum} | " +
                    $"supportRecentlyCrossed={supportRecentlyCrossed} | " +
                    $"confirmedBreakout={confirmedBreakout}"
                );


                return false;
            }


            return false;
        }
    }
}
