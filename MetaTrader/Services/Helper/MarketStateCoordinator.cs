using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.Enums.MetaTrader.Enums;
using MetaTrader.helper;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using static MetaTrader.Services.HybridMomentumEngine;

namespace MetaTrader.Services.Helper
{
    public class MarketStateCoordinator
    {
        private readonly MtApi5Client _client;
        private readonly IEnumerable<string> _symbols;

        private readonly TradeCoordinator _indexCoordinator;
        private readonly TradeCoordinator _metalsCoordinator;
        private readonly TradeCoordinator _fxCoordinator;
        private readonly TradeCoordinator _currencyCoordinator;
        private readonly TradeCoordinator _cryptoCoordinator;

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
            TradeCoordinator indexCoordinator,
            TradeCoordinator metalsCoordinator,
            TradeCoordinator cryptoCoordinator,
            TradeCoordinator currencyCoordinator,
            Dictionary<string, SymbolContext> contexts
        )
        {
            _cryptoCoordinator = cryptoCoordinator;
            _indexCoordinator = indexCoordinator;
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
                MarketUniverse.Index => _indexCoordinator,
                MarketUniverse.FX => _fxCoordinator,
                MarketUniverse.Metals => _metalsCoordinator,
                MarketUniverse.Currency => _currencyCoordinator,
                MarketUniverse.Crypto => _cryptoCoordinator,
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
    "SOLUSD",
    "ETHUSD",
    "BTCUSD",
    "BNBUSD",
    "XRPUSD",
    "ADAUSD",
    "AVAXUSD",
    "LINKUSD",
    "AVAUSD",
    "LNKUSD"
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

            if (GoldSymbols.Contains(baseSymbol))
                return MarketUniverse.Currency;

            if (baseSymbol.Contains("US100") || baseSymbol.Contains("NAS"))
                return MarketUniverse.Index;

            return MarketUniverse.Crypto;
        }

        private bool IsAtrCool(SymbolContext ctx, string symbol, double atrRaw)
        {
            var u = GetUniverse(symbol);

            if (ctx.PeakAtr <= 0)
                ctx.PeakAtr = atrRaw;

            double peakFactor = (u == MarketUniverse.Metals) ? 0.90 : 0.85;

            bool coolByPeak = atrRaw < ctx.PeakAtr * peakFactor;
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
            foreach (var symbol in _symbols)
            {
                // IMPORTANT:
                // Update all symbols, not only crypto.
                // Otherwise gold/FX freshness can remain unavailable.
                UpdateSymbolState(symbol);
            }

            // GOLD: inspect executable live impulse candidates BEFORE selecting
            // a single symbol by market-state score. This does not place orders,
            // bypass MT5 position locks, or relax anti-chase rules.
            string goldImpulseCandidate = SelectQualifiedLiveGoldImpulse();
            if (!string.IsNullOrWhiteSpace(goldImpulseCandidate))
                return goldImpulseCandidate;

            return ChooseBestSymbol();
        }

