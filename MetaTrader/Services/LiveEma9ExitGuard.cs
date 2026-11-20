
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
        private static readonly object Sync = new();

        private sealed class BreachState
        {
            public DateTime StartedUtc;
            public DateTime LastSeenUtc;
            public bool AdaptiveGold;
        }

        private static readonly Dictionary<string, BreachState>
            BreachStates = new(StringComparer.OrdinalIgnoreCase);

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

        // ============================================================
        // ORIGINAL SIGNATURE - BACKWARD COMPATIBILITY
        // ============================================================

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
            return Evaluate(
                symbol,
                client,
                atr,
                ema9,
                ema21,
                side,
                profile,
                tradeOpen,
                isShockActive,
                out reason,
                false,
                0UL);
        }

        // ============================================================
        // ADAPTIVE GOLD WINNER MANAGEMENT
        // ============================================================

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
            out string reason,
            bool allowGoldWinnerRoom,
            ulong positionTicket)
        {
            reason = "";

            string s = symbol?.ToUpperInvariant() ?? "";

            bool isBuy =
                side == TradeAction.EnterBuy ||
                side == TradeAction.Buy;

            bool isSell =
                side == TradeAction.EnterSell ||
                side == TradeAction.Sell;

            bool adaptiveGold =
                IsGold(s) &&
                allowGoldWinnerRoom &&
                positionTicket != 0;

            string key =
                $"{s}|{positionTicket}|{(isBuy ? "BUY" : "SELL")}";

            if (!tradeOpen)
            {
                ResetSymbol(s);
                return LiveExitDecision.None;
            }

            if (isShockActive ||
                client == null ||
                string.IsNullOrWhiteSpace(s) ||
                !FinitePositive(atr) ||
                !FinitePositive(ema9) ||
                !FinitePositive(ema21) ||
                (!isBuy && !isSell))
            {
                ResetBreach(key);
                return LiveExitDecision.None;
            }

            try
            {
                var cfg = GetProfile(s);

                // ================================================
                // 1. LIVE BID AND ASK
                // ================================================

                double bid = client.SymbolInfoDouble(
                    symbol,
                    ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);

                double ask = client.SymbolInfoDouble(
                    symbol,
                    ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);

                if (!FinitePositive(bid) ||
                    !FinitePositive(ask) ||
                    ask < bid)
                {
                    ResetBreach(key);

                    reason = "Invalid live BID/ASK";

                    return LiveExitDecision.None;
                }

                double liveMid = (bid + ask) / 2.0;

                // Actual executable exit price:
                // BUY closes at BID.
                // SELL closes at ASK.

                double price = isBuy ? bid : ask;

                // ================================================
                // 2. LIVE EMA BREACH DISTANCES
                // ================================================

                double breach9 = isBuy
                    ? ema9 - price
                    : price - ema9;

                double breach21 = isBuy
                    ? ema21 - price
                    : price - ema21;

                double breach9Atr = breach9 / atr;
                double breach21Atr = breach21 / atr;

                if (breach9 <= 0)
                {
                    ResetBreach(key);

                    reason = "Price respecting EMA9";

                    return LiveExitDecision.None;
                }

                // A small breach must not inherit an older timer.

                if (breach9Atr < cfg.SoftBreachAtr)
                {
                    ResetBreach(key);

                    reason =
                        $"EMA9 breach too small | " +
                        $"breachATR={breach9Atr:F2}";

                    return LiveExitDecision.None;
                }

                // ================================================
                // 3. GET CURRENT H1 CANDLE
                // ================================================
                // Only one candle is requested.
                // No direct liveRates[0] dependency.
                // ================================================

                client.CopyRates(
                    symbol,
                    ENUM_TIMEFRAMES.PERIOD_H1,
                    0,
                    1,
                    out MqlRates[] liveRates);

                if (liveRates == null ||
                    liveRates.Length == 0)
                {
                    ResetBreach(key);

                    reason = "Current H1 candle unavailable";

                    return LiveExitDecision.None;
                }

                var live = liveRates[^1];

                if (!FinitePositive(live.open) ||
                    !FinitePositive(live.high) ||
                    !FinitePositive(live.low) ||
                    live.high < live.low)
                {
                    ResetBreach(key);

                    reason = "Invalid H1 candle data";

                    return LiveExitDecision.None;
                }

                // ================================================
                // 4. UPDATE LIVE CANDLE FROM BID/ASK
                // ================================================
                // Use midpoint for candle geometry.
                // Use BID/ASK for executable EMA breaches.
                // ================================================

                double liveOpen = live.open;
                double liveClose = liveMid;

                double liveHigh = Math.Max(
                    Math.Max(live.high, liveOpen),
                    liveMid);

                double liveLow = Math.Min(
                    Math.Min(live.low, liveOpen),
                    liveMid);

                double liveRange = Math.Max(
                    liveHigh - liveLow,
                    0.0000001);

                double liveBody = Math.Abs(
                    liveClose - liveOpen);

                double liveBodyRatio =
                    liveBody / liveRange;

                double liveRangeAtr =
                    liveRange / atr;

                double liveBodyAtr =
                    liveBody / atr;

                bool candleAgainst = isBuy
                    ? liveClose < liveOpen
                    : liveClose > liveOpen;

                // ================================================
                // 5. HARD FAILURE DETECTION
                // ================================================

                bool hardDistance =
                    breach9Atr >= cfg.HardBreachAtr;

                bool hardExpansion =
                    candleAgainst &&
                    liveRangeAtr >= cfg.HardRangeAtr &&
                    liveBodyRatio >= cfg.HardBodyRatio;

                string metrics =
                    $"bid={bid:F5} | " +
                    $"ask={ask:F5} | " +
                    $"mid={liveMid:F5} | " +
                    $"ema9BreachATR={breach9Atr:F2} | " +
                    $"ema21BreachATR={breach21Atr:F2} | " +
                    $"rangeATR={liveRangeAtr:F2} | " +
                    $"bodyATR={liveBodyAtr:F2} | " +
                    $"bodyRatio={liveBodyRatio:F2} | " +
                    $"candleAgainst={candleAgainst}";

                // ================================================
                // 6. ADAPTIVE GOLD WINNER PROTECTION
                // ================================================

                if (adaptiveGold)
                {
                    // Immediate danger:
                    // Preserve existing hard expansion protection
                    // and recognise decisive dual-EMA failure.

                    bool decisiveDualEmaFailure =
                        hardDistance &&
                        breach21Atr >= 0.20 &&
                        candleAgainst;

                    bool immediateDanger =
                        (hardDistance && hardExpansion) ||
                        decisiveDualEmaFailure;

                    if (immediateDanger)
                    {
                        ResetBreach(key);

                        reason =
                            $"GOLD ADAPTIVE EXIT | DANGER | " +
                            metrics;

                        LogFile.Write(
                            $"[{symbol}] [LiveEma9Exit] {reason}");

                        return LiveExitDecision.ExitToNextRegime;
                    }

                    // A breach of EMA9 alone is not sufficient
                    // to exit a protected profitable GOLD trade.

                    bool confirmedWeaknessCandidate =
                        breach9Atr >= cfg.SoftBreachAtr &&
                        breach21Atr >= 0.05 &&
                        candleAgainst;

                    if (!confirmedWeaknessCandidate)
                    {
                        ResetBreach(key);

                        reason =
                            $"GOLD ADAPTIVE HOLD | " +
                            $"HEALTHY_PULLBACK_OR_MIXED | " +
                            metrics;

                        return LiveExitDecision.None;
                    }

                    double weaknessSeconds =
                        ObserveBreach(key, adaptiveGold);

                    if (weaknessSeconds >= cfg.ConfirmSeconds)
                    {
                        ResetBreach(key);

                        reason =
                            $"GOLD ADAPTIVE EXIT | " +
                            $"CONFIRMED_WEAKNESS | " +
                            $"elapsed={weaknessSeconds:F0}s | " +
                            metrics;

                        LogFile.Write(
                            $"[{symbol}] [LiveEma9Exit] {reason}");

                        return LiveExitDecision.ExitToNextRegime;
                    }

                    reason =
                        $"GOLD ADAPTIVE HOLD | " +
                        $"WEAKNESS_PENDING | " +
                        $"elapsed={weaknessSeconds:F0}s/" +
                        $"{cfg.ConfirmSeconds}s | " +
                        metrics;

                    return LiveExitDecision.None;
                }

                // ================================================
                // 7. STANDARD EXIT PROTECTION
                // ================================================

                // Preserve the existing EMA21 grace behaviour
                // for non-adaptive positions.

                if (cfg.AllowGraceIfNearEma21)
                {
                    double distToEma21 =
                        Math.Abs(price - ema21);

                    if (distToEma21 <=
                        atr * cfg.NearEma21Atr)
                    {
                        ResetBreach(key);

                        reason =
                            $"Grace near EMA21 | " +
                            $"dist={distToEma21:F5} | " +
                            $"limit={atr * cfg.NearEma21Atr:F5}";

                        return LiveExitDecision.None;
                    }
                }

                if (IsCrypto(s) && hardDistance)
                {
                    ResetBreach(key);

                    reason =
                        $"CRYPTO HARD EMA9 BREACH | " +
                        metrics;

                    LogFile.Write(
                        $"[{symbol}] [LiveEma9Exit] {reason}");

                    return LiveExitDecision.ExitToNextRegime;
                }

                if (hardDistance && hardExpansion)
                {
                    ResetBreach(key);

                    reason =
                        $"HARD EMA9 FAILURE | " +
                        metrics;

                    LogFile.Write(
                        $"[{symbol}] [LiveEma9Exit] {reason}");

                    return cfg.ExitHardAsDisarm
                        ? LiveExitDecision.ExitAndDisarm
                        : LiveExitDecision.ExitToNextRegime;
                }

                // ================================================
                // 8. SUSTAINED SOFT FAILURE
                // ================================================

                double seconds = ObserveBreach(key, adaptiveGold);

                if (seconds >= cfg.ConfirmSeconds)
                {
                    ResetBreach(key);

                    reason =
                        $"SOFT EMA9 BREACH CONFIRMED | " +
                        $"elapsed={seconds:F0}s | " +
                        metrics;

                    LogFile.Write(
                        $"[{symbol}] [LiveEma9Exit] {reason}");

                    return LiveExitDecision.ExitToNextRegime;
                }

                reason =
                    $"EMA9 BREACH CONFIRMING | " +
                    $"elapsed={seconds:F0}s/" +
                    $"{cfg.ConfirmSeconds}s | " +
                    metrics;

                return LiveExitDecision.None;
            }
            catch (Exception ex)
            {
                ResetBreach(key);

                reason =
                    $"LiveEma9ExitGuard evaluation error | " +
                    $"{ex.Message}";

                LogFile.Write($"[{symbol}] {reason}");

                // Unified risk management and existing broker
                // stop-loss protection remain responsible.

                return LiveExitDecision.None;
            }
        }

        // ============================================================
        // CONTINUOUS BREACH CONFIRMATION
        // Never carry confirmation time across standard/adaptive mode changes.
        // ============================================================

        private static double ObserveBreach(string key, bool adaptiveGold)
        {
            DateTime now = DateTime.UtcNow;

            lock (Sync)
            {
                var expired = new List<string>();

                foreach (var pair in BreachStates)
                {
                    if ((now - pair.Value.LastSeenUtc)
                        .TotalSeconds > 120)
                    {
                        expired.Add(pair.Key);
                    }
                }

                foreach (string oldKey in expired)
                {
                    BreachStates.Remove(oldKey);
                }

                if (!BreachStates.TryGetValue(
                        key,
                        out BreachState state) ||
                    state.AdaptiveGold != adaptiveGold ||
                    now < state.LastSeenUtc ||
                    (now - state.LastSeenUtc)
                        .TotalSeconds > 15)
                {
                    state = new BreachState
                    {
                        StartedUtc = now,
                        LastSeenUtc = now,
                        AdaptiveGold = adaptiveGold
                    };

                    BreachStates[key] = state;

                    return 0;
                }

                state.LastSeenUtc = now;

                return Math.Max(
                    0,
                    (now - state.StartedUtc).TotalSeconds);
            }
        }

        private static void ResetBreach(string key)
        {
            lock (Sync)
            {
                BreachStates.Remove(key);
            }
        }

        private static void ResetSymbol(string symbol)
        {
            lock (Sync)
            {
                var keys = new List<string>();

                foreach (var pair in BreachStates)
                {
                    if (pair.Key.StartsWith(
                        symbol + "|",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        keys.Add(pair.Key);
                    }
                }

                foreach (string key in keys)
                {
                    BreachStates.Remove(key);
                }
            }
        }

        private static bool FinitePositive(double value)
        {
            return value > 0 &&
                   !double.IsNaN(value) &&
                   !double.IsInfinity(value);
        }

        // ============================================================
        // ASSET-SPECIFIC EXIT PROFILES
        // ============================================================

        private static SymbolExitProfile GetProfile(string symbol)
        {
            string s = symbol?.ToUpperInvariant() ?? "";

            if (IsCrypto(s))
            {
                bool bnb = s.Contains("BNB");

                return new SymbolExitProfile
                {
                    SoftBreachAtr = bnb ? 0.38 : 0.30,
                    HardBreachAtr = bnb ? 0.60 : 0.55,
                    HardRangeAtr = bnb ? 1.30 : 1.25,
                    HardBodyRatio = bnb ? 0.60 : 0.58,
                    ConfirmSeconds = bnb ? 75 : 45,
                    AllowGraceIfNearEma21 = true,
                    NearEma21Atr = bnb ? 0.40 : 0.35,
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

            // FX default

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
