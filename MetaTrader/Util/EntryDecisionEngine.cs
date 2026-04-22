using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.helper;
using MetaTrader.Services;
using MetaTrader.Services.SMC.MetaTrader.Services;
using MtApi5;
using Serilog;
using System;
using System.Threading;

namespace MetaTrader.Util
{
    public static class EntryDecisionEngine
    {
        private static TradeAction EmitDecision(
            TradeAction action,
            DecisionCategory category,
            DecisionReason reason,
            string message,
            string symbol,
            double momentum,
            bool earlyMomentum,
            bool emaCompressed,
            MarketRegime regime
        )
        {
            TradingEventBus.Decision(new DecisionEvent
            {
                Symbol = symbol,
                Action = action.ToString(),
                Category = category.ToString(),
                Reason = reason.ToString(),
                Message = message,
                Momentum = momentum,
                EarlyMomentum = earlyMomentum,
                EmaCompressed = emaCompressed,
                Regime = regime.ToString(),
                Time = DateTime.UtcNow
            });

            return action;
        }


        private static bool _entryLocked = false;
        private static DateTime _lastEntryCandleTime = DateTime.MinValue;

        //-----------------------------------------------------------------
        // Convert FilterEngine → TradeAction
        //-----------------------------------------------------------------
        private static TradeAction ConvertFilterResult(FilterResult f)
        {
            if (f == null) return TradeAction.Hold;
            if (f.Blocked) return TradeAction.Hold;
            return TradeAction.Allow;
        }

