using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.Enums.MetaTrader.Enums;
using MetaTrader.helper;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Collections.Generic;
using System.Linq;
using static MetaTrader.Services.HybridMomentumEngine;

namespace MetaTrader.Services.Helper
{
    public class MarketStateCoordinator
    {
        private readonly MtApi5Client _client;
        private readonly IEnumerable<string> _symbols;

        private readonly TradeCoordinator _cryptoCoordinator;
        private readonly TradeCoordinator _metalsCoordinator;
        private readonly TradeCoordinator _fxCoordinator;
        private readonly TradeCoordinator _currencyCoordinator;

        private readonly Dictionary<string, SymbolContext> _contexts;

        private bool _previousCryptoWindow;
        private class SymbolFreshness
        {
            public double DistanceFromEma9Atr;
            public int ExpansionCandles;
            public bool IsLateExpansion;
            public bool IsTooExtended;
            public DateTime LastUpdatedUtc;
        }

        private readonly Dictionary<string, SymbolFreshness> _freshness = new();

        public MarketStateCoordinator(
            MtApi5Client client,
            IEnumerable<string> symbols,
            TradeCoordinator cryptoCoordinator,
            TradeCoordinator metalsCoordinator,
            TradeCoordinator fxCoordinator,
            TradeCoordinator currencyCoordinator,
            Dictionary<string, SymbolContext> contexts
            )
        {
            _fxCoordinator = fxCoordinator;
            _cryptoCoordinator = cryptoCoordinator;
            _metalsCoordinator = metalsCoordinator;
            _currencyCoordinator = currencyCoordinator;
            _client = client;
            _symbols = symbols;
            _contexts = contexts;

        }

        private TradeCoordinator GetCoordinator(string symbol)
        {


            var universe = GetUniverse(symbol);

            return universe switch
            {
                MarketUniverse.Crypto => _cryptoCoordinator,
                MarketUniverse.FX => _fxCoordinator,
                MarketUniverse.Metals => _metalsCoordinator,
                MarketUniverse.Currency => _currencyCoordinator,
                _ => throw new InvalidOperationException($"Unknown universe for {symbol}")
            };
        }

        public void UpdateSingleSymbol(string symbol)
        {
            UpdateSymbolState(symbol);
        }
        private static readonly HashSet<string> FxSymbols = new()
        {
                "EURUSD","GBPUSD","USDJPY","AUDUSD","USDCAD","USDCHF","NZDUSD"
        };

        private static readonly HashSet<string> CryptoSymbols = new()
        {
            "BNBUSD",
            "SOLUSD",
            "XRPUSD",
            "ETHUSD"
        };

        private static readonly HashSet<string> GoldSymbols = new()
        {
            "XAUUSD",
            "XAUEUR"
        };

        private MarketUniverse GetUniverse(string symbol)
        {
            var baseSymbol = NormalizeSymbol(symbol);
            if (baseSymbol.StartsWith("XAU") || baseSymbol.StartsWith("XAG"))
                return MarketUniverse.Metals;

            if (FxSymbols.Contains(baseSymbol))
                return MarketUniverse.FX;

            if (GoldSymbols.Contains(symbol))
                return MarketUniverse.Currency;


            return MarketUniverse.Crypto;

        }

        private bool IsAtrCool(SymbolContext ctx, string symbol, double atrRaw)
        {
            var u = GetUniverse(symbol);

            // Capture peak if missing
            if (ctx.PeakAtr <= 0)
                ctx.PeakAtr = atrRaw;

            // Universe-specific thresholds
            double peakFactor = (u == MarketUniverse.Metals) ? 0.90 : 0.85;

            bool coolByPeak = atrRaw < ctx.PeakAtr * peakFactor;

            // Metals: allow "cooling trend" as alternative
            bool coolByTrend = (u == MarketUniverse.Metals) && ctx.AtrDownCount >= 2;

            return coolByPeak || coolByTrend;
        }

