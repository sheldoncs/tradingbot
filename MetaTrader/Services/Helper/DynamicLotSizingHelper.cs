using MetaTrader.Util;
using System;
using System.Collections.Generic;

namespace MetaTrader.Services.Helper
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public class SymbolLotProfile
    {
        public double BaseLot { get; set; }
        public double VolatilityFactor { get; set; }
        public double HighVolatilityCut { get; set; }
        public double LowVolatilityBoost { get; set; }
        public double MaxLot { get; set; }
        public double ContractSize { get; set; }
    }

    public enum RiskMode
    {
        Recovery,
        Normal,
        Aggressive
    }

    public static class DynamicLotSizingHelper
    {
        // =====================================================
        // CURRENT MODE
        // =====================================================
        public static RiskMode CurrentRiskMode = RiskMode.Recovery;

        // =====================================================
        // RISK SETTINGS
        // =====================================================
        public static double RecoveryRiskPercent = 0.10;
        public static double NormalRiskPercent = 0.25;
        public static double AggressiveRiskPercent = 0.35;

        public static bool AllowMomentumLotBoost = false;

        // =====================================================
        // DYNAMIC TRACKING
        // =====================================================
        public static double PeakEquity = 10000;

        private static readonly Dictionary<string, int> SymbolWins = new();
        private static readonly Dictionary<string, int> SymbolLosses = new();

        // =====================================================
        // SYMBOL PROFILES
        // =====================================================
        private static readonly Dictionary<string, SymbolLotProfile> Profiles = new()
        {
            // -----------------------------
            // GOLD / METALS
            // -----------------------------
            ["XAUUSD"] = new SymbolLotProfile
            {
                BaseLot = 0.01,
                VolatilityFactor = 0.80,
                HighVolatilityCut = 0.70,
                LowVolatilityBoost = 1.00,
                MaxLot = 0.03,
                ContractSize = 100
            },

            ["XAUEUR"] = new SymbolLotProfile
            {
                BaseLot = 0.01,
                VolatilityFactor = 0.75,
                HighVolatilityCut = 0.70,
                LowVolatilityBoost = 1.00,
                MaxLot = 0.03,
                ContractSize = 100
            },

            // -----------------------------
            // CRYPTO
            // -----------------------------
            ["ETHUSD"] = new SymbolLotProfile
            {
                BaseLot = 0.01,
                VolatilityFactor = 0.85,
                HighVolatilityCut = 0.65,
                LowVolatilityBoost = 1.00,
                MaxLot = 0.05,
                ContractSize = 1
            },

            ["BTCUSD"] = new SymbolLotProfile
            {
                BaseLot = 0.01,
                VolatilityFactor = 0.70,
                HighVolatilityCut = 0.60,
                LowVolatilityBoost = 1.00,
                MaxLot = 0.03,
                ContractSize = 1
            },

            ["SOLUSD"] = new SymbolLotProfile
            {
                BaseLot = 0.01,
                VolatilityFactor = 0.85,
                HighVolatilityCut = 0.65,
                LowVolatilityBoost = 1.00,
                MaxLot = 0.03,
                ContractSize = 1
            },

            ["BNBUSD"] = new SymbolLotProfile
            {
                BaseLot = 0.01,
                VolatilityFactor = 0.85,
                HighVolatilityCut = 0.65,
                LowVolatilityBoost = 1.00,
                MaxLot = 0.02,
                ContractSize = 1
            },

            ["XRPUSD"] = new SymbolLotProfile
            {
                BaseLot = 0.01,
                VolatilityFactor = 0.90,
                HighVolatilityCut = 0.70,
                LowVolatilityBoost = 1.00,
                MaxLot = 0.06,
                ContractSize = 1
            },

            ["ADAUSD"] = new SymbolLotProfile
            {
                BaseLot = 0.01,
                VolatilityFactor = 0.90,
                HighVolatilityCut = 0.70,
                LowVolatilityBoost = 1.00,
                MaxLot = 0.06,
                ContractSize = 1
            },

            // -----------------------------
            // INDICES
            // -----------------------------
            ["US100.cash"] = new SymbolLotProfile
            {
                BaseLot = 0.01,
                VolatilityFactor = 0.80,
                HighVolatilityCut = 0.65,
                LowVolatilityBoost = 1.00,
                MaxLot = 0.03,
                ContractSize = 1
            },

            ["US100"] = new SymbolLotProfile
            {
                BaseLot = 0.01,
                VolatilityFactor = 0.80,
                HighVolatilityCut = 0.65,
                LowVolatilityBoost = 1.00,
                MaxLot = 0.03,
                ContractSize = 1
            },

            // -----------------------------
            // FX
            // -----------------------------
            ["EURUSD"] = new SymbolLotProfile
            {
                BaseLot = 0.01,
                VolatilityFactor = 1.00,
                HighVolatilityCut = 0.80,
                LowVolatilityBoost = 1.00,
                MaxLot = 0.03,
                ContractSize = 100000
            },

            ["GBPUSD"] = new SymbolLotProfile
            {
                BaseLot = 0.01,
                VolatilityFactor = 0.90,
                HighVolatilityCut = 0.75,
                LowVolatilityBoost = 1.00,
                MaxLot = 0.03,
                ContractSize = 100000
            },

            ["USDJPY"] = new SymbolLotProfile
            {
                BaseLot = 0.01,
                VolatilityFactor = 0.90,
                HighVolatilityCut = 0.75,
                LowVolatilityBoost = 1.00,
                MaxLot = 0.03,
                ContractSize = 100000
            },

            ["EURJPY"] = new SymbolLotProfile
            {
                BaseLot = 0.01,
                VolatilityFactor = 0.85,
                HighVolatilityCut = 0.75,
                LowVolatilityBoost = 1.00,
                MaxLot = 0.03,
                ContractSize = 100000
            },

            ["USDCAD"] = new SymbolLotProfile
            {
                BaseLot = 0.01,
                VolatilityFactor = 0.95,
                HighVolatilityCut = 0.80,
                LowVolatilityBoost = 1.00,
                MaxLot = 0.03,
                ContractSize = 100000
            },

            ["AUDUSD"] = new SymbolLotProfile
            {
                BaseLot = 0.01,
                VolatilityFactor = 1.00,
                HighVolatilityCut = 0.80,
                LowVolatilityBoost = 1.00,
                MaxLot = 0.03,
                ContractSize = 100000
            }
        };

        public class LotDecision
        {
            public double Lot { get; set; }
            public string Reason { get; set; }
            public double RiskPercent { get; set; }
            public double RiskAmount { get; set; }
            public double SlDistance { get; set; }
        }

        // =====================================================
        // REGISTER TRADE RESULT
        // =====================================================
        public static void RegisterTradeResult(
            string symbol,
            bool win)
        {
            symbol = NormalizeSymbol(symbol);

            if (!SymbolWins.ContainsKey(symbol))
                SymbolWins[symbol] = 0;

            if (!SymbolLosses.ContainsKey(symbol))
                SymbolLosses[symbol] = 0;

            if (win)
                SymbolWins[symbol]++;
            else
                SymbolLosses[symbol]++;
        }

        // =====================================================
        // AUTO UPDATE RISK MODE
        // =====================================================
        public static void UpdateRiskMode(
            double currentEquity)
        {
            PeakEquity =
                Math.Max(PeakEquity, currentEquity);

            double drawdownPercent =
                ((PeakEquity - currentEquity)
                / PeakEquity) * 100.0;

            int totalWins =
                SymbolWins.Values.Sum();

            int totalLosses =
                SymbolLosses.Values.Sum();

            int totalTrades =
                totalWins + totalLosses;

            double winRate = 0;

            if (totalTrades > 0)
            {
                winRate =
                    (double)totalWins /
                    totalTrades;
            }

            // --------------------------------
            // RECOVERY
            // --------------------------------
            if (drawdownPercent >= 3.0 ||
                totalLosses >= totalWins + 3)
            {
                if (CurrentRiskMode != RiskMode.Recovery)
                {
                    LogFile.Write(
                        $"⚠️ RiskMode → RECOVERY | DD={drawdownPercent:F2}%");
                }

                CurrentRiskMode = RiskMode.Recovery;
                return;
            }

            // --------------------------------
            // AGGRESSIVE
            // --------------------------------
            if (drawdownPercent < 1.0 &&
                totalTrades >= 10 &&
                winRate >= 0.65)
            {
                if (CurrentRiskMode != RiskMode.Aggressive)
                {
                    LogFile.Write(
                        $"🚀 RiskMode → AGGRESSIVE | WR={winRate:P1}");
                }

                CurrentRiskMode = RiskMode.Aggressive;
                return;
            }

            // --------------------------------
            // NORMAL
            // --------------------------------
            if (CurrentRiskMode != RiskMode.Normal)
            {
                LogFile.Write("✅ RiskMode → NORMAL");
            }

            CurrentRiskMode = RiskMode.Normal;
        }

        // =====================================================
        // MAIN METHOD
        // =====================================================
        public static LotDecision Calculate(
            string symbol,
            double accountEquity,
            double entryPrice,
            double stopLoss,
            double atrPercentile,
            bool isStrongMomentum)
        {
            symbol = NormalizeSymbol(symbol);

            var profile = GetProfile(symbol);

            double riskPercent = GetRiskPercent(symbol);

            double riskAmount =
                accountEquity * (riskPercent / 100.0);

            double normalizedAtrPercentile =
                atrPercentile > 1.0
                ? atrPercentile / 100.0
                : atrPercentile;

            // --------------------------------
            // REAL SL DISTANCE
            // --------------------------------
            double slDistance =
                Math.Abs(entryPrice - stopLoss);

            if (slDistance <= 0)
            {
                LogFile.Write(
                    $"❌ {symbol} invalid SL distance");

                return new LotDecision
                {
                    Lot = profile.BaseLot,
                    Reason = "Invalid SL distance",
                    RiskPercent = riskPercent,
                    RiskAmount = riskAmount,
                    SlDistance = slDistance
                };
            }

            // --------------------------------
            // BASE LOT
            // --------------------------------
            double lot =
                riskAmount /
                (slDistance * profile.ContractSize);

            // --------------------------------
            // HIGH VOLATILITY CUT
            // --------------------------------
            if (normalizedAtrPercentile >= 0.85)
            {
                lot *= profile.HighVolatilityCut;
            }

            // --------------------------------
            // LOW VOLATILITY BOOST
            // --------------------------------
            else if (
                normalizedAtrPercentile <= 0.30 &&
                CurrentRiskMode != RiskMode.Recovery)
            {
                lot *= profile.LowVolatilityBoost;
            }

            // --------------------------------
            // MOMENTUM BOOST
            // --------------------------------
            if (
                isStrongMomentum &&
                AllowMomentumLotBoost &&
                CurrentRiskMode != RiskMode.Recovery)
            {
                lot *= 1.10;
            }

            // --------------------------------
            // SYMBOL FACTOR
            // --------------------------------
            lot *= profile.VolatilityFactor;

            // --------------------------------
            // ROUNDING
            // --------------------------------
            lot = Math.Round(lot, 2);

            // --------------------------------
            // MIN LOT
            // --------------------------------
            lot = Math.Max(profile.BaseLot, lot);

            // --------------------------------
            // MAX CAP
            // --------------------------------
            if (lot > profile.MaxLot)
            {
                LogFile.Write(
                    $"⚠️ {symbol} lot capped from {lot} → {profile.MaxLot}");

                lot = profile.MaxLot;
            }

            // --------------------------------
            // FINAL LOG
            // --------------------------------
            LogFile.Write(
                $"[{symbol}] Lot={lot:F2} | " +
                $"Mode={CurrentRiskMode} | " +
                $"Equity={accountEquity:F2} | " +
                $"Risk%={riskPercent:F2}% | " +
                $"Risk=${riskAmount:F2} | " +
                $"SL={slDistance:F5} | " +
                $"ATR%={normalizedAtrPercentile:F2}"
            );

            return new LotDecision
            {
                Lot = lot,
                Reason =
                    $"{symbol} → SL-based dynamic sizing ({CurrentRiskMode})",

                RiskPercent = riskPercent,
                RiskAmount = riskAmount,
                SlDistance = slDistance
            };
        }

        // =====================================================
        // BACKWARD COMPATIBLE
        // =====================================================
        public static LotDecision Calculate(
            string symbol,
            double entryPrice,
            double stopLoss,
            double atrPercentile,
            bool isStrongMomentum)
        {
            double fallbackEquity = 9637.00;

            LogFile.Write(
                $"⚠️ Old Calculate() overload used. " +
                $"Fallback equity={fallbackEquity:F2}");

            return Calculate(
                symbol,
                fallbackEquity,
                entryPrice,
                stopLoss,
                atrPercentile,
                isStrongMomentum
            );
        }

        // =====================================================
        // UPDATED RISK %
        // =====================================================
        private static double GetRiskPercent(
            string symbol)
        {
            double risk =
                CurrentRiskMode switch
                {
                    RiskMode.Recovery =>
                        RecoveryRiskPercent,

                    RiskMode.Normal =>
                        NormalRiskPercent,

                    RiskMode.Aggressive =>
                        AggressiveRiskPercent,

                    _ => RecoveryRiskPercent
                };

            symbol = NormalizeSymbol(symbol);

            int wins =
                SymbolWins.ContainsKey(symbol)
                ? SymbolWins[symbol]
                : 0;

            int losses =
                SymbolLosses.ContainsKey(symbol)
                ? SymbolLosses[symbol]
                : 0;

            int total = wins + losses;

            double symbolWinRate = 0.50;

            if (total >= 5)
            {
                symbolWinRate =
                    (double)wins / total;
            }

            // --------------------------------
            // WEAK SYMBOL
            // --------------------------------
            if (total >= 5 &&
                symbolWinRate < 0.40)
            {
                risk *= 0.75;

                LogFile.Write(
                    $"⚠️ {symbol} weak performance → reduced risk");
            }

            // --------------------------------
            // STRONG SYMBOL
            // --------------------------------
            if (total >= 10 &&
                symbolWinRate >= 0.70 &&
                CurrentRiskMode == RiskMode.Aggressive)
            {
                risk *= 1.10;

                LogFile.Write(
                    $"🚀 {symbol} strong performance → slight boost");
            }

            // --------------------------------
            // SAFETY CAPS
            // --------------------------------
            risk = Math.Min(risk, 0.35);
            risk = Math.Max(risk, 0.05);

            return risk;
        }

        private static SymbolLotProfile GetProfile(
            string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            if (Profiles.ContainsKey(symbol))
                return Profiles[symbol];

            if (IsGold(symbol))
                return Profiles["XAUUSD"];

            if (IsCrypto(symbol))
                return Profiles["ETHUSD"];

            if (IsIndex(symbol))
                return Profiles["US100.cash"];

            return Profiles["EURUSD"];
        }

        private static string NormalizeSymbol(
            string symbol)
        {
            return string.IsNullOrWhiteSpace(symbol)
                ? ""
                : symbol.ToUpperInvariant();
        }

        private static bool IsGold(
            string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            return symbol.Contains("XAU") ||
                   symbol.Contains("XAUEUR") ||
                   symbol.Contains("XAG") ||
                   symbol.Contains("GOLD") ||
                   symbol.Contains("SILVER");
        }

        private static bool IsCrypto(
            string symbol)
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

        private static bool IsIndex(
            string symbol)
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