        public static TradeAction Evaluate(
            double ask,
            double bid,
            MqlRates[] rates1H,
            double[] emaFast1H,
            double[] emaSlow1H,
            double atrPoints,
            double atrRaw,
            double midPrice,
            bool h1Bull,
            bool h1Bear,
            bool bullishBreakout,
            bool bearishBreakout,
            bool impulseCandle,
            bool h4Weak,
            bool h4Reversal,
            double h4Change,
            double h4Momentum,
            bool? isBuy,
            string SYMBOL,
            TrendContext trend,
            SymbolContext ctx
         )
        {
            // --------------------------------------------------------------
            // ⛔ ENTRY ENGINE ONLY HANDLES NEW ENTRIES
            // --------------------------------------------------------------
            if (isBuy != null)
                return TradeAction.Hold;

            // --------------------------------------------------------------
            // 🧠 Candle refs
            // --------------------------------------------------------------
            var last = rates1H[^1];
            var prev = rates1H[^2];

            // --------------------------------------------------------------
            // 📉 EMA compression
            // --------------------------------------------------------------
            double ema9 = emaFast1H[^1];
            double ema21 = emaSlow1H[^1];

            double slope9 = emaFast1H[^1] - emaFast1H[^2];
            double slope21 = emaSlow1H[^1] - emaSlow1H[^2];

            bool emaCompressed = MarketStateEngine.IsEmaCompressed(
                ema9, ema21, slope9, slope21, atrRaw
            );

            // --------------------------------------------------------------
            // 🔥 Hybrid momentum
            // --------------------------------------------------------------
            var hybrid = HybridMomentumEngine.Calculate(
                last,
                prev,
                rates1H,
                emaFast1H,
                emaSlow1H,
                atrRaw,
                false
            );

            double momentumScore = hybrid.Hybrid;

            // --------------------------------------------------------------
            // 🚀 Directional early momentum (FIXED)
            // --------------------------------------------------------------
            bool earlyMomentum =
                hybrid.Strong &&
                (hybrid.Breakout || hybrid.Impulse);

            bool earlyBull = earlyMomentum && last.close > last.open;
            bool earlyBear = earlyMomentum && last.close < last.open;

            // --------------------------------------------------------------
            // 📊 Market regime
            // --------------------------------------------------------------
            var regime = MarketRegimeEngine.Detect(
                rates1H,
                atrRaw,
                emaFast1H,
                emaSlow1H,
                hybrid
            );

            // --------------------------------------------------------------
            // ⛔ COMPRESSION BLOCK (FIXED)
            // Allow ONLY breakout from compression
            // --------------------------------------------------------------
            if (regime == MarketRegime.Compression && !hybrid.CompressionBreakout)
            {
                EmitDecision(
                    TradeAction.Hold,
                    DecisionCategory.Regime,
                    DecisionReason.Compression,
                    "Blocked: Compression regime",
                    SYMBOL,
                    momentumScore,
                    earlyMomentum,
                    emaCompressed,
                    regime
                );

                TradingEventBus.Sniper(new SniperEvent()
                {
                    Regime = MarketRegime.Compression.ToString(),
                    ActionDescription = "Compression regime.",
                    SniperAction = TradeAction.Hold.ToString()

                });

                return TradeAction.Hold;
            }

            // --------------------------------------------------------------
            // ⛔ EXHAUSTION
            // --------------------------------------------------------------
            if (regime == MarketRegime.Exhaustion)
            {
                EmitDecision(
                    TradeAction.Hold,
                    DecisionCategory.Exhaustion,
                    DecisionReason.Exhaustion,
                    "Blocked: Exhaustion regime",
                    SYMBOL,
                    momentumScore,
                    earlyMomentum,
                    emaCompressed,
                    regime
                );

                TradingEventBus.Sniper(new SniperEvent()
                {
                    Regime = MarketRegime.Exhaustion.ToString(),
                    ActionDescription = "Exhaustion regime.",
                    SniperAction = TradeAction.Hold.ToString()

                });

                return TradeAction.Hold;
            }

            // --------------------------------------------------------------
            // ⏸ MOMENTUM TOO WEAK
            // --------------------------------------------------------------
            if (!hybrid.Strong)
            {
                EmitDecision(
                    TradeAction.H4Weak,
                    DecisionCategory.Momentum,
                    DecisionReason.MomentumWeak,
                    "Blocked: Weak Momentum",
                    SYMBOL,
                    momentumScore,
                    earlyMomentum,
                    emaCompressed,
                    regime
                );

                return TradeAction.Hold;
            }
            // --------------------------------------------------------------
            // 📍 Swing levels
            // --------------------------------------------------------------
            double swingHigh = SwingLevelEngine.GetRecentSwingHigh(rates1H);
            double swingLow = SwingLevelEngine.GetRecentSwingLow(rates1H);

            // --------------------------------------------------------------
            // 🧠 Acceptance
            // --------------------------------------------------------------
            bool bullishAcceptance =
                AcceptanceEngine.IsBullishAcceptance(prev, last, swingHigh, atrRaw);

            bool bearishAcceptance =
                AcceptanceEngine.IsBearishAcceptance(prev, last, swingLow, atrRaw);

            // --------------------------------------------------------------
            // 💧 Liquidity grab logic (FIXED)
            // --------------------------------------------------------------
            if (!h4Weak)
            {
                bool bearishGrab =
                    LiquidityGrabEngine.IsBearishLiquidityGrab(last, swingHigh, hybrid.Strong);

                bool bullishGrab =
                    LiquidityGrabEngine.IsBullishLiquidityGrab(last, swingLow, hybrid.Strong);

                if (bearishGrab)
                {
                    if (bearishAcceptance)
                        return TradeAction.EnterSell;

                    return TradeAction.Hold;
                }

                if (bullishGrab)
                {
                    if (bullishAcceptance)
                        return TradeAction.EnterBuy;

                    return TradeAction.Hold;
                }
            }


            if (LowVolatilityFilter.ShouldBlockTrade(rates1H, atrRaw))
            {
                EmitDecision(
                        TradeAction.Hold,
                        DecisionCategory.Structure,
                        DecisionReason.EntryBlocked,
                        "low volatility / candle compression",
                        SYMBOL,
                        momentumScore,
                        earlyMomentum,
                        emaCompressed,
                        regime
                    );

                LogFile.Write("⛔ Entry blocked — low volatility / candle compression");
                return TradeAction.Hold;
            }

            bool impulseOk = ImpulseQualityFilter.IsImpulseStrongEnough(
                        rates1H,
                        atrRaw,
                        emaFast1H[^1],
                        emaSlow1H[^1]
                    );

            if (!impulseOk)
            {
               
                EmitDecision(
                        TradeAction.Hold,
                        DecisionCategory.Structure,
                        DecisionReason.EntryBlocked,
                        "weak impulse / low energy",
                        SYMBOL,
                        momentumScore,
                        earlyMomentum,
                        emaCompressed,
                        regime
                    );

                Console.WriteLine("🚫 Entry blocked — weak impulse / low energy");
                LogFile.Write("🚫 Entry blocked — weak impulse / low energy");
                return TradeAction.Hold;
            }
            // --------------------------------------------------------------
            // 🚀 AGGRESSIVE FALLBACK ENTRIES (CLEAN + SAFE)
            // --------------------------------------------------------------
            if ((h1Bull || earlyBull) &&
                !h4Reversal &&
                (bullishBreakout || impulseCandle || hybrid.Breakout || hybrid.Impulse))
            {
                _entryLocked = true;
                _lastEntryCandleTime = last.time;

                EmitDecision(
                    TradeAction.EnterBuy,
                    DecisionCategory.Fallback,
                    DecisionReason.AggressiveFallbackBuy,
                    "BUY: Early momentum / breakout",
                    SYMBOL,
                    momentumScore,
                    earlyMomentum,
                    emaCompressed,
                    regime
                );

                return TradeAction.EnterBuy;
            }

            if ((h1Bear || earlyBear) &&
                !h4Reversal &&
                (bearishBreakout || impulseCandle || hybrid.Breakout || hybrid.Impulse))
            {
                _entryLocked = true;
                _lastEntryCandleTime = last.time;

                EmitDecision(
                    TradeAction.EnterSell,
                    DecisionCategory.Fallback,
                    DecisionReason.AggressiveFallbackSell,
                    "SELL: Early momentum / breakout",
                    SYMBOL,
                    momentumScore,
                    earlyMomentum,
                    emaCompressed,
                    regime
                );

                return TradeAction.EnterSell;
            }

            // --------------------------------------------------------------
            // DEFAULT
            // --------------------------------------------------------------
            EmitDecision(
                    TradeAction.WeakMom,
                    DecisionCategory.Momentum,
                    DecisionReason.MomentumWeak,
                    "Weak: momentum",
                    SYMBOL,
                    momentumScore,
                    earlyMomentum,
                    emaCompressed,
                    regime
                );
            return TradeAction.Hold;
        }


        public static void ResetEntryLock()
        {
            _entryLocked = false;
        }

    }



}
