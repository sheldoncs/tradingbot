using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Collections.Generic;

namespace MetaTrader.Services
{
    public enum LiveExitDecision
    {
        None,
        ExitToNextRegime,
        ExitAndDisarm
    }

    public static class LiveEma9ExitGuard
    {
        private static readonly Dictionary<string, DateTime> _breachSince = new();

        private class SymbolExitProfile
        {
            public double SoftBreachAtr { get; set; }
            public double HardBreachAtr { get; set; }
            public double HardRangeAtr { get; set; }
            public double HardBodyRatio { get; set; }
            public int ConfirmSeconds { get; set; }
            public bool AllowGraceIfNearEma21 { get; set; }
            public double NearEma21Atr { get; set; }
            public bool ExitHardAsDisarm { get; set; }
        }

        public static LiveExitDecision Evaluate(
            string symbol,
            MtApi5Client client,
            double atr,
            double ema9,
            double ema21,
            TradeAction side,
            EntryExitProfile profile,
            bool tradeOpen,
            bool isShockActive,
            out string reason)
        {
            reason = "";

            if (!tradeOpen || atr <= 0 || client == null)
                return LiveExitDecision.None;

            if (isShockActive)
                return LiveExitDecision.None;

            bool isBuy = side == TradeAction.EnterBuy || side == TradeAction.Buy;
            bool isSell = side == TradeAction.EnterSell || side == TradeAction.Sell;

            if (!isBuy && !isSell)
                return LiveExitDecision.None;

            var cfg = GetProfile(symbol);

            double bid = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);
            double ask = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);

            double price = isBuy ? bid : ask;

            client.CopyRates(symbol, ENUM_TIMEFRAMES.PERIOD_H1, 0, 2, out MqlRates[] liveRates);

            if (liveRates == null || liveRates.Length < 1)
                return LiveExitDecision.None;

            var live = liveRates[0];

            double liveRange = live.high - live.low;
            if (liveRange <= 0)
                liveRange = 0.0000001;

            double liveBody = Math.Abs(live.close - live.open);
            double liveBodyRatio = liveBody / liveRange;

            double breach = isBuy
                ? ema9 - price     // BUY fails below EMA9
                : price - ema9;    // SELL fails above EMA9

            bool breached = breach > 0;

            if (!breached)
            {
                _breachSince.Remove(symbol);
                return LiveExitDecision.None;
            }

            if (cfg.AllowGraceIfNearEma21)
            {
                double distToEma21 = Math.Abs(price - ema21);

                if (distToEma21 <= atr * cfg.NearEma21Atr)
                {
                    _breachSince.Remove(symbol);

                    reason =
                        $"Grace near EMA21 | dist={distToEma21:F5}, " +
                        $"limit={atr * cfg.NearEma21Atr:F5}";

                    LogFile.Write($"[{symbol}] [LiveEma9Exit] {reason}");
                    return LiveExitDecision.None;
                }
            }

            bool candleAgainst = isBuy
                ? live.close < live.open
                : live.close > live.open;

            bool hardDistance =
                breach >= atr * cfg.HardBreachAtr;

            bool hardExpansion =
                candleAgainst &&
                liveRange >= atr * cfg.HardRangeAtr &&
                liveBodyRatio >= cfg.HardBodyRatio;

            if (IsCrypto(symbol) && hardDistance)
            {
                reason = $"CRYPTO HARD EMA9 breach by live price | breach={breach:F5}";
                LogFile.Write($"[{symbol}] [LiveEma9Exit] {reason}");
                return LiveExitDecision.ExitToNextRegime;
            }

            if (hardDistance && hardExpansion)
            {
                _breachSince.Remove(symbol);

                reason =
                    $"HARD EMA9 failure confirmed | breach={breach:F5}, " +
                    $"hardNeed={atr * cfg.HardBreachAtr:F5}, " +
                    $"rangeATR={(liveRange / atr):F2}, bodyRatio={liveBodyRatio:F2}";

                LogFile.Write($"[{symbol}] [LiveEma9Exit] {reason}");

                return cfg.ExitHardAsDisarm
                    ? LiveExitDecision.ExitAndDisarm
                    : LiveExitDecision.ExitToNextRegime;
            }