        // ================================================================
        // GOLD LIVE IMPULSE PRESELECTION
        // ================================================================
        // The original selector ranked closed-H1 MarketState and freshness.
        // That could select XAUEUR while XAUUSD already had a valid live
        // impulse. Evaluate BOTH symbols against the EXISTING impulse rules.
        // This is selection only: M15/SR/risk/duplicate/MT5 final guards
        // remain authoritative inside TradeServiceEngine.
        // ================================================================
        private string SelectQualifiedLiveGoldImpulse()
        {
            string bestSymbol = null;
            double bestScore = double.NegativeInfinity;

            foreach (string symbol in _symbols)
            {
                if (string.IsNullOrWhiteSpace(symbol) ||
                    !(NormalizeSymbol(symbol).StartsWith("XAU") ||
                      NormalizeSymbol(symbol).StartsWith("XAG")))
                    continue;

                try
                {
                    _client.CopyRates(symbol, ENUM_TIMEFRAMES.PERIOD_H1,
                        1, 120, out MqlRates[] closedH1);

                    if (closedH1 == null || closedH1.Length < 30)
                        continue;

                    double atr = IndicatorEngine.CalculateATR(closedH1, 14);
                    double[] ema9Values = IndicatorEngine.CalculateEMA(closedH1, 9);
                    double[] ema21Values = IndicatorEngine.CalculateEMA(closedH1, 21);

                    if (atr <= 0 || ema9Values == null || ema21Values == null ||
                        ema9Values.Length < 2 || ema21Values.Length < 1)
                        continue;

                    double bid = _client.SymbolInfoDouble(symbol,
                        ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);
                    double ask = _client.SymbolInfoDouble(symbol,
                        ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);
                    if (bid <= 0 || ask <= bid)
                        continue;

                    // Match the synthetic live-H1 convention currently used
                    // by the GOLD FastEntry path. No intrabar wick claims.
                    double liveOpen = closedH1[^1].close;
                    double liveMid = (bid + ask) / 2.0;
                    var synthetic = new MqlRates
                    {
                        open = liveOpen,
                        close = liveMid,
                        high = Math.Max(liveOpen, liveMid),
                        low = Math.Min(liveOpen, liveMid)
                    };

                    // Use CLOSED-bar structural reference. Never derive a
                    // 'recent high' from a live candle that includes the
                    // current bid/ask.
                    double recentHigh = double.NegativeInfinity;
                    double recentLow = double.PositiveInfinity;
                    int start = Math.Max(0, closedH1.Length - 20);
                    for (int i = start; i < closedH1.Length; i++)
                    {
                        recentHigh = Math.Max(recentHigh, closedH1[i].high);
                        recentLow = Math.Min(recentLow, closedH1[i].low);
                    }

                    var momentum = HybridMomentumEngine.CalculateLive(
                        liveOpen, bid, ask, closedH1[^1], closedH1,
                        ema9Values, ema21Values, atr, false);

                    // Match the live-EMA convention in the GOLD engine:
                    // update the last CLOSED EMA with the current live midpoint.
                    double liveEma9 = ema9Values[^1] +
                        (2.0 / (9.0 + 1.0)) * (liveMid - ema9Values[^1]);
                    double liveEma21 = ema21Values[^1] +
                        (2.0 / (21.0 + 1.0)) * (liveMid - ema21Values[^1]);

                    bool qualified = LiveImpulseEntryEngine.TryEnter(
                        symbol, synthetic, atr,
                        liveEma9, ema9Values[^1], liveEma21,
                        recentHigh, recentLow, bid, ask, momentum.Hybrid,
                        out TradeAction action);

                    if (!qualified ||
                        (action != TradeAction.EnterBuy &&
                         action != TradeAction.EnterSell))
                        continue;

                    // A valid signal near EMA9 receives priority over a
                    // similarly qualified but more extended signal.
                    double executable = action == TradeAction.EnterBuy ? ask : bid;
                    double distanceAtr = Math.Abs(executable - liveEma9) / atr;
                    double score = momentum.Hybrid - distanceAtr * 0.30;

                    LogFile.Write($"[{symbol}] GOLD LIVE SELECTION CANDIDATE | " +
                        $"action={action} | momentum={momentum.Hybrid:F3} | " +
                        $"emaDistATR={distanceAtr:F3} | score={score:F3}");

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestSymbol = symbol;
                    }
                }
                catch (Exception ex)
                {
                    // Candidate scanning must not stop ordinary symbol
                    // selection if one broker symbol temporarily fails.
                    LogFile.Write($"[{symbol}] GOLD LIVE SELECTION ERROR | {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(bestSymbol))
                LogFile.Write($"[{bestSymbol}] GOLD LIVE SIGNAL PRIORITIZED | " +
                    $"reason=qualified_impulse_before_state_score | score={bestScore:F3}");

            return bestSymbol;
        }

        private void UpdateSymbolState(string marketSymbol)
        {
            var ctx = GetContext(marketSymbol);

            _client.CopyRates(
                marketSymbol,
                ENUM_TIMEFRAMES.PERIOD_H1,
                1,
                120,
                out MqlRates[] rates1H
            );

            if (rates1H == null || rates1H.Length < 3)
                return;

            double atrRaw = IndicatorEngine.CalculateATR(rates1H, 14);
            double[] emaFast1H = IndicatorEngine.CalculateEMA(rates1H, 9);
            double[] emaSlow1H = IndicatorEngine.CalculateEMA(rates1H, 21);

            var last = rates1H[^1];
            var prev = rates1H[^2];

            double liveOpen = rates1H[^1].close;
            double bid = _client.SymbolInfoDouble(ctx.Symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);
            double ask = _client.SymbolInfoDouble(ctx.Symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);
            var momentum =
                HybridMomentumEngine.CalculateLive(
                    liveOpen,
                    bid,
                    ask,
                    rates1H[^1],
                    rates1H,
                    emaFast1H,
                    emaSlow1H,
                    atrRaw,
                    false);

            UpdateMarketState(
                marketSymbol,
                ctx,
                momentum,
                emaFast1H,
                emaSlow1H,
                atrRaw,
                rates1H
            );
        }

        private static double CalculateAtrPercentile(double currentAtr, Queue<double> history)
        {
            if (history == null || history.Count < 20)
                return 50.0;

            int belowOrEqual = history.Count(x => x <= currentAtr);

            double percentile = ((double)belowOrEqual / history.Count) * 100.0;

            percentile = Math.Max(1.0, percentile);
            percentile = Math.Min(99.0, percentile);

            return percentile;
        }

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
            if (TradingConfig.Mode == TradingMode.XauUsdOnly &&
                SYMBOL != "XAUUSD.s")
            {
                return;
            }

            coordinator = GetCoordinator(SYMBOL);

            ctx.ArmedHybrid = 0.0;

            DateTime currentCandleTime = rates1H[^1].time;

            VolatilityGuard.UpdateShockStatus(SYMBOL, rates1H, atrRaw);

            // IMPORTANT:
            // Always update freshness, even if this H1 candle was already processed.
            // This prevents "Freshness: unavailable" and stale freshness during live movement.
            UpdateFreshness(SYMBOL, ctx, emaFast1H, atrRaw, rates1H);

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
                    return;
                }
            }