        private static string NormalizeSymbol(string symbol)
        {
            return symbol
                .Replace(".s", "")
                .Replace("_i", "")
                .Replace("m", "")
                .ToUpperInvariant();
        }

        public string ScanAndSelectSymbol()
        {
            DateTime nowAst = DateTime.UtcNow.AddHours(-4); // AST conversion

            bool cryptoWindow = TradingUniverseSelector.IsCryptoWindow(nowAst);

            foreach (var symbol in _symbols)
            {
                bool isGold = GoldSymbols.Contains(symbol);

                if (!isGold)
                    continue;

                UpdateSymbolState(symbol);
            }

            return ChooseBestSymbol();
        }

        // ----------------------------
        // SYMBOL STATE UPDATE
        // ----------------------------
        private void UpdateSymbolState(string marketSymbol)
        {
            var ctx = GetContext(marketSymbol);
            //if (TradingConfig.Mode == TradingMode.XauUsdOnly)
            _client.CopyRates(marketSymbol, ENUM_TIMEFRAMES.PERIOD_H1, 1, 120, out MqlRates[] rates1H);
            if (rates1H == null || rates1H.Length < 3)
                return;

            double atrRaw = IndicatorEngine.CalculateATR(rates1H, 14);
            double[] emaFast1H = IndicatorEngine.CalculateEMA(rates1H, 9);
            double[] emaSlow1H = IndicatorEngine.CalculateEMA(rates1H, 21);

            var last = rates1H[^1];
            var prev = rates1H[^2];

            MomentumResult momentum = HybridMomentumEngine.Calculate(
                last,
                prev,
                rates1H,
                emaFast1H,
                emaSlow1H,
                atrRaw,
                true
            );

            UpdateMarketState(marketSymbol, ctx, momentum, emaFast1H, emaSlow1H, atrRaw, rates1H);
        }

        private void UpdateFreshness(
            string symbol,
            SymbolContext ctx,
            double[] emaFast1H,
            double atrRaw,
            MqlRates[] rates1H)
        {
            if (emaFast1H == null ||
                emaFast1H.Length < 3 ||
                rates1H == null ||
                rates1H.Length < 6 ||
                atrRaw <= 0)
            {
                return;
            }

            string normalized = NormalizeSymbol(symbol);

            double ema9 = emaFast1H[^1];
            double close = rates1H[^1].close;

            double distanceFromEma9Atr = Math.Abs(close - ema9) / atrRaw;

            MarketUniverse universe = GetUniverse(symbol);

            double maxFreshDistance =
                universe == MarketUniverse.Metals ? 1.25 :
                universe == MarketUniverse.Crypto ? 1.45 :
                universe == MarketUniverse.FX ? 1.10 :
                1.20;

            int maxExpansionCandles =
                universe == MarketUniverse.Metals ? 4 :
                universe == MarketUniverse.Crypto ? 5 :
                universe == MarketUniverse.FX ? 3 :
                4;

            int liveExpansionCandles = CountLiveExpansionCandles(
                symbol,
                emaFast1H,
                atrRaw,
                rates1H
            );

            bool tooExtended = distanceFromEma9Atr > maxFreshDistance;

            bool lateExpansion =
                liveExpansionCandles >= maxExpansionCandles ||
                tooExtended;

            _freshness[normalized] = new SymbolFreshness
            {
                DistanceFromEma9Atr = distanceFromEma9Atr,
                ExpansionCandles = liveExpansionCandles,
                IsLateExpansion = lateExpansion,
                IsTooExtended = tooExtended,
                LastUpdatedUtc = DateTime.UtcNow
            };
        }