            bool softDistance =
                breach >= atr * cfg.SoftBreachAtr;

            if (!softDistance)
            {
                reason =
                    $"EMA9 breach too small | breach={breach:F5}, " +
                    $"need={atr * cfg.SoftBreachAtr:F5}";

                return LiveExitDecision.None;
            }

            if (!_breachSince.TryGetValue(symbol, out DateTime started))
            {
                _breachSince[symbol] = DateTime.UtcNow;

                reason =
                    $"EMA9 breach timer started | breach={breach:F5}, " +
                    $"need={atr * cfg.SoftBreachAtr:F5}";

                return LiveExitDecision.None;
            }

            var elapsed = DateTime.UtcNow - started;

            if (elapsed.TotalSeconds >= cfg.ConfirmSeconds)
            {
                _breachSince.Remove(symbol);

                reason =
                    $"SOFT EMA9 breach confirmed | elapsed={elapsed.TotalSeconds:F0}s, " +
                    $"breach={breach:F5}, need={atr * cfg.SoftBreachAtr:F5}";

                LogFile.Write($"[{symbol}] [LiveEma9Exit] {reason}");

                return LiveExitDecision.ExitToNextRegime;
            }

            reason =
                $"EMA9 breach confirming | elapsed={elapsed.TotalSeconds:F0}s/" +
                $"{cfg.ConfirmSeconds}s, breach={breach:F5}";

            return LiveExitDecision.None;
        }

        private static SymbolExitProfile GetProfile(string symbol)
        {
            string s = symbol?.ToUpperInvariant() ?? "";

            if (IsCrypto(s))
            {   
                return new SymbolExitProfile
                {
                    SoftBreachAtr = s.Contains("BNB") ? 0.38 : 0.30,
                    HardBreachAtr = s.Contains("BNB") ? 0.60 : 0.55,
                    HardRangeAtr = s.Contains("BNB") ? 1.30 : 1.25,
                    HardBodyRatio = s.Contains("BNB") ? 0.60 : 0.58,
                    ConfirmSeconds = s.Contains("BNB")? 75 : 45,
                    AllowGraceIfNearEma21 = true,
                    NearEma21Atr = s.Contains("BNB") ? 0.40 : 0.35,
                    ExitHardAsDisarm = false
                };
            }

            if (IsGold(s))
            {
                return new SymbolExitProfile
                {
                    SoftBreachAtr = 0.25,
                    HardBreachAtr = 0.45,
                    HardRangeAtr = 1.15,
                    HardBodyRatio = 0.60,
                    ConfirmSeconds = 35,
                    AllowGraceIfNearEma21 = true,
                    NearEma21Atr = 0.30,
                    ExitHardAsDisarm = false
                };
            }

            if (IsIndex(s))
            {
                return new SymbolExitProfile
                {
                    SoftBreachAtr = 0.28,
                    HardBreachAtr = 0.50,
                    HardRangeAtr = 1.20,
                    HardBodyRatio = 0.58,
                    ConfirmSeconds = 40,
                    AllowGraceIfNearEma21 = true,
                    NearEma21Atr = 0.32,
                    ExitHardAsDisarm = false
                };
            }

            // FX default: tighter because FX usually moves cleaner
            return new SymbolExitProfile
            {
                SoftBreachAtr = 0.20,
                HardBreachAtr = 0.35,
                HardRangeAtr = 1.00,
                HardBodyRatio = 0.55,
                ConfirmSeconds = 25,
                AllowGraceIfNearEma21 = true,
                NearEma21Atr = 0.25,
                ExitHardAsDisarm = false
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
                   symbol.Contains("LTC") ||
                   symbol.Contains("LINK") ||
                   symbol.Contains("AVAX");
        }

        private static bool IsIndex(string symbol)
        {
            return symbol.Contains("US100") ||
                   symbol.Contains("US100.CASH") ||
                   symbol.Contains("NAS") ||
                   symbol.Contains("NAS100") ||
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
