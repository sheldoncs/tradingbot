using MetaTrader.Data.Entities;
using MetaTrader.Util;
using System;

namespace MetaTrader.Services.Helper
{
    public static class AtrTrailingStopEngine
    {
        private class Profile
        {
            public double ActivationAtr;
            public double BreakEvenAtr;
            public double ProfitLockAtr;
            public double Stage1TrailAtr;
            public double Stage2TrailAtr;
            public double Stage3TrailAtr;
        }

        public static bool Apply(
            TrackedTrade t,
            double mid,
            double atr,
            Action<TrackedTrade> modifyTrade)
        {
            if (t == null || atr <= 0 || modifyTrade == null)
                return false;

            var profile = GetProfile(t.Symbol);

            double favorableMove =
                t.Side == TradeSide.Sell
                    ? t.EntryPrice - mid
                    : mid - t.EntryPrice;

            double moveAtr = favorableMove / atr;

            if (moveAtr < profile.ActivationAtr)
            {
                LogFile.Write(
                    $"⏳ ATR TRAIL HOLD | {t.Symbol} | ticket={t.Ticket} | " +
                    $"moveATR={moveAtr:F2}, need={profile.ActivationAtr:F2}"
                );
                return false;
            }

            double trailAtr =
                moveAtr < 1.20 ? profile.Stage1TrailAtr :
                moveAtr < 2.00 ? profile.Stage2TrailAtr :
                                  profile.Stage3TrailAtr;

            double trail = atr * trailAtr;
            double profitLock = atr * profile.ProfitLockAtr;

            if (t.Side == TradeSide.Buy)
            {
                double rawSL = mid - trail;

                double minSL =
                    moveAtr >= profile.BreakEvenAtr
                        ? t.EntryPrice + profitLock
                        : t.StopLoss;

                double newSL = Math.Max(rawSL, minSL);

                if (newSL > t.StopLoss)
                {
                    LogFile.Write(
                        $"🔁 ATR TRAIL BUY | {t.Symbol} | ticket={t.Ticket} | " +
                        $"oldSL={t.StopLoss:F5} → newSL={newSL:F5} | " +
                        $"mid={mid:F5}, moveATR={moveAtr:F2}, trailATR={trailAtr:F2}"
                    );

                    t.StopLoss = newSL;
                    modifyTrade(t);
                    return true;
                }
            }
            else
            {
                double rawSL = mid + trail;

                double maxSL =
                    moveAtr >= profile.BreakEvenAtr
                        ? t.EntryPrice - profitLock
                        : t.StopLoss;

                double newSL = Math.Min(rawSL, maxSL);

                if (newSL < t.StopLoss)
                {
                    LogFile.Write(
                        $"🔁 ATR TRAIL SELL | {t.Symbol} | ticket={t.Ticket} | " +
                        $"oldSL={t.StopLoss:F5} → newSL={newSL:F5} | " +
                        $"mid={mid:F5}, moveATR={moveAtr:F2}, trailATR={trailAtr:F2}"
                    );

                    t.StopLoss = newSL;
                    modifyTrade(t);
                    return true;
                }
            }

            return false;
        }

        private static Profile GetProfile(string symbol)
        {
            string s = symbol?.ToUpperInvariant() ?? "";

            if (IsGold(s))
            {
                return new Profile
                {
                    ActivationAtr = 0.55,
                    BreakEvenAtr = 0.80,
                    ProfitLockAtr = 0.08,
                    Stage1TrailAtr = 0.70,
                    Stage2TrailAtr = 0.95,
                    Stage3TrailAtr = 1.20
                };
            }

            if (IsCrypto(s))
            {
                return new Profile
                {
                    ActivationAtr = 0.65,
                    BreakEvenAtr = 0.90,
                    ProfitLockAtr = 0.08,
                    Stage1TrailAtr = 0.85,
                    Stage2TrailAtr = 1.10,
                    Stage3TrailAtr = 1.35
                };
            }

            if (IsIndex(s))
            {
                return new Profile
                {
                    ActivationAtr = 0.60,
                    BreakEvenAtr = 0.85,
                    ProfitLockAtr = 0.08,
                    Stage1TrailAtr = 0.80,
                    Stage2TrailAtr = 1.05,
                    Stage3TrailAtr = 1.30
                };
            }

            // FX default
            return new Profile
            {
                ActivationAtr = 0.50,
                BreakEvenAtr = 0.75,
                ProfitLockAtr = 0.06,
                Stage1TrailAtr = 0.70,
                Stage2TrailAtr = 0.95,
                Stage3TrailAtr = 1.15
            };
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
                   symbol.Contains("DOGE") ||
                   symbol.Contains("LTC");
        }

        private static bool IsIndex(string symbol)
        {
            return symbol.Contains("US100") ||
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