        private int CountLiveExpansionCandles(
            string symbol,
            double[] emaFast1H,
            double atrRaw,
            MqlRates[] rates1H)
        {
            if (emaFast1H == null ||
                rates1H == null ||
                emaFast1H.Length < 3 ||
                rates1H.Length < 3 ||
                atrRaw <= 0)
            {
                return 0;
            }

            int count = 0;

            int maxLookback = Math.Min(6, Math.Min(emaFast1H.Length, rates1H.Length));

            for (int i = 1; i <= maxLookback; i++)
            {
                var candle = rates1H[^i];
                double ema9 = emaFast1H[^i];

                double body = Math.Abs(candle.close - candle.open);
                double range = candle.high - candle.low;
                double distanceFromEmaAtr = Math.Abs(candle.close - ema9) / atrRaw;

                bool strongBody = range > 0 && body / range >= 0.55;
                bool awayFromEma = distanceFromEmaAtr >= 0.65;
                bool expansionRange = range >= atrRaw * 0.75;

                if (strongBody && awayFromEma && expansionRange)
                    count++;
                else
                    break;
            }

            return count;
        }

        private bool IsFreshEnoughToArm(string symbol, SymbolContext ctx)
        {
            string normalized = NormalizeSymbol(symbol);

            if (!_freshness.TryGetValue(normalized, out var f))
                return true;

            if (f.IsLateExpansion)
                return false;

            if (f.ExpansionCandles >= 5)
                return false;

            if (f.IsTooExtended)
                return false;

            return true;
        }

        private string GetFreshnessSummary(string symbol)
        {
            string normalized = NormalizeSymbol(symbol);

            if (!_freshness.TryGetValue(normalized, out var f))
                return "Freshness: unavailable";

            return
                $"Freshness: distEMA9Atr={f.DistanceFromEma9Atr:F2}, " +
                $"expCandles={f.ExpansionCandles}, " +
                $"late={f.IsLateExpansion}, " +
                $"tooExtended={f.IsTooExtended}";
        }

        private static double CalculateAtrPercentile(
            double currentAtr,
            Queue<double> history)
        {
            if (history == null || history.Count < 20)
                return 50.0;

            int belowOrEqual = history.Count(x => x <= currentAtr);

            double percentile =
                ((double)belowOrEqual / history.Count) * 100.0;

            // safety clamp
            percentile = Math.Max(1.0, percentile);
            percentile = Math.Min(99.0, percentile);

            return percentile;
        }

        // ----------------------------
        // MARKET STATE MACHINE
        // ----------------------------

