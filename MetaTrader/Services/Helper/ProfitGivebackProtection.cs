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

            // Track peak profit
            t.MaxProfitUsd = Math.Max(t.MaxProfitUsd, t.ProfitUsd);

            var profile = GetProfile(t.Symbol);

            // Do not activate until trade has reached minimum profit
            if (t.MaxProfitUsd < profile.ActivationProfitUsd)
                return false;

            double profitFloor = t.MaxProfitUsd - profile.AllowedGivebackUsd;

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

            if (s.Contains("XAUUSD") || s.Contains("XAUEUR"))
            {
                return new Profile
                {
                    ActivationProfitUsd = 8.00,
                    AllowedGivebackUsd = 4.00
                };
            }

            if (s.Contains("ETHUSD") || s.Contains("BTCUSD"))
            {
                return new Profile
                {
                    ActivationProfitUsd = 7.00,
                    AllowedGivebackUsd = 3.50
                };
            }

            if (s.Contains("SOLUSD") || s.Contains("BNBUSD"))
            {
                return new Profile
                {
                    ActivationProfitUsd = 6.00,
                    AllowedGivebackUsd = 3.00
                };
            }

            if (s.Contains("XRPUSD") || s.Contains("ADAUSD"))
            {
                return new Profile
                {
                    ActivationProfitUsd = 5.00,
                    AllowedGivebackUsd = 2.50
                };
            }

            if (s.Contains("US100") || s.Contains("NAS"))
            {
                return new Profile
                {
                    ActivationProfitUsd = 7.00,
                    AllowedGivebackUsd = 3.50
                };
            }

            // FX
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

            return new Profile
            {
                ActivationProfitUsd = 5.00,
                AllowedGivebackUsd = 2.50
            };
        }
    }
}
