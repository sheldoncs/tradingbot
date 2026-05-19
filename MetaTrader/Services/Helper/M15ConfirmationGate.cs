namespace MetaTrader.Services.Helper
{
    using global::MetaTrader.Enums;
    using global::MetaTrader.Util;
    using System;
    using static global::MetaTrader.Services.TradeServiceEngine;

    public static class M15ConfirmationGate
    {
        public enum M15GateDecision
        {
            Allow,
            Block,
            SoftBypass
        }

        private class M15GateProfile
        {
            public double MinMomentumForSoftBypass { get; set; }
            public double MinAtrPercentileForSoftBypass { get; set; }
            public bool AllowSoftBypassForPullbackEngines { get; set; }
            public bool RequireHardM15ForBreakoutEngines { get; set; }
        }

        public static M15GateDecision Evaluate(
            string symbol,
            TradeAction action,
            EntryModel entryModel,
            bool m15Allowed,
            string reason,
            SymbolContext ctx,
            double atrPercentile,
            out string decisionReason)
        {
            decisionReason = "";

            if (m15Allowed)
            {
                decisionReason = "M15 confirmed";
                return M15GateDecision.Allow;
            }

            if (ctx == null)
            {
                decisionReason = $"M15 blocked: ctx is null. Reason={reason}";
                return M15GateDecision.Block;
            }

            var profile = GetProfile(symbol);

            double normalizedAtrPercentile = atrPercentile > 1.0
                ? atrPercentile / 100.0
                : atrPercentile;

            bool isBuy = action == TradeAction.EnterBuy || action == TradeAction.Bullish;
            bool isSell = action == TradeAction.EnterSell || action == TradeAction.Bearish;

            bool m15HardOpposite = IsHardOpposite(reason, isBuy, isSell);

            bool pullbackStyleEngine =
                entryModel == EntryModel.TrendRide ||
                entryModel == EntryModel.Sniper ||
                entryModel == EntryModel.Pullback ||
                entryModel == EntryModel.Continuation;

            bool aggressiveBreakoutEngine =
                entryModel == EntryModel.Velocity ||
                entryModel == EntryModel.Cascade ||
                entryModel == EntryModel.Breakout ||
                entryModel == EntryModel.BreakoutContinuation;

            // Velocity / Cascade / Breakout should usually require hard M15 confirmation.
            if (aggressiveBreakoutEngine && profile.RequireHardM15ForBreakoutEngines)
            {
                decisionReason = $"M15 hard block for breakout-style engine. Reason={reason}";
                return M15GateDecision.Block;
            }

            // If M15 is clearly opposite, block.
            if (m15HardOpposite)
            {
                decisionReason = $"M15 hard opposite. Reason={reason}";
                return M15GateDecision.Block;
            }

            bool goodState =
                ctx.State == MarketState.Armed ||
                ctx.State == MarketState.Tradable;

            bool strongMomentum =
                ctx.CurrentMomentum >= profile.MinMomentumForSoftBypass;

            bool atrOk =
                normalizedAtrPercentile >= profile.MinAtrPercentileForSoftBypass;

            bool allowSoftBypass =
                profile.AllowSoftBypassForPullbackEngines &&
                pullbackStyleEngine &&
                goodState &&
                strongMomentum &&
                atrOk &&
                !m15HardOpposite;

            if (allowSoftBypass)
            {
                decisionReason =
                    $"M15 soft bypass allowed | " +
                    $"engine={entryModel}, state={ctx.State}, momentum={ctx.CurrentMomentum:F2}, " +
                    $"ATR%={normalizedAtrPercentile:F2}, reason={reason}";

                return M15GateDecision.SoftBypass;
            }

            decisionReason =
                $"M15 blocked | engine={entryModel}, state={ctx.State}, " +
                $"momentum={ctx.CurrentMomentum:F2}, ATR%={normalizedAtrPercentile:F2}, reason={reason}";

            return M15GateDecision.Block;
        }

        public static bool ShouldAllowTrade(
            string symbol,
            TradeAction action,
            EntryModel entryModel,
            bool m15Allowed,
            string reason,
            SymbolContext ctx,
            double atrPercentile,
            out string decisionReason)
        {
            var decision = Evaluate(
                symbol,
                action,
                entryModel,
                m15Allowed,
                reason,
                ctx,
                atrPercentile,
                out decisionReason
            );

            return decision == M15GateDecision.Allow ||
                   decision == M15GateDecision.SoftBypass;
        }

        private static bool IsHardOpposite(string reason, bool isBuy, bool isSell)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return false;

            string r = reason.ToLowerInvariant();

            bool saysOpposite =
                r.Contains("opposite") ||
                r.Contains("against") ||
                r.Contains("counter") ||
                r.Contains("not aligned bearish") ||
                r.Contains("not aligned bullish");

            bool bearishAgainstBuy =
                isBuy &&
                (
                    r.Contains("bearish") ||
                    r.Contains("sell bias") ||
                    r.Contains("downtrend") ||
                    r.Contains("lower low") ||
                    r.Contains("lower high")
                );

            bool bullishAgainstSell =
                isSell &&
                (
                    r.Contains("bullish") ||
                    r.Contains("buy bias") ||
                    r.Contains("uptrend") ||
                    r.Contains("higher high") ||
                    r.Contains("higher low")
                );

            return saysOpposite || bearishAgainstBuy || bullishAgainstSell;
        }

        private static M15GateProfile GetProfile(string symbol)
        {
            string s = NormalizeSymbol(symbol);

            // Gold / Metals
            if (IsGold(s))
            {
                return new M15GateProfile
                {
                    MinMomentumForSoftBypass = 0.75,
                    MinAtrPercentileForSoftBypass = 0.35,
                    AllowSoftBypassForPullbackEngines = true,
                    RequireHardM15ForBreakoutEngines = true
                };
            }

            // Crypto
            if (IsCrypto(s))
            {
                return new M15GateProfile
                {
                    MinMomentumForSoftBypass = 0.80,
                    MinAtrPercentileForSoftBypass = 0.38,
                    AllowSoftBypassForPullbackEngines = true,
                    RequireHardM15ForBreakoutEngines = true
                };
            }

            // Indices / CFDs
            if (IsIndex(s))
            {
                return new M15GateProfile
                {
                    MinMomentumForSoftBypass = 0.78,
                    MinAtrPercentileForSoftBypass = 0.35,
                    AllowSoftBypassForPullbackEngines = true,
                    RequireHardM15ForBreakoutEngines = true
                };
            }

            // FX default: EURUSD, GBPUSD, USDJPY, EURJPY, USDCAD, AUDUSD, etc.
            return new M15GateProfile
            {
                MinMomentumForSoftBypass = 0.70,
                MinAtrPercentileForSoftBypass = 0.32,
                AllowSoftBypassForPullbackEngines = true,
                RequireHardM15ForBreakoutEngines = true
            };
        }

        private static string NormalizeSymbol(string symbol)
        {
            return string.IsNullOrWhiteSpace(symbol)
                ? ""
                : symbol.ToUpperInvariant();
        }

        private static bool IsGold(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            return symbol.Contains("XAU") ||
                   symbol.Contains("XAUEUR") ||
                   symbol.Contains("XAG") ||
                   symbol.Contains("GOLD") ||
                   symbol.Contains("SILVER");
        }

        private static bool IsCrypto(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            return symbol.Contains("BTC") ||
                   symbol.Contains("ETH") ||
                   symbol.Contains("SOL") ||
                   symbol.Contains("BNB") ||
                   symbol.Contains("XRP") ||
                   symbol.Contains("ADA") ||
                   symbol.Contains("AVAX") ||
                   symbol.Contains("DOGE") ||
                   symbol.Contains("LTC") ||
                   symbol.Contains("LINK");
        }

        private static bool IsIndex(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            return symbol.Contains("US100") ||
                   symbol.Contains("US100.CASH") ||
                   symbol.Contains("NAS") ||
                   symbol.Contains("NAS100") ||
                   symbol.Contains("USTEC") ||
                   symbol.Contains("US30") ||
                   symbol.Contains("DJ") ||
                   symbol.Contains("SPX") ||
                   symbol.Contains("US500") ||
                   symbol.Contains("DAX") ||
                   symbol.Contains("GER") ||
                   symbol.Contains("UK100");
        }
    }
}
