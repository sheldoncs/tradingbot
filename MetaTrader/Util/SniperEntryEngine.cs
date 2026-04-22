namespace MetaTrader.Util
{
    using MetaTrader.Data.Entities;
    using MetaTrader.Enums;
    using MetaTrader.helper;
    using MtApi5;
    using System;
    using System.Linq;
    using static System.Runtime.InteropServices.JavaScript.JSType;

    public class SniperEntryEngine
    {
        public static TradeAction CheckEntrySniper(
            double ask, double bid,
            double[] emaFast1H, double[] emaSlow1H,
            MqlRates[] rates1H,
            SymbolContext ctx, double atr, MtApi5Client _client)
        {



            // ---------------------------------------------
            // 🚀 VELOCITY ENTRY CHECK (FIRST PRIORITY)
            // ---------------------------------------------

            // ---------------------------------------------
            // 🚫 SHOCK EXECUTION BLOCK
            // ---------------------------------------------
            if (VolatilityGuard.IsShockBlocking(
                    ctx.Symbol,
                    rates1H[^1].time,
                    atr))
            {
                Console.WriteLine($"🚫 {ctx.Symbol} trade blocked due to shock regime.");
                LogFile.Write($"🚫 {ctx.Symbol} trade blocked due to shock regime.");
                TradingEventBus.Sniper(new SniperEvent()
                {
                    Regime = "ShockBlock",
                    ActionDescription = "Blocked due to recent shock.",
                    SniperAction = TradeAction.Hold.ToString()
                });

                return TradeAction.Hold;
            }

            var last = rates1H[^1];
            var prev = rates1H[^2];

            double mid = (ask + bid) / 2;

            double ema9 = emaFast1H[^1];
            double ema21 = emaSlow1H[^1];

            double ema9Prev = emaFast1H[^2];
            double ema21Prev = emaSlow1H[^2];

            double slope9 = ema9 - ema9Prev;
            double slope21 = ema21 - ema21Prev;

            double body = Math.Abs(last.close - last.open);
            double full = last.high - last.low;
            double upperWick = last.high - Math.Max(last.close, last.open);
            double lowerWick = Math.Min(last.close, last.open) - last.low;

            Console.WriteLine("---- SNIPER ENGINE START ----");
            Console.WriteLine($"Last Candle: O={last.open} C={last.close} H={last.high} L={last.low}");
            Console.WriteLine($"EMA9={ema9:F2}, EMA21={ema21:F2}, Slope9={slope9:F2}, Slope21={slope21:F2}");

            // -------------------------------------------------------------
            // 1. Trend Check
            // -------------------------------------------------------------
            bool trendUp = ema9 > ema21 && slope9 > 0 && slope21 > 0;
            bool trendDown = ema9 < ema21 && slope9 < 0 && slope21 < 0;

            if (!trendUp && !trendDown)
            {
                Console.WriteLine("❌ Sniper: No trend alignment.");
                TradingEventBus.Sniper(new SniperEvent()
                {
                     Regime = MarketRegime.Range.ToString(),
                     ActionDescription = "❌ Sniper: No trend alignment.",
                     SniperAction = TradeAction.Hold.ToString()

                });
                return TradeAction.Hold;
            }

            Console.WriteLine($"TrendUp={trendUp}, TrendDown={trendDown}");


            // -------------------------------------------------------------
            // 2. Breakout Strength
            // -------------------------------------------------------------
            bool bullishBreakout = last.close > prev.high;
            bool bearishBreakout = last.close < prev.low;

            bool strongBullBreak = bullishBreakout &&
                                   body > (full * 0.5) &&
                                   upperWick < (full * 0.25);

            bool strongBearBreak = bearishBreakout &&
                                   body > (full * 0.5) &&
                                   lowerWick < (full * 0.25);

            Console.WriteLine($"Breakouts → Bullish={bullishBreakout}, Bearish={bearishBreakout}");
            Console.WriteLine($"StrongBreakouts → StrongBull={strongBullBreak}, StrongBear={strongBearBreak}");

            if (trendUp && !strongBullBreak)
            {
                Console.WriteLine("❌ Sniper: Weak bullish breakout.");
                LogFile.Write("❌ Sniper: Weak bullish breakout.");
                TradingEventBus.Sniper(new SniperEvent()
                {
                    Regime = MarketRegime.BreakoutAttempt.ToString(),
                    ActionDescription = "❌ Sniper: Weak bullish breakout.",
                    SniperAction = TradeAction.Hold.ToString()
                });
                return TradeAction.Hold;
            }

            if (trendDown && !strongBearBreak)
            {
                Console.WriteLine("❌ Sniper: Weak bearish breakout.");
                LogFile.Write("❌ Sniper: Weak bearish breakout.");
                TradingEventBus.Sniper(new SniperEvent()
                {
                    Regime = MarketRegime.BreakoutAttempt.ToString(),
                    ActionDescription = "❌ Sniper: Weak bearish breakout.",
                    SniperAction = TradeAction.Hold.ToString()

                });
                return TradeAction.Hold;
            }

            // -------------------------------------------------------------
            // 3. Support / Resistance Check
            // -------------------------------------------------------------
            double recentHigh = prev.high;
            double recentLow = prev.low;

            if (trendUp && last.close < recentHigh)
            {
                Console.WriteLine("❌ Sniper: Buying into previous high (resistance).");
                LogFile.Write("❌ Sniper: Buying into previous high (resistance).");
                TradingEventBus.Sniper(new SniperEvent()
                {
                    Regime = MarketRegime.BullishBreakoutTest.ToString(),
                    ActionDescription = "❌ Sniper: Buying into previous high (resistance).",
                    SniperAction = TradeAction.Hold.ToString()

                });
                return TradeAction.Hold;
            }

            if (trendDown && last.close > recentLow)
            {
                Console.WriteLine("❌ Sniper: Selling into previous low (support).");
                LogFile.Write("❌ Sniper: Selling into previous low (support).");
                TradingEventBus.Sniper(new SniperEvent()
                {
                    Regime = MarketRegime.BearishBreakoutTest.ToString(),
                    ActionDescription = "❌ Sniper: Selling into previous low (support).",
                    SniperAction = TradeAction.Hold.ToString()

                });
                return TradeAction.Hold;
            }
            LogFile.Write("Support/Resistance check passed.");
            Console.WriteLine("Support/Resistance check passed.");

            TradingEventBus.Sniper(new SniperEvent()
            {
                Regime = MarketRegime.SupportResistanceClear.ToString(),
                ActionDescription = "Support/Resistance check passed.",
                SniperAction = TradeAction.Hold.ToString()

            });

            // -------------------------------------------------------------
            // 4. Pullback Logic
            // -------------------------------------------------------------
            bool pullbackToEma9 = last.low <= ema9 && last.close > ema9;
            bool pullbackToEma21 = last.low <= ema21;

            bool continuationCandleUp =
                body > Math.Abs(prev.close - prev.open) &&
                last.close > last.open &&
                upperWick < body * 0.4;

            bool continuationCandleDown =
                body > Math.Abs(prev.open - prev.close) &&
                last.close < last.open &&
                lowerWick < body * 0.4;

            Console.WriteLine($"Pullbacks → EMA9={pullbackToEma9}, EMA21={pullbackToEma21}");
            Console.WriteLine($"Continuation → Up={continuationCandleUp}, Down={continuationCandleDown}");
            if (ctx.LastImpulseHadAbsorption && !ctx.IsSecondImpulse)
            {
                Console.WriteLine($"⏳ {ctx.Symbol} waiting for second impulse after absorption");
                LogFile.Write($"⏳ {ctx.Symbol} waiting for second impulse after absorption");
            }

            if (LowVolatilityFilter.ShouldBlockTrade(rates1H, atr))
            {
                LogFile.Write("⛔ Entry blocked — low volatility / candle compression");
                return TradeAction.Hold;
            }

            bool impulseOk = ImpulseQualityFilter.IsImpulseStrongEnough(
                        rates1H,
                        atr,
                        emaFast1H[^1],
                        emaSlow1H[^1]
                    );

            if (!impulseOk)
            {
                Console.WriteLine("🚫 Entry blocked — weak impulse / low energy");
                LogFile.Write("🚫 Entry blocked — weak impulse / low energy");
                return TradeAction.Hold;
            }

            // -------------------------------------------------------------
            // 5. Sniper Entry Decisions
            // -------------------------------------------------------------
            if (trendUp)
            {
                if (strongBullBreak && continuationCandleUp &&
                         (!ctx.LastImpulseHadAbsorption || ctx.IsSecondImpulse))
                {

                    Console.WriteLine(
                        ctx.IsSecondImpulse
                        ? "🎯 SNIPER BUY: Breakout + continuation (2nd impulse)."
                        : "🎯 SNIPER BUY: Breakout + continuation."
                    );
                    Console.WriteLine("🎯 SNIPER BUY: Breakout + continuation.");
                    TradingEventBus.Sniper(new SniperEvent()
                    {
                        Regime = MarketRegime.Transition.ToString(),
                        ActionDescription = ctx.IsSecondImpulse
                            ? "🎯 SNIPER BUY: Breakout + continuation (2nd impulse)."
                            : "🎯 SNIPER BUY: Breakout + continuation.",
                        SniperAction = TradeAction.EnterBuy.ToString()
                    });
                    return TradeAction.EnterBuy;
                }

                if (pullbackToEma9 && continuationCandleUp)
                {
                    Console.WriteLine("🎯 SNIPER BUY: EMA9 pullback continuation.");
                    TradingEventBus.Sniper(new SniperEvent()
                    {
                        Regime = MarketRegime.Transition.ToString(),
                        ActionDescription = "🎯 SNIPER BUY: EMA9 pullback continuation.",
                        SniperAction = TradeAction.EnterBuy.ToString()
                    });
                    return TradeAction.EnterBuy;
                }

                if (pullbackToEma21 && continuationCandleUp)
                {
                    Console.WriteLine("🎯 SNIPER BUY: EMA21 deep continuation.");
                    TradingEventBus.Sniper(new SniperEvent()
                    {
                        Regime = MarketRegime.Transition.ToString(),
                        ActionDescription = "🎯 SNIPER BUY: EMA21 deep continuation.",
                        SniperAction = TradeAction.EnterBuy.ToString()
                    });
                    return TradeAction.EnterBuy;
                }
            }

            if (trendDown)
            {
                if (strongBearBreak && continuationCandleDown &&
                   (!ctx.LastImpulseHadAbsorption || ctx.IsSecondImpulse))
                {
                    Console.WriteLine("🎯 SNIPER SELL: Breakout + continuation.");
                    Console.WriteLine(
                        ctx.IsSecondImpulse
                        ? "🎯 SNIPER SELL: Breakout + continuation (2nd impulse)."
                        : "🎯 SNIPER SELL: Breakout + continuation."
                    );
                    TradingEventBus.Sniper(new SniperEvent()
                    {
                        Regime = MarketRegime.Transition.ToString(),
                        ActionDescription = ctx.IsSecondImpulse
                            ? "🎯 SNIPER SELL: Breakout + continuation (2nd impulse)."
                            : "🎯 SNIPER SELL: Breakout + continuation.",
                        SniperAction = TradeAction.EnterSell.ToString()
                    });
                    return TradeAction.EnterSell;
                }

                if (pullbackToEma9 && continuationCandleDown )
                {
                    Console.WriteLine("🎯 SNIPER SELL: EMA9 pullback continuation.");
                    TradingEventBus.Sniper(new SniperEvent()
                    {
                        Regime = MarketRegime.Transition.ToString(),
                        ActionDescription = "🎯 SNIPER SELL: EMA9 pullback continuation.",
                        SniperAction = TradeAction.EnterSell.ToString()
                    });
                    return TradeAction.EnterSell;
                }

                if (pullbackToEma21 && continuationCandleDown)
                {
                    Console.WriteLine("🎯 SNIPER SELL: EMA21 deep continuation.");
                    TradingEventBus.Sniper(new SniperEvent()
                    {
                        Regime = MarketRegime.Transition.ToString(),
                        ActionDescription = "🎯 SNIPER SELL: EMA21 deep continuation.",
                        SniperAction = TradeAction.EnterSell.ToString()
                    });
                    return TradeAction.EnterSell;
                }
            }

            Console.WriteLine("❌ Sniper: No qualifying setup found.");
            TradingEventBus.Sniper(new SniperEvent()
            {
                Regime = MarketRegime.NoQualifyingSetupFound.ToString(),
                ActionDescription = "❌ Sniper: No qualifying setup found.",
                SniperAction = TradeAction.Hold.ToString()
            });
            Console.WriteLine("---- SNIPER ENGINE END ----");

            return TradeAction.Hold;
        }

    }


}
