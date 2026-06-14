using MetaTrader.Util;
using System;
using System.Collections.Generic;
using static MetaTrader.Services.TradeServiceEngine;

namespace MetaTrader.Services.Helper
{
    public static class SymbolTradeCooldown
    {
        private class CooldownInfo
        {
            public DateTime LastExitUtc { get; set; }
            public EntryModel LastModel { get; set; }
            public bool LastTradeWasProfit { get; set; }
        }

        private static readonly Dictionary<string, CooldownInfo> _cooldowns = new();

        public static void MarkExit(string symbol, EntryModel model, double profitUsd)
        {
            symbol = Normalize(symbol);

            _cooldowns[symbol] = new CooldownInfo
            {
                LastExitUtc = DateTime.UtcNow,
                LastModel = model,
                LastTradeWasProfit = profitUsd > 0
            };
        }

        public static bool IsBlocked(string symbol, EntryModel model)
        {
            symbol = Normalize(symbol);

            if (!_cooldowns.TryGetValue(symbol, out var info))
                return false;

            int cooldownMinutes = GetCooldownMinutes(symbol, model, info.LastTradeWasProfit);

            double minutesSinceExit =
                (DateTime.UtcNow - info.LastExitUtc).TotalMinutes;

            if (minutesSinceExit < cooldownMinutes)
            {
                LogFile.Write(
                    $"[{symbol}] Trade blocked: same-symbol cooldown | " +
                    $"model={model}, minutesSinceExit={minutesSinceExit:F1}, " +
                    $"required={cooldownMinutes}, lastProfit={info.LastTradeWasProfit}"
                );

                return true;
            }

            return false;
        }

        private static int GetCooldownMinutes(
            string symbol,
            EntryModel model,
            bool lastTradeWasProfit)
        {
            // Longer cooldown after profitable TrendRide exits
            if (model == EntryModel.TrendRide ||
                model == EntryModel.TrendRideContinuation)
            {
                if (IsGold(symbol)) return lastTradeWasProfit ? 60 : 30;
                if (IsCrypto(symbol)) return lastTradeWasProfit ? 45 : 20;
                if (IsFx(symbol)) return lastTradeWasProfit ? 45 : 20;
            }

            // Breakout engines may re-enter sooner
            if (model == EntryModel.BreakoutContinuation ||
                model == EntryModel.Breakout)
            {
                if (IsGold(symbol)) return 30;
                if (IsCrypto(symbol)) return 20;
                if (IsFx(symbol)) return 20;
            }

            return 30;
        }

        private static string Normalize(string symbol)
        {
            return string.IsNullOrWhiteSpace(symbol)
                ? ""
                : symbol.Replace(".s", "")
                        .Replace("_i", "")
                        .Replace("m", "")
                        .ToUpperInvariant();
        }

        private static bool IsGold(string symbol)
            => symbol.Contains("XAU") || symbol.Contains("XAG");

        private static bool IsCrypto(string symbol)
            => symbol.Contains("BTC") ||
               symbol.Contains("ETH") ||
               symbol.Contains("SOL") ||
               symbol.Contains("BNB") ||
               symbol.Contains("XRP") ||
               symbol.Contains("ADA") ||
               symbol.Contains("AVAX") ||
               symbol.Contains("LINK") ||
               symbol.Contains("LTC") ||
               symbol.Contains("DOGE");

        private static bool IsFx(string symbol)
            => !IsGold(symbol) && !IsCrypto(symbol);
    }
}