        public DateTime LastProcessedCandleTime { get; set; } = DateTime.MinValue;
        private TradeCoordinator coordinator;
        private void UpdateMarketState(
            string SYMBOL,
            SymbolContext ctx,
            MomentumResult momentum,
            double[] emaFast1H,
            double[] emaSlow1H,
            double atrRaw,
            MqlRates[] rates1H
        )
        {

            if (SYMBOL == "XAUUSD")
            {
                Console.WriteLine("");
            }

            if (TradingConfig.Mode == TradingMode.XauUsdOnly &&
                  SYMBOL != "XAUUSD.s")
            {
                return;
            }
            if (TradingConfig.Mode == TradingMode.XauUsdOnly)
                Console.WriteLine($"Symbol={ctx.Symbol} previous State={ctx.PreviousState} current State={ctx.State} pullback sufficient={ctx.PullbackSufficient} cooldown={ctx.AtrCooldown}");

            coordinator = GetCoordinator(SYMBOL);

            ctx.ArmedHybrid = 0.0;

            DateTime currentCandleTime = rates1H[^1].time;
            VolatilityGuard.UpdateShockStatus(SYMBOL, rates1H, atrRaw);

            // 🔒 GUARD: only process on candle close
            if (ctx.LastProcessedCandleTime == currentCandleTime)
                return;

            ctx.LastProcessedCandleTime = currentCandleTime;
            if (ctx.InRecoveryMode)
            {
                ctx.ClosedCandlesSinceRecovery++;

                if (ctx.ClosedCandlesSinceRecovery >= ctx.RecoveryMinClosedCandles)
                {
                    ctx.InRecoveryMode = false;
                }
                else
                {
                    return; // block trading during recovery buffer
                }
            }


            // -----------------------------
            // 📉 ATR TREND TRACKING (per candle close)
            // -----------------------------
            if (ctx.PrevAtr > 0 && atrRaw < ctx.PrevAtr)
                ctx.AtrDownCount++;
            else
                ctx.AtrDownCount = 0;

            ctx.PrevAtr = atrRaw;
            // -----------------------------
            // 📊 ATR Percentile Tracking
            // -----------------------------

            ctx.AtrHistory.Enqueue(atrRaw);

            if (ctx.AtrHistory.Count > 50)   // 50-candle rolling window
                ctx.AtrHistory.Dequeue();

            ctx.AtrPercentile = CalculateAtrPercentile(atrRaw, ctx.AtrHistory);


            var last = rates1H[^1];

            // ---------------------------------
            // 🧠 IMPULSE WITH ABSORPTION DETECTION (XAU-safe)
            // ---------------------------------
            double full = last.high - last.low;
            double body = Math.Abs(last.open - last.close);
            double lowerWick = Math.Min(last.open, last.close) - last.low;

            // treat gold differently (it wicks more)
            double absorptionThreshold =
                GetUniverse(SYMBOL) == MarketUniverse.Metals ? 0.30 : 0.25;

            bool impulseWithAbsorption =
                body > full * 0.5 &&
                lowerWick > full * absorptionThreshold &&
                full > atrRaw * 0.8;

            if (impulseWithAbsorption)
            {
                ctx.LastImpulseHadAbsorption = true;
                ctx.CandlesSinceImpulse = 0;

                if (GetUniverse(SYMBOL) == MarketUniverse.Metals)
                {
                    Console.WriteLine(
                        $"⚠️ {SYMBOL} Impulse with absorption detected — waiting for second impulse");
                }
            }
            else
            {
                ctx.CandlesSinceImpulse++;
            }

            // -------------------------------------------------
            // 🔄 FAILED EXPANSION RESET (NON-CHOSEN SYMBOLS)
            // -------------------------------------------------
            if (ctx.State == MarketState.FailedExpansion)
            {
                Console.WriteLine($"Failed Expansion for symbol {ctx.Symbol}");
                return;
            }

            bool emaCompressed = IsEmaCompressed(emaFast1H, emaSlow1H, atrRaw);

            double dynamicThreshold = GetDynamicThreshold(
                atrRaw,
                emaCompressed,
                ctx.State,
                momentum
            );

            // ---------------------------------
            // 🔥 STRONG IMPULSE OVERRIDES COMPRESSION
            // ---------------------------------
            if (ctx.State == MarketState.Neutral &&
                momentum.Strong &&
                momentum.Hybrid > dynamicThreshold)
            {
                ctx.TransitionTo(MarketState.Impulse);
                ctx.IncrementExpansion();

                ctx.PeakAtr = atrRaw;
                ctx.PeakAtrCaptured = true;

                Console.WriteLine(
                    $"🔥 {SYMBOL} TRANSITION → {ctx.State} | Exp={ctx.ExpansionCandles}"
                );

                return;
            }

            // ---------------------------------
            // COMPRESSION (ONLY if momentum weak)
            // ---------------------------------
            if (ctx.State == MarketState.Neutral &&
                emaCompressed &&
                !momentum.Strong)
            {
                ctx.LastImpulseHadAbsorption = false;
                ctx.CandlesSinceImpulse = 0;

                ctx.TransitionTo(MarketState.Compression);
                return;
            }

            // ---------------------------------
            // FRESH EXPANSION
            // ---------------------------------
            if (momentum.Breakout &&
                ctx.State == MarketState.Compression)
            {
                ctx.TransitionTo(MarketState.Impulse);
                ctx.IncrementExpansion();

                if (ctx.ActiveOrderBlock == null)
                {
                    ctx.ActiveOrderBlock = OrderBlockEngine.DetectImpulseOrderBlock(
                        rates1H,
                        isBullish: emaFast1H[^1] > emaSlow1H[^1]
                    );

                    ctx.OrderBlockCreatedAt = rates1H[^2].time;
                }

                return;
            }

            // ---------------------------------
            // EXPANSION → LATE → EXHAUSTION
            // ---------------------------------
            if (ctx.State == MarketState.Impulse ||
                ctx.State == MarketState.Continuation)
            {
                ctx.IncrementExpansion();

                if (ctx.ExpansionCandles == 2)
                    ctx.TransitionTo(MarketState.Continuation);

                // ---------------------------------
                // 🧠 DETECT LATE EXPANSION (NEW)
                // ---------------------------------
                double ema9 = emaFast1H[^1];
                double ema21 = emaSlow1H[^1];

                double emaSpread = Math.Abs(ema9 - ema21);
                double atrMultiple = emaSpread / atrRaw;

                // slope weakening
                double slopeNow = emaFast1H[^1] - emaFast1H[^2];
                double slopePrev = emaFast1H[^2] - emaFast1H[^3];

                bool slopeWeakening = Math.Abs(slopeNow) < Math.Abs(slopePrev);

                // extension
                double distanceFromEMA = Math.Abs(rates1H[^1].close - ema9);
                bool extended = distanceFromEMA > atrRaw * 1.5;

                bool strongTrend = atrMultiple > 1.2;

                if (strongTrend && (slopeWeakening || extended))
                {
                    if (ctx.State != MarketState.LateExpansion)
                    {
                        Console.WriteLine($"⚠️ {SYMBOL} → LateExpansion");
                        //ctx.TransitionTo(MarketState.LateExpansion);
                    }
                }


                if (ctx.IsLateExpansion)
                    ctx.TransitionTo(MarketState.Exhaustion);

                return;
            }

            // ---------------------------------
            // 🟢 EXHAUSTION → PULLBACK (RECOVERY)
            // ---------------------------------
            if (ctx.State == MarketState.Exhaustion)
            {
                var lastClosed = rates1H[^2];
                double ema9 = emaFast1H[^1];
                double midPrice = (lastClosed.open + lastClosed.close) / 2.0;

                bool emaReclaimed =
                    lastClosed.low <= ema9 &&
                    lastClosed.high >= ema9;   // price interacted with EMA again

                bool volatilityCooled =
                    Math.Abs(midPrice - ema9) / atrRaw < 0.35;

                bool timeDecay =
                    ctx.ExpansionCandles >= 2; // simple decay (2 candles max)

                if ((emaReclaimed && volatilityCooled) || timeDecay)
                {
                    Console.WriteLine($"🟢 {SYMBOL} EXHAUSTION RECOVERED → Pullback");

                    ctx.TransitionTo(MarketState.Pullback);
                    ctx.ResetExpansion();
                    ctx.ResetPullback();

                    ctx.PeakAtr = 0.0;
                    ctx.PeakAtrCaptured = false;

                    return;
                }
            }

            // ---------------------------------
            // 🚫 FAILED EXPANSION DETECTION
            // ---------------------------------
            if (ExpansionFailureEngine.IsFailedExpansion(
                    ctx.State,
                    emaFast1H,
                    emaSlow1H
                    ))
            {

                ctx.LastImpulseHadAbsorption = false;
                ctx.CandlesSinceImpulse = 0;

                ctx.TransitionTo(MarketState.FailedExpansion);
                coordinator.Clear();
                ctx.ClearListOfStates();
                ctx.ResetExpansion();
                ctx.ResetPullback();

                ctx.LastImpulseHadAbsorption = false;
                ctx.CandlesSinceImpulse = 0;

                ctx.ActiveOrderBlock = null;
                ctx.OrderBlockCreatedAt = null;

                // 🔥 RESET ATR CONTEXT
                ctx.PeakAtr = 0.0;
                ctx.PeakAtrCaptured = false;

                Console.WriteLine(
                    $"❌ {ctx.Symbol} FAILED EXPANSION — structure invalidated");

                return;
            }

            // ---------------------------------
            // EXHAUSTION → PULLBACK (MOMENTUM COOLING)
            // ---------------------------------
            bool cooled =
                    !momentum.Strong &&
                    momentum.Hybrid < 1.0 &&
                    !momentum.Breakout &&
                    !momentum.Impulse;

            if (cooled && ctx.State == MarketState.Armed)
            {
                // drop ONE level onlyne
                ctx.TransitionTo(MarketState.Pullback);
                ctx.ResetPullback();
                ctx.MarketCoolingState();
                SymbolPriorityEngine.Release(ctx.Symbol);
            }
            /*
            if (cooled && ctx.State != MarketState.Pullback)
            {
                ctx.TransitionTo(MarketState.Pullback);
                ctx.ResetPullback();
            }*/

            // ---------------------------------
            // 🧠 PRICE ACCEPTANCE CHECK (CRITICAL FIX)
            // ---------------------------------
            var lastClosedCandle = rates1H[^2];


            // Direction from EMA regime (already trusted elsewhere)
            bool isBullish = emaFast1H[^1] > emaSlow1H[^1];

            bool priceAccepted = PriceAcceptanceEngine.IsPriceAccepted(
                rates1H,
                lastClosedCandle,
                emaFast1H,
                atrRaw,
                 isBullish ? TradeDirection.Bullish : TradeDirection.Bearish
            );

            ctx.PriceAccepted = priceAccepted;

            // OPTIONAL: ATR cooling after impulse
            if (ctx.PeakAtr == 0)
                ctx.PeakAtr = atrRaw;

            bool atrCooling = IsAtrCool(ctx, SYMBOL, atrRaw);

            if (atrCooling)
            {
                ctx.ShockActive = false;
            }

            ctx.AtrCooldown = atrCooling;


            // ---------------------------------
            // PULLBACK → ARMED (SAFE VERSION)
            // ---------------------------------
            if (ctx.State == MarketState.Pullback)
            {
                ctx.IncrementPullback();

                bool bigShockCandle = (last.high - last.low) > atrRaw * 1.6; // tune 1.4–1.8

                if (bigShockCandle)
                {
                    ctx.ShockActive = true;
                    ctx.LastShockTime = last.time;
                }

                if (GetUniverse(SYMBOL) == MarketUniverse.Metals && bigShockCandle)
                {
                    Console.WriteLine($"Shock candle detected — cooldown required {ctx.Symbol}");
                    // force at least one more candle of stabilization

                }

                if (!coordinator.CanArm(SYMBOL))
                    return;

                Console.WriteLine(
                        $"🟢 before armed {SYMBOL} PullbackSufficient = {ctx.PullbackSufficient} | PriceAccepted={priceAccepted} ATRcool={atrCooling}"
                    );

                //if (ctx.PullbackSufficient &&
                //priceAccepted &&
                //(!ctx.ShockActive || atrCooling))
                // ---------------------------------------------
                // SOFT ARMING (location-based)
                // ---------------------------------------------
                if (ctx.PullbackSufficient &&
                    (ctx.PriceAccepted || ctx.AtrCooldown))
                {

                    UpdateFreshness(
                        SYMBOL,
                        ctx,
                        emaFast1H,
                        atrRaw,
                        rates1H
                    );

                    if (!IsFreshEnoughToArm(SYMBOL, ctx))
                    {
                        LogFile.Write(
                            $"⛔ {SYMBOL} arming blocked: stale/extended symbol | {GetFreshnessSummary(SYMBOL)}"
                        );

                        Console.WriteLine(
                            $"⛔ {SYMBOL} arming blocked: stale/extended symbol | {GetFreshnessSummary(SYMBOL)}"
                        );

                        ctx.TransitionTo(MarketState.Pullback);
                        ctx.ResetPullback();

                        return;
                    }

                    LogFile.Write($"SOFT ARMING CONDITIONS MET | {SYMBOL} | PullbackSufficient={ctx.PullbackSufficient} | PriceAccepted={ctx.PriceAccepted} | AtrCooldown={ctx.AtrCooldown}");
                    Console.WriteLine(
                        $"🟢 {SYMBOL} SOFT ARMED | PullbackSufficient={ctx.PullbackSufficient} | PriceAccepted={ctx.PriceAccepted} | AtrCooldown={ctx.AtrCooldown}"
                    );
                    //  if (ctx.PullbackSufficient && priceAccepted && atrCooling)
                    //{
                    ctx.ArmedHybrid = momentum.Hybrid;   // 🔒 BASELINE CAPTURE (NON-ZERO)
                    coordinator.SetArmed(SYMBOL);

                    ctx.TransitionTo(MarketState.Armed);
                    bool proceed = HandleArmedStateTransition(ctx, ctx.PreviousState, ctx.State);

                    if (!proceed)
                        return; // IMPORTANT: stop further processing for this symbol


                    BotStateUpdate armedState = new BotStateUpdate()
                    {
                        state = ctx.State.ToString(),
                        Symbol = ctx.Symbol,
                        PriceAccepted = ctx.PriceAccepted,
                        AtrCoolDown = ctx.AtrCooldown,
                        PullbackSufficient = ctx.PullbackSufficient,
                        ListOfStates = ctx.GetListOfStates()
                    };
                    TradingEventBus.State(armedState);

                    Console.WriteLine(
                        $"🟢 {SYMBOL} ARMED | PriceAccepted={priceAccepted} ATRcool={atrCooling} PullbackSufficient={ctx.PullbackSufficient}"
                    );
                }

                return;
            }

            // ---------------------------------
            // ARMED → DISARM (ACCEPTANCE LOST)
            // ---------------------------------
            if (ctx.State == MarketState.Armed)
            {
                bool acceptanceLost =
                    ctx.PriceAccepted == false &&          // price left acceptance zone
                        momentum.Impulse == false &&           // no continuation yet
                                momentum.Hybrid < ctx.ArmedHybrid * 0.85; // momentum actually faded


                if (acceptanceLost)
                {

                    Console.WriteLine($"🔓 {SYMBOL} DISARMED — acceptance lost");

                    ctx.TransitionTo(MarketState.Neutral);
                    ctx.ResetPullback();
                    ctx.ResetExpansion();

                    SymbolPriorityEngine.Release(ctx.Symbol);

                    ctx.PeakAtr = 0.0;
                    ctx.PeakAtrCaptured = false;
                    ctx.ClearListOfStates();
                    coordinator.Clear();

                    ctx.LastImpulseHadAbsorption = false;
                    ctx.CandlesSinceImpulse = 0;

                }
                else
                {

                }

                return;
            }

            // ---------------------------------
            // ARMED: wait for trade execution
            // ---------------------------------
            Console.WriteLine(
                $"STATE={ctx.State} | Exp={ctx.ExpansionCandles} | PB={ctx.PullbackCandles} | Hybrid={momentum.Hybrid:F2}"
            );
        }

