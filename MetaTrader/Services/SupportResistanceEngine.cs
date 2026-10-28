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
        // ============================================================
        // ENTRY STUDY DIAGNOSTICS
        //
        // Instrumentation only. These values are taken from the exact
        // S/R geometry used by ShouldBlockTrade(). They do not alter
        // Gold/Silver entry decisions.
        // ============================================================
        public sealed class SrEntryDiagnostics
        {
            public double Wall { get; set; }
            public double ClearanceAtr { get; set; }
            public bool WallAhead { get; set; }
            public double Ema9DistanceAtr { get; set; }
        }

        public static int Lookback = 12;

        private class SrLevel
        {
            public double Price;
            public int Touches;
            public bool Valid;
        }

        // ============================================================
        // GOLD STRUCTURAL BREAKDOWN WATCH
        //
        // A valid XAU impulse that reaches an SR band is NOT allowed
        // to punch straight through the hard wall.  Instead we remember
        // the setup and wait for price to CLEAR the outer edge.
        //
        // This solves the 9/28 XAUUSD case:
        // valid SELL near 4138 -> support band blocked it -> support
        // later failed -> price accelerated lower.
        // ============================================================

        private class GoldBreakdownWatch
        {
            public TradeAction Action;
            public double Wall;
            public double ZoneLow;
            public double ZoneHigh;
            public double ArmedPrice;
            public DateTime ArmedAtUtc;
        }

        private static readonly object GoldBreakdownWatchLock =
            new object();

        private static readonly System.Collections.Generic.Dictionary<string, GoldBreakdownWatch>
            GoldBreakdownWatches =
                new System.Collections.Generic.Dictionary<string, GoldBreakdownWatch>();

        private const double GoldBreakTriggerClearanceAtr = 0.05;
        // Preserve an already-approved GOLD acceleration impulse when SR blocks it.
        // This arms MEMORY only; it does NOT permit an entry or lower release requirements.
        private const double GoldBreakArmMinMomentum = 0.30;
        private const double GoldBreakMinMomentum = 0.45;

        // Armed Gold SR breaks must obey the same hard anti-chase distance
        // as the final Gold entry-location gate in LiveImpulseEntryEngine.
        // This prevents an SR watch from becoming a back door around that gate.
        private const double GoldBreakMaxEmaDistanceAtr = 1.25;
        private const double GoldBreakCancelEmaDistanceAtr = 1.25;

        private const double GoldBreakCancelReversalAtr = 0.25;
        private const int GoldBreakWatchMinutes = 60;

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
            // GOLD: slightly more permissive normal runway (was 0.40 ATR).
            // Critical 0.25 ATR no-space protection remains unchanged.
            if (IsGold(symbol))
                return 0.30;

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
            // Retain Gold crossed-wall context past its 0.12 ATR band and
            // 0.20 ATR breakout clearance. The prior 0.35 ATR window could
            // drop the wall before a timely breakout was recognized.
            if (IsGold(symbol))
                return 0.60;

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
        // SUPPORT / RESISTANCE BAND HALF-WIDTH
        //
        // A support or resistance level is treated as a ZONE rather
        // than as one exact price.  The value returned here is the
        // half-width of that zone in ATR units.
        //
        // Example:
        //   resistance = 780.55
        //   ATR = 3.48
        //   crypto half-width = 0.15 ATR
        //
        //   zone = resistance +/- 0.52
        //        = 780.03 -> 781.07
        //
        // Price must clear the OUTER EDGE of the zone plus the normal
        // breakout-clearance requirement before the wall is considered
        // genuinely broken.
        // ============================================================

        private static double GetSrZoneHalfWidthAtr(string symbol)
        {
            if (IsGold(symbol))
                return 0.12;

            if (IsCrypto(symbol))
                return 0.15;

            if (IsIndex(symbol))
                return 0.12;

            if (IsFx(symbol))
                return 0.08;

            return 0.10;
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


            double zoneHalfWidthAtr =
                GetSrZoneHalfWidthAtr(symbol);


            double zoneHalfWidthPrice =
                zoneHalfWidthAtr * atr;


            // --------------------------------------------------------
            // BUY
            // --------------------------------------------------------
            if (action ==
                TradeAction.EnterBuy)
            {
                if (activeResistance > 0)
                {
                    double resistanceZoneLow =
                        activeResistance -
                        zoneHalfWidthPrice;

                    double resistanceZoneHigh =
                        activeResistance +
                        zoneHalfWidthPrice;

                    // If price is inside the resistance band, there is
                    // effectively ZERO safe runway.  If price is below
                    // the band, measure to the near edge.  If price is
                    // above the band, measure clearance beyond the far
                    // edge.
                    if (currentPrice >= resistanceZoneLow &&
                        currentPrice <= resistanceZoneHigh)
                    {
                        spaceAtr = 0.0;
                    }
                    else if (currentPrice < resistanceZoneLow)
                    {
                        spaceAtr =
                            (resistanceZoneLow - currentPrice) /
                            atr;
                    }
                    else
                    {
                        spaceAtr =
                            (currentPrice - resistanceZoneHigh) /
                            atr;
                    }

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
                    double supportZoneLow =
                        activeSupport -
                        zoneHalfWidthPrice;

                    double supportZoneHigh =
                        activeSupport +
                        zoneHalfWidthPrice;

                    if (currentPrice >= supportZoneLow &&
                        currentPrice <= supportZoneHigh)
                    {
                        spaceAtr = 0.0;
                    }
                    else if (currentPrice > supportZoneHigh)
                    {
                        spaceAtr =
                            (currentPrice - supportZoneHigh) /
                            atr;
                    }
                    else
                    {
                        spaceAtr =
                            (supportZoneLow - currentPrice) /
                            atr;
                    }

                    return
                        spaceAtr <
                        criticalSpace;
                }

                return false;
            }


            return false;
        }



        // ============================================================
        // ARM GOLD BREAKDOWN WATCH
        // ============================================================

        private static void ArmGoldBreakdownWatch(
            string symbol,
            TradeAction action,
            double wall,
            double zoneLow,
            double zoneHigh,
            double currentPrice)
        {
            if (!IsGold(symbol))
                return;

            symbol =
                symbol?.ToUpperInvariant()
                ?? "";

            lock (GoldBreakdownWatchLock)
            {
                GoldBreakdownWatch existing;

                if (GoldBreakdownWatches.TryGetValue(
                        symbol,
                        out existing))
                {
                    bool stillFresh =
                        DateTime.UtcNow -
                        existing.ArmedAtUtc <=
                        TimeSpan.FromMinutes(
                            GoldBreakWatchMinutes
                        );

                    // Keep the ORIGINAL structural wall while the watch
                    // is still fresh.  Do not let a recalculated shelf
                    // move the goalposts every tick.
                    if (stillFresh &&
                        existing.Action == action)
                    {
                        return;
                    }
                }

                GoldBreakdownWatches[symbol] =
                    new GoldBreakdownWatch
                    {
                        Action = action,
                        Wall = wall,
                        ZoneLow = zoneLow,
                        ZoneHigh = zoneHigh,
                        ArmedPrice = currentPrice,
                        ArmedAtUtc = DateTime.UtcNow
                    };
            }

            LogFile.Write(
                $"[{symbol}] 🟠 GOLD SR BREAK WATCH ARMED | " +
                $"action={action} | " +
                $"wall={wall:F5} | " +
                $"zoneLow={zoneLow:F5} | " +
                $"zoneHigh={zoneHigh:F5} | " +
                $"armedPrice={currentPrice:F5}"
            );
        }


        // ============================================================
        // CHECK ARMED GOLD BREAKDOWN WATCH
        //
        // This is intentionally stricter than simply "price crossed
        // support/resistance".
        //
        // SELL:
        // - price must clear BELOW support-zone low
        // - EMA9 must still be falling
        // - price must remain below EMA9
        // - hybrid momentum must still be meaningful
        // - entry must not already be too far from EMA9
        //
        // BUY is the mirror image.
        // ============================================================

        public static bool TryTriggerArmedGoldBreakdown(
            string symbol,
            double atr,
            double currentPrice,
            double ema9,
            double ema9Previous,
            double momentum,
            out TradeAction action)
        {
            action =
                TradeAction.Hold;

            if (!IsGold(symbol) ||
                atr <= 0 ||
                currentPrice <= 0 ||
                ema9 <= 0 ||
                ema9Previous <= 0)
            {
                return false;
            }

            symbol =
                symbol?.ToUpperInvariant()
                ?? "";

            GoldBreakdownWatch watch;

            lock (GoldBreakdownWatchLock)
            {
                if (!GoldBreakdownWatches.TryGetValue(
                        symbol,
                        out watch))
                {
                    return false;
                }

                if (DateTime.UtcNow -
                    watch.ArmedAtUtc >
                    TimeSpan.FromMinutes(
                        GoldBreakWatchMinutes
                    ))
                {
                    GoldBreakdownWatches.Remove(
                        symbol
                    );

                    return false;
                }
            }

            double emaDistanceAtr =
                Math.Abs(
                    currentPrice -
                    ema9
                ) / atr;

            double triggerClearance =
                GoldBreakTriggerClearanceAtr *
                atr;


            // --------------------------------------------------------
            // SELL BREAKDOWN
            // --------------------------------------------------------
            if (watch.Action ==
                TradeAction.EnterSell)
            {
                bool clearedBand =
                    currentPrice <=
                    watch.ZoneLow -
                    triggerClearance;

                bool emaStillBearish =
                    ema9 <
                    ema9Previous &&
                    currentPrice <
                    ema9;

                bool momentumOk =
                    Math.Abs(momentum) >=
                    GoldBreakMinMomentum;

                bool notTooLate =
                    emaDistanceAtr <=
                    GoldBreakMaxEmaDistanceAtr;

                // Cancel if price has run too far before confirmation.
                if (clearedBand &&
                    emaDistanceAtr >
                    GoldBreakCancelEmaDistanceAtr)
                {
                    ClearGoldBreakdownWatch(
                        symbol
                    );

                    LogFile.Write(
                        $"[{symbol}] ❌ GOLD BREAK WATCH CANCELLED - TOO EXTENDED | " +
                        $"action=EnterSell | " +
                        $"price={currentPrice:F5} | " +
                        $"emaDistATR={emaDistanceAtr:F2} | " +
                        $"cancelAbove={GoldBreakCancelEmaDistanceAtr:F2}"
                    );

                    return false;
                }

                // Cancel if price rejects the level and moves materially
                // back above the opposite edge of the band.
                if (currentPrice >=
                    watch.ZoneHigh +
                    atr *
                    GoldBreakCancelReversalAtr)
                {
                    ClearGoldBreakdownWatch(
                        symbol
                    );

                    LogFile.Write(
                        $"[{symbol}] ❌ GOLD BREAK WATCH CANCELLED - SUPPORT HELD | " +
                        $"action=EnterSell | " +
                        $"price={currentPrice:F5} | " +
                        $"zoneHigh={watch.ZoneHigh:F5}"
                    );

                    return false;
                }

                if (clearedBand &&
                    emaStillBearish &&
                    momentumOk &&
                    notTooLate)
                {
                    action =
                        TradeAction.EnterSell;

                    LogFile.Write(
                        $"[{symbol}] 🔥 GOLD SUPPORT BREAK CONFIRMED | " +
                        $"action={action} | " +
                        $"wall={watch.Wall:F5} | " +
                        $"zoneLow={watch.ZoneLow:F5} | " +
                        $"price={currentPrice:F5} | " +
                        $"clearanceATR={(watch.ZoneLow - currentPrice) / atr:F2} | " +
                        $"emaDistATR={emaDistanceAtr:F2} | " +
                        $"momentum={momentum:F2}"
                    );

                    return true;
                }

                return false;
            }


            // --------------------------------------------------------
            // BUY BREAKOUT
            // --------------------------------------------------------
            if (watch.Action ==
                TradeAction.EnterBuy)
            {
                bool clearedBand =
                    currentPrice >=
                    watch.ZoneHigh +
                    triggerClearance;

                bool emaStillBullish =
                    ema9 >
                    ema9Previous &&
                    currentPrice >
                    ema9;

                bool momentumOk =
                    Math.Abs(momentum) >=
                    GoldBreakMinMomentum;

                bool notTooLate =
                    emaDistanceAtr <=
                    GoldBreakMaxEmaDistanceAtr;

                if (clearedBand &&
                    emaDistanceAtr >
                    GoldBreakCancelEmaDistanceAtr)
                {
                    ClearGoldBreakdownWatch(
                        symbol
                    );

                    LogFile.Write(
                        $"[{symbol}] ❌ GOLD BREAK WATCH CANCELLED - TOO EXTENDED | " +
                        $"action=EnterBuy | " +
                        $"price={currentPrice:F5} | " +
                        $"emaDistATR={emaDistanceAtr:F2} | " +
                        $"cancelAbove={GoldBreakCancelEmaDistanceAtr:F2}"
                    );

                    return false;
                }

                if (currentPrice <=
                    watch.ZoneLow -
                    atr *
                    GoldBreakCancelReversalAtr)
                {
                    ClearGoldBreakdownWatch(
                        symbol
                    );

                    LogFile.Write(
                        $"[{symbol}] ❌ GOLD BREAK WATCH CANCELLED - RESISTANCE HELD | " +
                        $"action=EnterBuy | " +
                        $"price={currentPrice:F5} | " +
                        $"zoneLow={watch.ZoneLow:F5}"
                    );

                    return false;
                }

                if (clearedBand &&
                    emaStillBullish &&
                    momentumOk &&
                    notTooLate)
                {
                    action =
                        TradeAction.EnterBuy;

                    LogFile.Write(
                        $"[{symbol}] 🔥 GOLD RESISTANCE BREAK CONFIRMED | " +
                        $"action={action} | " +
                        $"wall={watch.Wall:F5} | " +
                        $"zoneHigh={watch.ZoneHigh:F5} | " +
                        $"price={currentPrice:F5} | " +
                        $"clearanceATR={(currentPrice - watch.ZoneHigh) / atr:F2} | " +
                        $"emaDistATR={emaDistanceAtr:F2} | " +
                        $"momentum={momentum:F2}"
                    );

                    return true;
                }
            }

            return false;
        }


        // FINAL-GUARD HANDOFF: read-only confirmation of the ORIGINAL armed wall.
        // Never manufacture approval from an entry-model name or a single tick.
        // This preserves the existing armed-watch clearance, direction, momentum,
        // freshness and EMA-location requirements. No watch is consumed here.
        public static bool TryVerifyArmedGoldBreakForFinalGuard(
            string symbol, TradeAction expectedAction, double atr,
            double executablePrice, double ema9, double previousEma9,
            double momentum, out double verifiedWall)
        {
            verifiedWall = 0.0;
            if (!IsGold(symbol) || atr <= 0 || executablePrice <= 0 ||
                ema9 <= 0 || previousEma9 <= 0 ||
                double.IsNaN(momentum) || double.IsInfinity(momentum))
                return false;

            GoldBreakdownWatch watch;
            lock (GoldBreakdownWatchLock)
            {
                if (!GoldBreakdownWatches.TryGetValue((symbol ?? "").ToUpperInvariant(), out watch) ||
                    watch.Action != expectedAction ||
                    DateTime.UtcNow - watch.ArmedAtUtc > TimeSpan.FromMinutes(GoldBreakWatchMinutes))
                    return false;
            }

            double distance = Math.Abs(executablePrice - ema9) / atr;
            if (distance > GoldBreakMaxEmaDistanceAtr ||
                Math.Abs(momentum) < GoldBreakMinMomentum)
                return false;

            bool buy = expectedAction == TradeAction.EnterBuy;
            bool sell = expectedAction == TradeAction.EnterSell;
            double clearance = GoldBreakTriggerClearanceAtr * atr;
            bool valid = (buy && executablePrice >= watch.ZoneHigh + clearance &&
                          ema9 > previousEma9 && executablePrice > ema9) ||
                         (sell && executablePrice <= watch.ZoneLow - clearance &&
                          ema9 < previousEma9 && executablePrice < ema9);
            if (!valid) return false;
            verifiedWall = watch.Wall;
            return true;
        }

        // ============================================================
        // CLEAR GOLD BREAKDOWN WATCH
        // ============================================================

        public static void ClearGoldBreakdownWatch(
            string symbol)
        {
            if (string.IsNullOrWhiteSpace(
                    symbol))
            {
                return;
            }

            symbol =
                symbol.ToUpperInvariant();

            lock (GoldBreakdownWatchLock)
            {
                GoldBreakdownWatches.Remove(
                    symbol
                );
            }
        }


        // ============================================================
        // MAIN BLOCK LOGIC
        //
        // NOTE:
        // ema9 is now passed in explicitly.
        //
        // Do NOT substitute rates[^1].close for EMA9.
        // ============================================================

        // Compatibility overload: existing Gold callers continue to compile
        // and behave exactly as before.
        public static bool ShouldBlockTrade(
            string symbol,
            TradeAction action,
            MqlRates[] rates,
            double atr,
            double momentum,
            double currentPrice,
            double ema9,
            double recentHigh = 0.0,
            double recentLow = 0.0,
            bool isImpulseEntry = false,
            bool isArmedBreakdownTrigger = false)
        {
            SrEntryDiagnostics ignored;

            return ShouldBlockTrade(
                symbol,
                action,
                rates,
                atr,
                momentum,
                currentPrice,
                ema9,
                out ignored,
                recentHigh,
                recentLow,
                isImpulseEntry,
                isArmedBreakdownTrigger
            );
        }

        // Diagnostic overload used by FastEntryEngine/Trade Study.
        // The trading decision is still produced by the same logic below.
        public static bool ShouldBlockTrade(
            string symbol,
            TradeAction action,
            MqlRates[] rates,
            double atr,
            double momentum,
            double currentPrice,
            double ema9,
            out SrEntryDiagnostics diagnostics,
            double recentHigh = 0.0,
            double recentLow = 0.0,
            bool isImpulseEntry = false,
            bool isArmedBreakdownTrigger = false)
        {
            diagnostics = new SrEntryDiagnostics();

            if (
                rates == null ||
                rates.Length < Lookback + 2 ||
                atr <= 0)
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
            // SR BAND GEOMETRY
            // ========================================================

            double zoneHalfWidthAtr =
                GetSrZoneHalfWidthAtr(symbol);


            double zoneHalfWidthPrice =
                zoneHalfWidthAtr * atr;


            double resistanceZoneLow =
                activeResistance > 0
                    ? activeResistance - zoneHalfWidthPrice
                    : 0.0;


            double resistanceZoneHigh =
                activeResistance > 0
                    ? activeResistance + zoneHalfWidthPrice
                    : 0.0;


            double supportZoneLow =
                activeSupport > 0
                    ? activeSupport - zoneHalfWidthPrice
                    : 0.0;


            double supportZoneHigh =
                activeSupport > 0
                    ? activeSupport + zoneHalfWidthPrice
                    : 0.0;


            // ========================================================
            // DIRECTIONAL SPACE + RECENTLY-CROSSED BAND STATE
            //
            // IMPORTANT:
            // Space is measured to the NEAREST EDGE of the zone, not
            // to the old single center-line.  A price inside the zone
            // has zero runway.
            // ========================================================

            bool priceInsideResistanceBand =
                activeResistance > 0 &&
                currentPrice >= resistanceZoneLow &&
                currentPrice <= resistanceZoneHigh;


            bool priceInsideSupportBand =
                activeSupport > 0 &&
                currentPrice >= supportZoneLow &&
                currentPrice <= supportZoneHigh;


            bool resistanceAhead =
                activeResistance > 0 &&
                currentPrice < resistanceZoneLow;


            bool supportAhead =
                activeSupport > 0 &&
                currentPrice > supportZoneHigh;


            double distToResistance =
                activeResistance <= 0
                    ? double.MaxValue
                    : priceInsideResistanceBand
                        ? 0.0
                        : currentPrice < resistanceZoneLow
                            ? (resistanceZoneLow - currentPrice) / atr
                            : (currentPrice - resistanceZoneHigh) / atr;


            double distToSupport =
                activeSupport <= 0
                    ? double.MaxValue
                    : priceInsideSupportBand
                        ? 0.0
                        : currentPrice > supportZoneHigh
                            ? (currentPrice - supportZoneHigh) / atr
                            : (supportZoneLow - currentPrice) / atr;


            // A wall is only considered crossed AFTER price clears the
            // outer edge of its band.
            bool resistanceRecentlyCrossed =
                activeResistance > 0 &&
                currentPrice > resistanceZoneHigh &&
                distToResistance <=
                    crossedWallMemory;


            bool supportRecentlyCrossed =
                activeSupport > 0 &&
                currentPrice < supportZoneLow &&
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


            // Freeze the exact directional S/R context used by this decision.
            // BUY  -> resistance wall / clearance to resistance zone.
            // SELL -> support wall / clearance to support zone.
            if (action == TradeAction.EnterBuy)
            {
                diagnostics.Wall = activeResistance;
                diagnostics.ClearanceAtr =
                    activeResistance > 0 ? distToResistance : 0.0;
                diagnostics.WallAhead = resistanceAhead;
            }
            else if (action == TradeAction.EnterSell)
            {
                diagnostics.Wall = activeSupport;
                diagnostics.ClearanceAtr =
                    activeSupport > 0 ? distToSupport : 0.0;
                diagnostics.WallAhead = supportAhead;
            }

            diagnostics.Ema9DistanceAtr = distFromEma;


            double maxEmaDistance =
                GetMaxEmaDistanceAtr(
                    symbol
                );

            // An armed GOLD structural break is allowed a little more
            // room than a normal fresh impulse.  This is NOT a global
            // relaxation: it only applies after a previously-valid gold
            // impulse was hard-blocked by the exact SR band that has now
            // failed.
            double effectiveMaxEmaDistance =
                isArmedBreakdownTrigger &&
                IsGold(symbol)
                    ? GoldBreakMaxEmaDistanceAtr
                    : maxEmaDistance;


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


            // ========================================================
            // STRUCTURAL BREAKOUT CLEARANCE
            //
            // IMPORTANT:
            //
            // This answers ONLY whether price has genuinely cleared
            // the retained support/resistance wall by the required ATR
            // distance.  It intentionally does NOT require strong
            // momentum.
            //
            // This prevents a valid break from being labelled
            // "only just crossed" simply because hybrid momentum has
            // not reached the stronger SR override threshold.
            // ========================================================

            bool breakoutCleared =
                action ==
                TradeAction.EnterBuy

                    ? resistanceRecentlyCrossed &&
                      currentPrice >=
                          resistanceZoneHigh +
                          atr *
                          breakoutClearance

                    : supportRecentlyCrossed &&
                      currentPrice <=
                          supportZoneLow -
                          atr *
                          breakoutClearance;


            // ========================================================
            // CONFIRMED STRONG BREAKOUT
            //
            // Keep strong momentum as a separate concept.
            //
            // This remains useful when a trade must override another
            // SR restriction such as limited runway ahead.
            // ========================================================

            // For Gold, geometric clearance of the remembered wall is the
            // breakout confirmation; the originating impulse engine already
            // assessed momentum. Do not introduce a second 1.35 veto.
            // Other assets retain the original momentum-backed definition.
            bool confirmedBreakout =
                breakoutCleared &&
                (IsGold(symbol) || strongMomentum);


            // GOLD ONLY: a normal fresh impulse still requires the existing
            // structural-clearance AND strong-momentum confirmation here.
            // However, an armed Gold breakout has ALREADY passed the separate
            // TryTriggerArmedGoldBreakdown() verification (zone-edge clearance,
            // directional EMA, >= 0.45 momentum and <= 1.10 ATR EMA distance).
            // Do not reapply the unrelated 1.35 momentum threshold to that
            // already-confirmed armed trigger. All subsequent SR/next-wall and
            // EMA-distance checks remain in effect.
            if (IsGold(symbol) &&
                ((action == TradeAction.EnterBuy && resistanceRecentlyCrossed) ||
                 (action == TradeAction.EnterSell && supportRecentlyCrossed)) &&
                !confirmedBreakout &&
                !isArmedBreakdownTrigger)
            {
                LogFile.Write($"[{symbol}] GOLD SR UNCONFIRMED CROSS BLOCK | " +
                    $"action={action} | price={currentPrice:F5} | " +
                    $"wall={(action == TradeAction.EnterBuy ? activeResistance : activeSupport):F5} | " +
                    $"breakoutCleared={breakoutCleared} | strongMomentum={strongMomentum} | " +
                    $"confirmedBreakout={confirmedBreakout}");
                return true;
            }

            // GOLD ONLY: check the NEXT untouched overhead/underlying H1
            // structure, not just the nearest crossed wall. Multiple local
            // highs/lows may be inside the last 12 completed H1 candles.
            if (IsGold(symbol) && rates != null && rates.Length >= Lookback + 2)
            {
                double nearestNextWall = 0.0;
                double nearestNextRoom = double.MaxValue;
                for (int i = 2; i < Lookback + 2 && i < rates.Length; i++)
                {
                    double level = action == TradeAction.EnterBuy ? rates[^i].high : rates[^i].low;
                    double room = action == TradeAction.EnterBuy
                        ? (level - zoneHalfWidthPrice - currentPrice) / atr
                        : (currentPrice - (level + zoneHalfWidthPrice)) / atr;
                    // Only unbroken walls in the direction of the trade.
                    if (room >= 0 && room < nearestNextRoom)
                    {
                        nearestNextRoom = room;
                        nearestNextWall = level;
                    }
                }
                if (nearestNextWall > 0 && nearestNextRoom < minimumSpace)
                {
                    LogFile.Write($"[{symbol}] GOLD NEXT SR WALL BLOCK | " +
                        $"action={action} | nextWall={nearestNextWall:F5} | " +
                        $"runwayATR={nearestNextRoom:F2} | requiredATR={minimumSpace:F2}");

                    // A genuine impulse that is blocked by the NEXT untouched H1
                    // wall must not be forgotten. Retain this exact wall and allow
                    // TryTriggerArmedGoldBreakdown() to check a later real break.
                    // This DOES NOT authorize an entry into the wall.
                    if (isImpulseEntry &&
                        (action == TradeAction.EnterBuy || action == TradeAction.EnterSell) &&
                        Math.Abs(momentum) >= GoldBreakArmMinMomentum &&
                        distFromEma <= GoldBreakMaxEmaDistanceAtr)
                    {
                        ArmGoldBreakdownWatch(
                            symbol,
                            action,
                            nearestNextWall,
                            nearestNextWall - zoneHalfWidthPrice,
                            nearestNextWall + zoneHalfWidthPrice,
                            currentPrice);
                    }

                    return true;
                }
            }

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
                    (resistanceAhead ||
                     priceInsideResistanceBand) &&
                    distToResistance <
                        criticalSpace
                )
                {
                    LogFile.Write(
                        $"[{symbol}] ⛔ HARD SR BLOCK BUY | " +
                        $"spaceATR={(activeResistance > 0 ? distToResistance.ToString("F2") : "NO_WALL")} | " +
                        $"critical={criticalSpace:F2} | " +
                        $"resistance={activeResistance:F5} | " +
                        $"zoneLow={resistanceZoneLow:F5} | " +
                        $"zoneHigh={resistanceZoneHigh:F5} | " +
                        $"insideBand={priceInsideResistanceBand} | " +
                        $"price={currentPrice:F5} | " +
                        $"touches={resistanceShelf.Touches}"
                    );

                    // Retain the originally blocked wall even when price is
                    // just AHEAD of its band (not only already inside it).
                    // The current entry stays blocked until a separate, confirmed
                    // structural-break trigger qualifies on a later evaluation.
                    if (IsGold(symbol) &&
                        isImpulseEntry &&
                        Math.Abs(momentum) >= GoldBreakArmMinMomentum &&
                        distFromEma <= GoldBreakMaxEmaDistanceAtr &&
                        (priceInsideResistanceBand || resistanceAhead))
                    {
                        ArmGoldBreakdownWatch(
                            symbol,
                            TradeAction.EnterBuy,
                            activeResistance,
                            resistanceZoneLow,
                            resistanceZoneHigh,
                            currentPrice
                        );
                    }

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
                    !breakoutCleared &&
                    !isArmedBreakdownTrigger
                )
                {
                    LogFile.Write(
                        $"[{symbol}] ⛔ BUY BLOCKED: resistance only just crossed | " +
                        $"wall={activeResistance:F5} | " +
                        $"zoneHigh={resistanceZoneHigh:F5} | " +
                        $"price={currentPrice:F5} | " +
                        $"clearanceBeyondBandATR={distToResistance:F2} | " +
                        $"requiredBreakATR={breakoutClearance:F2} | " +
                        $"breakoutCleared={breakoutCleared} | " +
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
                    (resistanceAhead ||
                     priceInsideResistanceBand) &&
                    distToResistance <
                        minimumSpace &&
                    !confirmedBreakout
                )
                {
                    LogFile.Write(
                        $"[{symbol}] ❌ Buy blocked: no space above | " +
                        $"spaceATR={(activeResistance > 0 ? distToResistance.ToString("F2") : "NO_WALL")} | " +
                        $"required={minimumSpace:F2} | " +
                        $"critical={criticalSpace:F2} | " +
                        $"resistance={activeResistance:F5} | " +
                        $"zoneLow={resistanceZoneLow:F5} | " +
                        $"zoneHigh={resistanceZoneHigh:F5} | " +
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
                        effectiveMaxEmaDistance
                )
                {
                    LogFile.Write(
                        $"[{symbol}] ❌ Buy blocked: too far from EMA9 | " +
                        $"distATR={distFromEma:F2} | " +
                        $"max={effectiveMaxEmaDistance:F2} | " +
                        $"ema9={ema9:F5} | " +
                        $"price={currentPrice:F5}"
                    );

                    return true;
                }


                LogFile.Write(
                    $"[{symbol}] ✅ Buy allowed | " +
                    $"spaceATR={(activeResistance > 0 ? distToResistance.ToString("F2") : "NO_WALL")} | " +
                    $"resistanceAhead={resistanceAhead} | " +
                    $"activeResistance={activeResistance:F5} | " +
                    $"resistanceZoneLow={resistanceZoneLow:F5} | " +
                    $"resistanceZoneHigh={resistanceZoneHigh:F5} | " +
                    $"insideBand={priceInsideResistanceBand} | " +
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
                    (supportAhead ||
                     priceInsideSupportBand) &&
                    distToSupport <
                        criticalSpace
                )
                {
                    LogFile.Write(
                        $"[{symbol}] ⛔ HARD SR BLOCK SELL | " +
                        $"spaceATR={(activeSupport > 0 ? distToSupport.ToString("F2") : "NO_WALL")} | " +
                        $"critical={criticalSpace:F2} | " +
                        $"support={activeSupport:F5} | " +
                        $"zoneLow={supportZoneLow:F5} | " +
                        $"zoneHigh={supportZoneHigh:F5} | " +
                        $"insideBand={priceInsideSupportBand} | " +
                        $"price={currentPrice:F5} | " +
                        $"touches={supportShelf.Touches}"
                    );

                    // Retain the originally blocked wall even when price is
                    // just AHEAD of its band (not only already inside it).
                    // The current entry stays blocked until a separate, confirmed
                    // structural-break trigger qualifies on a later evaluation.
                    if (IsGold(symbol) &&
                        isImpulseEntry &&
                        Math.Abs(momentum) >= GoldBreakArmMinMomentum &&
                        distFromEma <= GoldBreakMaxEmaDistanceAtr &&
                        (priceInsideSupportBand || supportAhead))
                    {
                        ArmGoldBreakdownWatch(
                            symbol,
                            TradeAction.EnterSell,
                            activeSupport,
                            supportZoneLow,
                            supportZoneHigh,
                            currentPrice
                        );
                    }

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
                    !breakoutCleared &&
                    !isArmedBreakdownTrigger
                )
                {
                    LogFile.Write(
                        $"[{symbol}] ⛔ SELL BLOCKED: support only just crossed | " +
                        $"wall={activeSupport:F5} | " +
                        $"zoneLow={supportZoneLow:F5} | " +
                        $"price={currentPrice:F5} | " +
                        $"clearanceBeyondBandATR={distToSupport:F2} | " +
                        $"requiredBreakATR={breakoutClearance:F2} | " +
                        $"breakoutCleared={breakoutCleared} | " +
                        $"strongMomentum={strongMomentum}"
                    );

                    return true;
                }


                // ----------------------------------------------------
                // NORMAL SPACE BLOCK
                // ----------------------------------------------------

                if (
                    (supportAhead ||
                     priceInsideSupportBand) &&
                    distToSupport <
                        minimumSpace &&
                    !confirmedBreakout
                )
                {
                    LogFile.Write(
                        $"[{symbol}] ❌ Sell blocked: no space below | " +
                        $"spaceATR={(activeSupport > 0 ? distToSupport.ToString("F2") : "NO_WALL")} | " +
                        $"required={minimumSpace:F2} | " +
                        $"critical={criticalSpace:F2} | " +
                        $"support={activeSupport:F5} | " +
                        $"zoneLow={supportZoneLow:F5} | " +
                        $"zoneHigh={supportZoneHigh:F5} | " +
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
                        effectiveMaxEmaDistance
                )
                {
                    LogFile.Write(
                        $"[{symbol}] ❌ Sell blocked: too far from EMA9 | " +
                        $"distATR={distFromEma:F2} | " +
                        $"max={effectiveMaxEmaDistance:F2} | " +
                        $"ema9={ema9:F5} | " +
                        $"price={currentPrice:F5}"
                    );

                    return true;
                }


                LogFile.Write(
                    $"[{symbol}] ✅ Sell allowed | " +
                    $"spaceATR={(activeSupport > 0 ? distToSupport.ToString("F2") : "NO_WALL")} | " +
                    $"supportAhead={supportAhead} | " +
                    $"activeSupport={activeSupport:F5} | " +
                    $"supportZoneLow={supportZoneLow:F5} | " +
                    $"supportZoneHigh={supportZoneHigh:F5} | " +
                    $"insideBand={priceInsideSupportBand} | " +
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
