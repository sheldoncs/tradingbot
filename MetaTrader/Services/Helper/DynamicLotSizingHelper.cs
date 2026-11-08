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

        // GOLD uses this fixed profile lot rather than a monetary-risk calculator.
        // WARNING: the software risk manager cannot guarantee a maximum loss.
        public static double GoldFixedLot = 0.01;

        // =====================================================
        // DYNAMIC TRACKING
        // =====================================================
        public static double PeakEquity = 10000;

        private static readonly Dictionary<string, int> SymbolWins = new();
        private static readonly Dictionary<string, int> SymbolLosses = new();
        private static readonly object _statsSync = new object();

        // Wire these once at application startup using REAL MT5 account data.
        // The profit calculator must return the USD/account-currency LOSS for ONE LOT
        // from entryPrice to stopLoss; a nonpositive result rejects the trade.
        public static Func<string, double, double, double> LossPerLotInAccountCurrency { get; set; }
        public static Func<double> LiveAccountEquityProvider { get; set; }

        // Set true after wiring LossPerLotInAccountCurrency to MT5.
        // XRP CFD contract sizes and tick values vary by broker.
        // Default true: reject crypto trades until a verified MT5-backed calculator is wired.
        public static bool RequireBrokerLossCalculatorForCrypto = true;


        // =====================================================
        // SYMBOL PROFILES
        // =====================================================
        private static readonly Dictionary<string, SymbolLotProfile> Profiles = new(StringComparer.OrdinalIgnoreCase)
        {
            // -----------------------------
            // GOLD / METALS
            // -----------------------------
            ["XAUUSD.s"] = new SymbolLotProfile
            {
                BaseLot = 0.01,
                VolatilityFactor = 0.80,
                HighVolatilityCut = 0.70,
                LowVolatilityBoost = 1.00,
                MaxLot = 0.03,
                ContractSize = 100
            },

            ["XAUEUR.s"] = new SymbolLotProfile
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
            public double EstimatedSLRisk { get; set; }
            public double LossPerLot { get; set; }
            public string RiskSource { get; set; }
        }

        // =====================================================
        // REGISTER TRADE RESULT
        // =====================================================
        public static void RegisterTradeResult(
            string symbol,
            bool win)
        {
            symbol = NormalizeSymbol(symbol);

            lock (_statsSync)
            {
                if (!SymbolWins.ContainsKey(symbol)) SymbolWins[symbol] = 0;
                if (!SymbolLosses.ContainsKey(symbol)) SymbolLosses[symbol] = 0;
                if (win) SymbolWins[symbol]++;
                else SymbolLosses[symbol]++;
            }
        }

        // =====================================================
        // AUTO UPDATE RISK MODE
        // =====================================================
        public static void UpdateRiskMode(
            double currentEquity)
        {
            if (double.IsNaN(currentEquity) || double.IsInfinity(currentEquity) || currentEquity <= 0)
                return;

            int totalWins;
            int totalLosses;
            double drawdownPercent;
            lock (_statsSync)
            {
                PeakEquity = Math.Max(PeakEquity, currentEquity);
                drawdownPercent = ((PeakEquity - currentEquity) / PeakEquity) * 100.0;
                totalWins = SymbolWins.Values.Sum();
                totalLosses = SymbolLosses.Values.Sum();
            }

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
            double riskAmount = accountEquity * riskPercent / 100.0;
            double slDistance = Math.Abs(entryPrice - stopLoss);

            // GOLD: explicit fixed-lot execution mode for XAUUSD / XAUEUR.
            // No broker loss calculator, verification configuration or USD-budget
            // veto. The caller must still send a valid broker-side SL.
            if (IsGold(symbol))
            {
                if (string.IsNullOrWhiteSpace(symbol) ||
                    !double.IsFinite(accountEquity) || accountEquity <= 0 ||
                    !double.IsFinite(entryPrice) || entryPrice <= 0 ||
                    !double.IsFinite(stopLoss) || stopLoss <= 0 ||
                    slDistance <= 0 || !double.IsFinite(GoldFixedLot) ||
                    GoldFixedLot < profile.BaseLot || GoldFixedLot > profile.MaxLot)
                {
                    LogFile.Write($"[{symbol}] GOLD FIXED LOT REJECTED | invalid equity/price/SL or lot exceeds profile");
                    return new LotDecision
                    {
                        Lot = 0,
                        RiskSource = "REJECTED",
                        Reason = "Invalid GOLD fixed lot or price/stop inputs"
                    };
                }
                double fixedLot = Math.Round(Math.Floor((GoldFixedLot + 1e-10) / 0.01) * 0.01, 2);
                if (fixedLot < profile.BaseLot || fixedLot > profile.MaxLot)
                    return new LotDecision
                    {
                        Lot = 0,
                        RiskSource = "REJECTED",
                        Reason = "GOLD fixed lot outside profile"
                    };
                LogFile.Write($"[{symbol}] GOLD FIXED LOT APPROVED | lot={fixedLot:F2} | " +
                    "riskBudgetVeto=False | brokerLossCalculatorRequired=False");
                return new LotDecision
                {
                    Lot = fixedLot,
                    RiskPercent = 0,
                    RiskAmount = 0,
                    SlDistance = slDistance,
                    EstimatedSLRisk = double.NaN,
                    LossPerLot = double.NaN,
                    RiskSource = "FIXED_GOLD_PROFILE",
                    Reason = "GOLD fixed profile lot - no account-currency sizing gate"
                };
            }

            LotDecision Reject(string reason)
            {
                LogFile.Write($"[{symbol}] LOT SIZING BLOCKED | {reason}");
                return new LotDecision
                {
                    Lot = 0.0,
                    Reason = reason,
                    RiskPercent = riskPercent,
                    RiskAmount = riskAmount,
                    SlDistance = slDistance,
                    EstimatedSLRisk = 0.0,
                    LossPerLot = 0.0,
                    RiskSource = "REJECTED"
                };
            }

            if (string.IsNullOrWhiteSpace(symbol) ||
                double.IsNaN(accountEquity) || double.IsInfinity(accountEquity) || accountEquity <= 0 ||
                double.IsNaN(entryPrice) || double.IsInfinity(entryPrice) || entryPrice <= 0 ||
                double.IsNaN(stopLoss) || double.IsInfinity(stopLoss) || stopLoss <= 0 ||
                slDistance <= 0 || profile.BaseLot <= 0 || profile.MaxLot < profile.BaseLot ||
                double.IsNaN(riskAmount) || double.IsInfinity(riskAmount) || riskAmount <= 0 ||
                double.IsNaN(atrPercentile) || double.IsInfinity(atrPercentile) || atrPercentile < 0)
                return Reject("Invalid equity, price, stop, or profile");

            // A buy stop must be below entry; a sell stop must be above entry.
            // Direction is not part of this public method signature, so validate
            // the intended side at the OpenTrade caller before using the lot.
            if (IsCrypto(symbol) && RequireBrokerLossCalculatorForCrypto &&
                LossPerLotInAccountCurrency == null)
                return Reject("Broker loss calculator is required for crypto CFD risk");

            // For accurate currency conversion and CFD specifications, prefer
            // a REAL broker-backed loss calculator (for one standard lot).
            double lossPerLot;
            string riskSource;
            if (LossPerLotInAccountCurrency != null)
            {
                try
                {
                    lossPerLot = LossPerLotInAccountCurrency(symbol, entryPrice, stopLoss);
                    riskSource = "BROKER_DELEGATE";
                }
                catch (Exception ex) { return Reject("MT5 loss calculation failed: " + ex.Message); }
            }
            else if (symbol.EndsWith("USD", StringComparison.OrdinalIgnoreCase) &&
                     profile.ContractSize > 0)
            {
                // Approximation only for USD-quoted instruments on a USD account.
                // CFDs may still have different contract/tick economics: wire the provider.
                lossPerLot = slDistance * profile.ContractSize;
                riskSource = "PROFILE_APPROXIMATION";
            }
            else
            {
                return Reject("Broker account-currency loss calculator required for " + symbol);
            }

            if (double.IsNaN(lossPerLot) || double.IsInfinity(lossPerLot) || lossPerLot <= 0)
                return Reject("Invalid broker loss per lot");

            // Never round UP to the broker's minimum lot when that exceeds risk.
            if (lossPerLot * profile.BaseLot > riskAmount)
                return Reject($"Minimum lot {profile.BaseLot:F2} risks " +
                    $"{lossPerLot * profile.BaseLot:F2}, above budget {riskAmount:F2}");

            double lot = riskAmount / lossPerLot;
            double normalizedAtr = atrPercentile > 1 ? atrPercentile / 100.0 : atrPercentile;
            if (!double.IsNaN(normalizedAtr) && !double.IsInfinity(normalizedAtr))
            {
                if (normalizedAtr >= 0.85) lot *= profile.HighVolatilityCut;
                else if (normalizedAtr <= 0.30 && CurrentRiskMode != RiskMode.Recovery)
                    lot *= profile.LowVolatilityBoost;
            }

            if (isStrongMomentum && AllowMomentumLotBoost && CurrentRiskMode != RiskMode.Recovery)
                lot *= 1.10;

            lot *= profile.VolatilityFactor;
            lot = Math.Min(lot, profile.MaxLot);
            lot = Math.Min(lot, riskAmount / lossPerLot); // cap after ALL boosts
            if (double.IsNaN(lot) || double.IsInfinity(lot) || lot <= 0)
                return Reject("Invalid calculated lot");

            // Existing profiles use 0.01 lots as the step. Validate broker step at execution.
            lot = Math.Floor((lot + 1e-10) / 0.01) * 0.01;
            lot = Math.Round(lot, 2);
            if (lot < profile.BaseLot)
                return Reject("Calculated lot below minimum after volatility adjustments");

            double estimatedLoss = lot * lossPerLot;
            if (double.IsNaN(estimatedLoss) || double.IsInfinity(estimatedLoss) ||
                estimatedLoss <= 0.0)
                return Reject("Invalid estimated stop-loss risk");

            if (estimatedLoss > riskAmount + 1e-7)
                return Reject("Calculated loss exceeds trade budget");

            LogFile.Write($"[{symbol}] LOT SIZING DIAGNOSTIC | " +
                $"Lot={lot:F2} | Mode={CurrentRiskMode} | " +
                $"Equity={accountEquity:F2} | RiskPercent={riskPercent:F3}% | " +
                $"RiskBudget={riskAmount:F4} | " +
                $"EntryPrice={entryPrice:F8} | StopLoss={stopLoss:F8} | " +
                $"SLDistance={slDistance:F8} | " +
                $"ProfileContractSize={profile.ContractSize:F4} | " +
                $"RiskSource={riskSource} | " +
                $"LossPerLot={lossPerLot:F8} | EstimatedSLRisk={estimatedLoss:F8}");

            return new LotDecision
            {
                Lot = lot,
                RiskPercent = riskPercent,
                RiskAmount = riskAmount,
                SlDistance = slDistance,
                EstimatedSLRisk = estimatedLoss,
                LossPerLot = lossPerLot,
                RiskSource = riskSource,
                Reason = $"{symbol} SL-based dynamic sizing ({CurrentRiskMode})"
            };
        }

        // Existing five-argument callers remain compilable, but no stale hardcoded
        // account equity is permitted. Wire LiveAccountEquityProvider at startup.
        public static LotDecision Calculate(
            string symbol,
            double entryPrice,
            double stopLoss,
            double atrPercentile,
            bool isStrongMomentum)
        {
            double equity = 0;
            try { if (LiveAccountEquityProvider != null) equity = LiveAccountEquityProvider(); }
            catch (Exception ex) { LogFile.Write("Equity provider failed: " + ex.Message); }
            return Calculate(symbol, equity, entryPrice, stopLoss,
                atrPercentile, isStrongMomentum);
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

            int wins;
            int losses;
            lock (_statsSync)
            {
                SymbolWins.TryGetValue(symbol, out wins);
                SymbolLosses.TryGetValue(symbol, out losses);
            }

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