        private bool HandleArmedStateTransition(
            SymbolContext ctx,
            MarketState previousState,
            MarketState newState)
        {
            DateTime nowAst = DateTime.UtcNow.AddHours(-4);
            bool cryptoWindow = TradingUniverseSelector.IsCryptoWindow(nowAst);

            // Only care about transitions INTO Armed
            if (previousState == MarketState.Armed ||
                newState != MarketState.Armed)
                return true; // allow normal flow

            // Try to acquire global priority
            double myPriority = SymbolPriority.Get(ctx.Symbol, cryptoWindow);
            bool acquired = SymbolPriorityEngine.TryAcquire(ctx.Symbol, myPriority);


            if (!acquired)
            {
                // ❌ Lost priority fight → demote self
                ctx.TransitionTo(MarketState.Pullback);
                ctx.ResetPullback();
                ctx.ResetExpansion();

                LogFile.Write($"⛔ {ctx.Symbol} demoted — higher priority symbol active");
                return false; // STOP further processing for this symbol
            }

            // ✅ Won priority → demote others
            foreach (var symbol in _symbols)
            {
                if (symbol == ctx.Symbol)
                    continue;

                //ctx.Symbol is about to be armed
                _contexts.TryGetValue(symbol, out SymbolContext symbolContext);
                if (symbolContext.State == MarketState.Armed &&
                    SymbolPriority.Get(symbol, cryptoWindow) < myPriority)
                {
                    symbolContext.TransitionTo(MarketState.Pullback);
                    symbolContext.ResetExpansion();

                    Console.WriteLine($"🔻 {symbolContext.Symbol} forced back to Pullback by {ctx.Symbol}");
                    LogFile.Write($"🔻 {symbolContext.Symbol} forced back to Pullback by {ctx.Symbol}");
                }
            }
            Console.WriteLine($"🔐 {ctx.Symbol} acquired system priority");
            LogFile.Write($"🔐 {ctx.Symbol} acquired system priority");
            return true;
        }