            if (ctx.PrevAtr > 0 && atrRaw < ctx.PrevAtr)
                ctx.AtrDownCount++;
            else
                ctx.AtrDownCount = 0;

            ctx.PrevAtr = atrRaw;

            ctx.AtrHistory.Enqueue(atrRaw);

            if (ctx.AtrHistory.Count > 50)
                ctx.AtrHistory.Dequeue();

            ctx.AtrPercentile = CalculateAtrPercentile(atrRaw, ctx.AtrHistory);

            var last = rates1H[^1];

            double full = last.high - last.low;
            double body = Math.Abs(last.open - last.close);
            double lowerWick = Math.Min(last.open, last.close) - last.low;

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

            if (ctx.State == MarketState.Neutral &&
                momentum.Strong &&
                momentum.Hybrid > dynamicThreshold)
            {
                ctx.TransitionTo(MarketState.Impulse);
                ctx.IncrementExpansion();

                ctx.PeakAtr = atrRaw;
                ctx.PeakAtrCaptured = true;

                UpdateFreshness(SYMBOL, ctx, emaFast1H, atrRaw, rates1H);

                Console.WriteLine(
                    $"🔥 {SYMBOL} TRANSITION → {ctx.State} | Exp={ctx.ExpansionCandles}"
                );

                return;
            }

            if (ctx.State == MarketState.Neutral &&
                emaCompressed &&
                !momentum.Strong)
            {
                ctx.LastImpulseHadAbsorption = false;
                ctx.CandlesSinceImpulse = 0;

                ctx.TransitionTo(MarketState.Compression);
                UpdateFreshness(SYMBOL, ctx, emaFast1H, atrRaw, rates1H);

                return;
            }

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

                UpdateFreshness(SYMBOL, ctx, emaFast1H, atrRaw, rates1H);

