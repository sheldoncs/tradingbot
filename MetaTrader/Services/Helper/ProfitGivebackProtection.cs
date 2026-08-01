using MetaTrader.Data.Entities;
using MetaTrader.Util;
using System;

namespace MetaTrader.Services.Helper
{
    public static class ProfitGivebackProtection
    {
        public static bool ShouldClose(
            TrackedTrade t)
        {
            if (t == null)
                return false;

            // --------------------------------------------------
            // 🔥 1. TRACK PEAK PROFIT
            // --------------------------------------------------
            t.MaxProfitUsd = Math.Max(t.MaxProfitUsd, t.ProfitUsd);

            var profile = GetProfile(t.Symbol);

            // --------------------------------------------------
            // 🧠 2. ONLY ACTIVATE AFTER REAL PROFIT
            // --------------------------------------------------
            if (t.MaxProfitUsd < profile.ActivationProfitUsd)
                return false;

            // --------------------------------------------------
            // 🔥 3. CALCULATE FLOOR
            // --------------------------------------------------
            double profitFloor = t.MaxProfitUsd - profile.AllowedGivebackUsd;

            // --------------------------------------------------
            // 🛑 4. EXIT CONDITION
            // --------------------------------------------------
            if (t.ProfitUsd <= profitFloor)
            {
                LogFile.Write(
                    $"[{t.Symbol}] 🛑 PROFIT GIVEBACK EXIT | " +
                    $"ticket={t.Ticket}, side={t.Side}, engine={t.Model}, " +
                    $"profit={t.ProfitUsd:F2}, maxProfit={t.MaxProfitUsd:F2}, " +
                    $"allowedGiveback={profile.AllowedGivebackUsd:F2}, floor={profitFloor:F2}"
                );

                return true;
            }

            return false;
        }

        private class Profile
        {
            public double ActivationProfitUsd { get; set; }
            public double AllowedGivebackUsd { get; set; }
        }

        private static Profile GetProfile(string symbol)
        {
            string s = symbol?.ToUpperInvariant() ?? "";

            // --------------------------------------------------
            // 🟡 GOLD (slightly smarter)
            // --------------------------------------------------
            if (s.Contains("XAU") || s.Contains("XAG"))
            {
                return new Profile
                {
                    ActivationProfitUsd = 8.00,
                    AllowedGivebackUsd = 5.00
                };
            }

            // --------------------------------------------------
            // 🟣 MAJOR CRYPTO
            // --------------------------------------------------
            if (s.Contains("ETH") || s.Contains("BTC"))
            {
                return new Profile
                {
                    ActivationProfitUsd = 7.00,
                    AllowedGivebackUsd = 3.50
                };
            }

            // --------------------------------------------------
            // 🔵 MID CRYPTO
            // --------------------------------------------------
            if (s.Contains("SOL") || s.Contains("BNB"))
            {
                return new Profile
                {
                    ActivationProfitUsd = 6.00,
                    AllowedGivebackUsd = 3.00
                };
            }

            // --------------------------------------------------
            // 🟢 SMALL CRYPTO
            // --------------------------------------------------
            if (s.Contains("XRP") || s.Contains("ADA"))
            {
                return new Profile
                {
                    ActivationProfitUsd = 5.00,
                    AllowedGivebackUsd = 2.50
                };
            }

            // --------------------------------------------------
            // 📊 INDICES
            // --------------------------------------------------
            if (s.Contains("US100") || s.Contains("NAS"))
            {
                return new Profile
                {
                    ActivationProfitUsd = 7.00,
                    AllowedGivebackUsd = 3.50
                };
            }

            // --------------------------------------------------
            // 💱 FX
            // --------------------------------------------------
            if (s.Contains("EURUSD") ||
                s.Contains("GBPUSD") ||
                s.Contains("USDJPY") ||
                s.Contains("EURJPY") ||
                s.Contains("USDCAD") ||
                s.Contains("AUDUSD"))
            {
                return new Profile
                {
                    ActivationProfitUsd = 4.00,
                    AllowedGivebackUsd = 2.00
                };
            }

            // --------------------------------------------------
            // DEFAULT
            // --------------------------------------------------
            return new Profile
            {
                ActivationProfitUsd = 5.00,
                AllowedGivebackUsd = 2.50
            };
        }
    }
}