        private double GetDynamicThreshold(
            double atrRaw,
            bool emaCompressed,
            MarketState state,
            MomentumResult momentum)
        {
            double t = 0.60;

            // -----------------------------
            // Volatility sensitivity
            // -----------------------------
            if (atrRaw < 1.2)
                t -= 0.05;

            if (atrRaw > 2.5)
                t += 0.05;

            // -----------------------------
            // Compression penalty
            // -----------------------------
            if (emaCompressed)
                t += 0.05;

            // -----------------------------
            // Neutral / early state bonus
            // -----------------------------
            if (state == MarketState.Neutral)
                t -= 0.05;

            // -----------------------------
            // Momentum quality bonuses
            // -----------------------------
            if (momentum.Breakout)
                t -= 0.05;

            if (momentum.Impulse)
                t -= 0.05;

            return Math.Clamp(t, 0.45, 0.75);
        }


        // ----------------------------
        // SYMBOL SELECTION
        // ----------------------------
        private string ChooseBestSymbol()
        {
            // 🔒 If a symbol is armed, ONLY return that symbol
            if (coordinator.HasArmedSymbol)
                return coordinator.ArmedSymbol;

            string bestSymbol = null;
            SymbolContext bestCtx = null;
            double bestScore = double.MinValue;

            foreach (var kvp in _contexts)
            {
                var ctx = kvp.Value;
                double score = 0;

                if (ctx.State == MarketState.Armed) score += 2.0;
                if (ctx.State == MarketState.Pullback) score += 1.0;
                if (ctx.State == MarketState.Exhaustion) score -= 1.5;

                // ✅ Primary: higher score wins
                // ✅ Tie-breaker: earlier StateEnteredAt wins
                if (score > bestScore ||
                    (score == bestScore && bestCtx != null &&
                     ctx.StateEnteredAt < bestCtx.StateEnteredAt))
                {
                    bestScore = score;
                    bestSymbol = ctx.Symbol;
                    bestCtx = ctx;

                    BotStateUpdate armedState = new BotStateUpdate()
                    {
                        state = ctx.State.ToString(),
                        Symbol = ctx.Symbol,
                        score = bestScore
                    };
                }

                Console.WriteLine(
                        $"Candidate {ctx.Symbol}: State={ctx.State}, Score={score}, EnteredAt={ctx.StateEnteredAt:HH:mm:ss}"
                );
                LogFile.Write($"Candidate {ctx.Symbol}: State={ctx.State}, Score={score}, EnteredAt={ctx.StateEnteredAt:HH:mm:ss}");

            }


            return bestSymbol;
        }


        // ----------------------------
        // HELPERS
        // ----------------------------
        public SymbolContext GetContext(string symbol)
        {
            if (!_contexts.ContainsKey(symbol))
            {
                _contexts.Add(symbol, new SymbolContext(symbol));
            }

            return _contexts.GetValueOrDefault(symbol);

        }

        private bool IsEmaCompressed(
            double[] emaFast,
            double[] emaSlow,
            double atrRaw)
        {
            double dist = Math.Abs(emaFast[^1] - emaSlow[^1]);
            return dist < atrRaw * 0.15;
        }
    }
}