                return;
            }

            if (ctx.State == MarketState.Impulse ||
                ctx.State == MarketState.Continuation)
            {
                ctx.IncrementExpansion();
                UpdateFreshness(SYMBOL, ctx, emaFast1H, atrRaw, rates1H);

                if (ctx.ExpansionCandles == 2)
                    ctx.TransitionTo(MarketState.Continuation);

                if (ctx.IsLateExpansion)
                    ctx.TransitionTo(MarketState.Exhaustion);

                return;
            }

            if (ctx.State == MarketState.Exhaustion)
            {
                var lastClosed = rates1H[^2];
                double ema9 = emaFast1H[^1];
                double midPrice = (lastClosed.open + lastClosed.close) / 2.0;

                bool emaReclaimed =
                    lastClosed.low <= ema9 &&
                    lastClosed.high >= ema9;

                bool volatilityCooled =
                    Math.Abs(midPrice - ema9) / atrRaw < 0.35;

                bool timeDecay =
                    ctx.ExpansionCandles >= 2;

                if ((emaReclaimed && volatilityCooled) || timeDecay)
                {
                    Console.WriteLine($"🟢 {SYMBOL} EXHAUSTION RECOVERED → Pullback");

                    ctx.TransitionTo(MarketState.Pullback);
                    ctx.ResetExpansion();
                    ctx.ResetPullback();

                    ctx.PeakAtr = 0.0;
                    ctx.PeakAtrCaptured = false;

                    UpdateFreshness(SYMBOL, ctx, emaFast1H, atrRaw, rates1H);

                    return;
                }
            }

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

                ctx.ActiveOrderBlock = null;
                ctx.OrderBlockCreatedAt = null;

                ctx.PeakAtr = 0.0;
                ctx.PeakAtrCaptured = false;

                UpdateFreshness(SYMBOL, ctx, emaFast1H, atrRaw, rates1H);

                Console.WriteLine(
                    $"❌ {ctx.Symbol} FAILED EXPANSION — structure invalidated");

                return;
            }

            bool cooled =
                !momentum.Strong &&
                momentum.Hybrid < 1.0 &&
                !momentum.Breakout &&
                !momentum.Impulse;

            if (cooled && ctx.State == MarketState.Armed)
            {
                ctx.TransitionTo(MarketState.Pullback);
                ctx.ResetPullback();
                ctx.MarketCoolingState();
                SymbolPriorityEngine.Release(ctx.Symbol);
                coordinator.Clear();
            }

            var lastClosedCandle = rates1H[^2];

            bool isBullish = emaFast1H[^1] > emaSlow1H[^1];

            bool priceAccepted = PriceAcceptanceEngine.IsPriceAccepted(
                rates1H,
                lastClosedCandle,
                emaFast1H,
                atrRaw,
                isBullish ? TradeDirection.Bullish : TradeDirection.Bearish
            );

            ctx.PriceAccepted = priceAccepted;

            if (ctx.PeakAtr == 0)
                ctx.PeakAtr = atrRaw;

            bool atrCooling = IsAtrCool(ctx, SYMBOL, atrRaw);

            if (atrCooling)
                ctx.ShockActive = false;

            ctx.AtrCooldown = atrCooling;

            if (ctx.State == MarketState.Pullback)
            {
                ctx.IncrementPullback();

                bool bigShockCandle = (last.high - last.low) > atrRaw * 1.6;

                if (bigShockCandle)
                {
                    ctx.ShockActive = true;
                    ctx.LastShockTime = last.time;
                }

                if (GetUniverse(SYMBOL) == MarketUniverse.Metals && bigShockCandle)
                {
                    Console.WriteLine($"Shock candle detected — cooldown required {ctx.Symbol}");
                }

                if (!coordinator.CanArm(SYMBOL))
                    return;

                Console.WriteLine(
                    $"🟢 before armed {SYMBOL} PullbackSufficient = {ctx.PullbackSufficient} | PriceAccepted={priceAccepted} ATRcool={atrCooling}"
                );


                bool isIndex = SYMBOL.Contains("US100") || SYMBOL.Contains("NAS");

                bool indexArmOk =
                    isIndex &&
                    ctx.PullbackSufficient &&
                    momentum.Strong &&
                    momentum.Hybrid >= 0.70;

                if (ctx.PullbackSufficient &&
                (
                    ctx.PriceAccepted ||
                    ctx.AtrCooldown ||
                    indexArmOk
                ))
                {
                    UpdateFreshness(SYMBOL, ctx, emaFast1H, atrRaw, rates1H);

                    if (!HasUsableFreshness(SYMBOL) || !IsFreshEnoughToArm(SYMBOL, ctx))
                    {
                        LogFile.Write(
                            $"⛔ {SYMBOL} arming blocked: freshness unavailable/stale/extended | {GetFreshnessSummary(SYMBOL)}"
                        );

                        Console.WriteLine(
                            $"⛔ {SYMBOL} arming blocked: freshness unavailable/stale/extended | {GetFreshnessSummary(SYMBOL)}"
                        );

                        ctx.TransitionTo(MarketState.Pullback);
                        ctx.ResetPullback();

                        return;
                    }

                    LogFile.Write(
                        $"SOFT ARMING CONDITIONS MET | {SYMBOL} | " +
                        $"PullbackSufficient={ctx.PullbackSufficient} | " +
                        $"PriceAccepted={ctx.PriceAccepted} | " +
                        $"AtrCooldown={ctx.AtrCooldown} | " +
                        $"{GetFreshnessSummary(SYMBOL)}"
                    );

                    Console.WriteLine(
                        $"🟢 {SYMBOL} SOFT ARMED | PullbackSufficient={ctx.PullbackSufficient} | " +
                        $"PriceAccepted={ctx.PriceAccepted} | ATRcool={ctx.AtrCooldown} | " +
                        $"{GetFreshnessSummary(SYMBOL)}"
                    );

                    ctx.ArmedHybrid = momentum.Hybrid;
                    coordinator.SetArmed(SYMBOL);

                    ctx.TransitionTo(MarketState.Armed);

                    bool proceed = HandleArmedStateTransition(
                        ctx,
                        ctx.PreviousState,
                        ctx.State
                    );

                    if (!proceed)
                        return;

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
                        $"🟢 {SYMBOL} ARMED | PriceAccepted={priceAccepted} ATRcool={atrCooling} " +
                        $"PullbackSufficient={ctx.PullbackSufficient} | {GetFreshnessSummary(SYMBOL)}"
                    );
                }

                return;
            }

            if (ctx.State == MarketState.Armed)
            {
                if (!HasUsableFreshness(SYMBOL) || !IsFreshEnoughToArm(SYMBOL, ctx))
                {
                    LogFile.Write(
                        $"🔓 {SYMBOL} DISARMED — freshness unavailable/stale while armed | {GetFreshnessSummary(SYMBOL)}"
                    );

                    ctx.TransitionTo(MarketState.Pullback);
                    ctx.ResetPullback();
                    ctx.ResetExpansion();

                    SymbolPriorityEngine.Release(ctx.Symbol);
                    coordinator.Clear();

                    return;
                }

                bool acceptanceLost =
                    ctx.PriceAccepted == false &&
                    momentum.Impulse == false &&
                    momentum.Hybrid < ctx.ArmedHybrid * 0.85;

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

                return;
            }

            Console.WriteLine(
                $"STATE={ctx.State} | Exp={ctx.ExpansionCandles} | PB={ctx.PullbackCandles} | Hybrid={momentum.Hybrid:F2}"
            );
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
                LogFile.Write($"[{symbol}] Freshness update failed: insufficient data");
                return;
            }

            string normalized = NormalizeSymbol(symbol);

            double ema9 = emaFast1H[^1];
            double close = rates1H[^1].close;

            double distanceFromEma9Atr = Math.Abs(close - ema9) / atrRaw;

            MarketUniverse universe = GetUniverse(symbol);

            double maxFreshDistance =
                universe == MarketUniverse.Metals ? 1.25 :
                universe == MarketUniverse.Index ? 1.45 :
                universe == MarketUniverse.FX ? 1.10 :
                1.20;

            int maxExpansionCandles = GetMaxExpansionCandles(symbol);

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

            int maxLookback = Math.Min(
                6,
                Math.Min(emaFast1H.Length, rates1H.Length)
            );

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

        private bool HasUsableFreshness(string symbol)
        {
            string normalized = NormalizeSymbol(symbol);

            if (!_freshness.TryGetValue(normalized, out var f))
                return false;

            return (DateTime.UtcNow - f.LastUpdatedUtc).TotalMinutes <= 3;
        }

        private bool IsFreshEnoughToArm(string symbol, SymbolContext ctx)
        {
            string normalized = NormalizeSymbol(symbol);

            // IMPORTANT:
            // No freshness means no arming.
            if (!_freshness.TryGetValue(normalized, out var f))
                return false;

            if ((DateTime.UtcNow - f.LastUpdatedUtc).TotalMinutes > 3)
                return false;

            if (f.IsLateExpansion)
                return false;

            if (f.ExpansionCandles >= GetMaxExpansionCandles(symbol))
                return false;

            if (f.IsTooExtended)
                return false;

            return true;
        }

        private int GetMaxExpansionCandles(string symbol)
        {
            MarketUniverse universe = GetUniverse(symbol);

            if (universe == MarketUniverse.Metals) return 4;
            if (universe == MarketUniverse.Crypto) return 5;
            if (universe == MarketUniverse.FX) return 3;

            if (symbol.Contains("US100") || symbol.Contains("NAS"))
                return 5;

            return 4;
        }

        private string GetFreshnessSummary(string symbol)
        {
            string normalized = NormalizeSymbol(symbol);

            if (!_freshness.TryGetValue(normalized, out var f))
                return "Freshness: unavailable";

            // This is the age of the freshness SNAPSHOT, not the age of the
            // current MarketState/candidate. UpdateFreshness() intentionally
            // refreshes this on every scan, so this value will normally be near 0.
            double freshnessAgeSeconds =
                Math.Max(0, (DateTime.UtcNow - f.LastUpdatedUtc).TotalSeconds);

            return
                $"Freshness: distEMA9Atr={f.DistanceFromEma9Atr:F2}, " +
                $"expCandles={f.ExpansionCandles}, " +
                $"late={f.IsLateExpansion}, " +
                $"tooExtended={f.IsTooExtended}, " +
                $"freshnessAgeSec={freshnessAgeSeconds:F0}";
        }

        private double GetStateAgeSeconds(SymbolContext ctx)
        {
            if (ctx == null || ctx.StateEnteredAt == DateTime.MinValue)
                return 0;

            DateTime enteredAt = ctx.StateEnteredAt;
            DateTime now;

            // Preserve the clock basis already stored by SymbolContext.
            // Existing logs indicate StateEnteredAt is UTC in this bot, but this
            // also handles Local correctly and treats Unspecified as UTC to avoid
            // mixing local/UTC clocks.
            if (enteredAt.Kind == DateTimeKind.Local)
            {
                now = DateTime.Now;
            }
            else
            {
                if (enteredAt.Kind == DateTimeKind.Unspecified)
                {
                    enteredAt = DateTime.SpecifyKind(
                        enteredAt,
                        DateTimeKind.Utc
                    );
                }

                now = DateTime.UtcNow;
            }

            return Math.Max(
                0,
                (now - enteredAt).TotalSeconds
            );
        }

        private bool HandleArmedStateTransition(
            SymbolContext ctx,
            MarketState previousState,
            MarketState newState)
        {
            DateTime nowAst = DateTime.UtcNow.AddHours(-4);
            bool cryptoWindow = TradingUniverseSelector.IsCryptoWindow(nowAst);

            if (previousState == MarketState.Armed ||
                newState != MarketState.Armed)
                return true;

            if (!HasUsableFreshness(ctx.Symbol) || !IsFreshEnoughToArm(ctx.Symbol, ctx))
            {
                ctx.TransitionTo(MarketState.Pullback);
                ctx.ResetPullback();
                ctx.ResetExpansion();

                SymbolPriorityEngine.Release(ctx.Symbol);
                coordinator.Clear();

                LogFile.Write(
                    $"⛔ {ctx.Symbol} demoted before priority: freshness unavailable/not fresh | " +
                    GetFreshnessSummary(ctx.Symbol)
                );

                return false;
            }

            double myPriority = SymbolPriority.Get(ctx.Symbol, cryptoWindow);
            bool acquired = SymbolPriorityEngine.TryAcquire(ctx.Symbol, myPriority);

            if (!acquired)
            {
                ctx.TransitionTo(MarketState.Pullback);
                ctx.ResetPullback();
                ctx.ResetExpansion();

                LogFile.Write($"⛔ {ctx.Symbol} demoted — higher priority symbol active");
                return false;
            }

            foreach (var symbol in _symbols)
            {
                if (symbol == ctx.Symbol)
                    continue;

                _contexts.TryGetValue(symbol, out SymbolContext symbolContext);

                if (symbolContext == null)
                    continue;

                if (symbolContext.State == MarketState.Armed &&
                    SymbolPriority.Get(symbol, cryptoWindow) < myPriority)
                {
                    symbolContext.TransitionTo(MarketState.Pullback);
                    symbolContext.ResetExpansion();

                    Console.WriteLine($"🔻 {symbolContext.Symbol} forced back to Pullback by {ctx.Symbol}");
                    LogFile.Write($"🔻 {symbolContext.Symbol} forced back to Pullback by {ctx.Symbol}");
                }
            }

            Console.WriteLine($"🔐 {ctx.Symbol} acquired system priority | {GetFreshnessSummary(ctx.Symbol)}");
            LogFile.Write($"🔐 {ctx.Symbol} acquired system priority | {GetFreshnessSummary(ctx.Symbol)}");

            return true;
        }

        private double GetDynamicThreshold(
            double atrRaw,
            bool emaCompressed,
            MarketState state,
            MomentumResult momentum)
        {
            double t = 0.60;

            if (atrRaw < 1.2)
                t -= 0.05;

            if (atrRaw > 2.5)
                t += 0.05;

            if (emaCompressed)
                t += 0.05;

            if (state == MarketState.Neutral)
                t -= 0.05;

            if (momentum.Breakout)
                t -= 0.05;

            if (momentum.Impulse)
                t -= 0.05;

            return Math.Clamp(t, 0.45, 0.75);
        }

        private string ChooseBestSymbol()
        {
            if (coordinator != null && coordinator.HasArmedSymbol)
            {
                var armedCtx = GetContext(coordinator.ArmedSymbol);

                if (HasUsableFreshness(coordinator.ArmedSymbol) &&
                    IsFreshEnoughToArm(coordinator.ArmedSymbol, armedCtx))
                {
                    return coordinator.ArmedSymbol;
                }

                LogFile.Write(
                    $"⛔ {coordinator.ArmedSymbol} armed symbol rejected: freshness unavailable/stale | " +
                    GetFreshnessSummary(coordinator.ArmedSymbol)
                );

                coordinator.Clear();
            }

            string bestSymbol = null;
            SymbolContext bestCtx = null;
            double bestScore = double.MinValue;

            foreach (var kvp in _contexts)
            {
                var ctx = kvp.Value;
                double score = 0;

                if (ctx.State == MarketState.Armed)
                {
                    if (HasUsableFreshness(ctx.Symbol) &&
                        IsFreshEnoughToArm(ctx.Symbol, ctx))
                    {
                        score += 2.0;
                    }
                    else
                    {
                        score -= 3.0;

                        LogFile.Write(
                            $"⛔ Candidate {ctx.Symbol}: Armed but freshness invalid | " +
                            GetFreshnessSummary(ctx.Symbol)
                        );
                    }
                }

                if (ctx.State == MarketState.Pullback)
                    score += 1.0;

                if (ctx.State == MarketState.Exhaustion)
                    score -= 1.5;

                string normalized = NormalizeSymbol(ctx.Symbol);

                if (_freshness.TryGetValue(normalized, out var f))
                {
                    if (f.IsLateExpansion)
                        score -= 2.5;

                    if (f.ExpansionCandles >= GetMaxExpansionCandles(ctx.Symbol))
                        score -= 2.0;

                    if (f.IsTooExtended)
                        score -= 2.0;

                    if ((DateTime.UtcNow - f.LastUpdatedUtc).TotalMinutes > 3)
                        score -= 2.0;
                }
                else
                {
                    score -= 1.5;
                }

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

                double stateAgeSeconds =
                    GetStateAgeSeconds(ctx);

                Console.WriteLine(
                    $"Candidate {ctx.Symbol}: State={ctx.State}, Score={score}, " +
                    $"EnteredAt={ctx.StateEnteredAt:HH:mm:ss}, " +
                    $"stateAgeSec={stateAgeSeconds:F0} | " +
                    $"{GetFreshnessSummary(ctx.Symbol)}"
                );

                LogFile.Write(
                    $"Candidate {ctx.Symbol}: State={ctx.State}, Score={score}, " +
                    $"EnteredAt={ctx.StateEnteredAt:HH:mm:ss}, " +
                    $"stateAgeSec={stateAgeSeconds:F0} | " +
                    $"{GetFreshnessSummary(ctx.Symbol)}"
                );
            }

            return bestSymbol;
        }

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
