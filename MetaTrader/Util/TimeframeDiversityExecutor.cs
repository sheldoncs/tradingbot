namespace MetaTrader.Util
{
    using System;
    using System.Collections.Concurrent;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using MtApi5;              // Your MtApi5Client
    
    namespace Trading.Execution
    {
        public enum TradeDirection
        {
            Bullish,
            Bearish,
            None,
        }

        public sealed class TimeframeDiversityConfig
        {
            // M15 continuation trigger tuning
            public int M15LookbackBars { get; init; } = 60;

            // Trigger candle strength: body must be at least X% of candle range
            public double MinBodyToRangeRatio { get; init; } = 0.55;

            // Candle must close in direction of trade and close above/below prior swing micro-level
            public int MicroBreakLookbackBars { get; init; } = 6;

            // Optional safety: prevent entry if trigger candle is too large (avoid chasing)
            public double MaxTriggerCandleRangeAtrMultiple { get; init; } = 1.20;

            // Cooldown: after a disarm/expiry, do not instantly re-enter on same symbol
            public TimeSpan ReArmCooldown { get; init; } = TimeSpan.FromMinutes(30);

            // Execution timeframe
            public ENUM_TIMEFRAMES ExecutionTf { get; init; } = ENUM_TIMEFRAMES.PERIOD_M15;

            // How many bars to request each call
            public int BarsToFetch { get; init; } = 120;
        }

        public sealed class ExecutionDecision
        {
            public bool ShouldEnter { get; init; }
            public bool CanEnter { get; set; }
            public TradeDirection Direction { get; init; }
            public string Reason { get; init; } = "";
            public string NewReason { get; set; }
            public DateTime AsOfUtc { get; init; } = DateTime.UtcNow;

            public static ExecutionDecision No(string reason) => new() { ShouldEnter = false, Reason = reason };
            public static ExecutionDecision Yes(TradeDirection dir, string reason) => new() { ShouldEnter = true, Direction = dir, Reason = reason };
        }

        /// <summary>
        /// Non-invasive multi-timeframe execution overlay:
        /// - H1 creates/maintains setup and arms (your existing system).
        /// - This class ONLY looks for an M15 continuation trigger while H1 remains Armed.
        ///
        /// It does NOT alter your MarketState transitions.
        /// </summary>
        public sealed class TimeframeDiversityExecutor
        {
            private readonly MtApi5Client _client;
            private readonly TimeframeDiversityConfig _cfg;

            // prevent duplicate firing per M15 candle
            private readonly ConcurrentDictionary<string, DateTime> _lastM15Processed = new();

            // prevent immediate re-entry after disarm/expiry
            private readonly ConcurrentDictionary<string, DateTime> _cooldownUntil = new();

            // simple guard: entry can only fire once per "armed episode"
            private readonly ConcurrentDictionary<string, Guid> _armedEpisodeId = new();
            private readonly ConcurrentDictionary<string, Guid> _lastEnteredEpisodeId = new();

            public TimeframeDiversityExecutor(TimeframeDiversityConfig cfg)
            {
                _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            }

            /// <summary>
            /// Call this when your H1 logic transitions a symbol to Armed.
            /// (Does NOT change your existing logic; just notify the overlay.)
            /// </summary>
            public void NotifyArmed(string symbol)
            {
                _armedEpisodeId[symbol] = Guid.NewGuid();
            }

            /// <summary>
            /// Call this when your H1 logic disarms/reset-to-neutral OR failed-expansion clears.
            /// This sets a short cooldown so we don't "machine-gun" entries.
            /// </summary>
            public void NotifyDisarmed(string symbol)
            {
                _cooldownUntil[symbol] = DateTime.UtcNow.Add(_cfg.ReArmCooldown);
            }

            /// <summary>
            /// Core evaluation:
            /// - You pass the current H1 context snapshot (from your SymbolContext).
            /// - If H1 is Armed AND still valid, check M15 candle-close for a continuation trigger.
            ///
            /// If it returns ShouldEnter=true, you place the trade using your existing OpenTrade() flow.
            /// </summary>
            public async Task<ExecutionDecision> EvaluateAsync(
                MtApi5Client client,
                string symbol,
                bool h1IsArmed,
                bool h1PriceAccepted,
                bool h1AtrCooled,
                TradeDirection bias,
                double atrH1,
                CancellationToken ct = default,

                // 🔹 NEW — optional H4 context
                bool? h4Weak = null,
                bool? h4Reversal = null,
                double? h4Momentum = null
                )
            {
                if (!h1IsArmed)
                    return ExecutionDecision.No("H1 not armed.");

                if (!h1PriceAccepted)
                    return ExecutionDecision.No("H1 acceptance not valid (PriceAccepted=false).");

                if (!h1AtrCooled)
                    return ExecutionDecision.No("H1 ATR cooldown not satisfied (AtrCooldown=false).");

                if (_cooldownUntil.TryGetValue(symbol, out var until) && DateTime.UtcNow < until)
                    return ExecutionDecision.No($"Re-arm cooldown active until {until:HH:mm:ss}Z.");

                // Ensure we only enter once per armed-episode
                if (_armedEpisodeId.TryGetValue(symbol, out var episodeId) &&
                    _lastEnteredEpisodeId.TryGetValue(symbol, out var enteredEpisodeId) &&
                    episodeId == enteredEpisodeId)
                {
                    return ExecutionDecision.No("Already entered once for this armed episode.");
                }

                // Fetch M15 rates (use your existing symbol normalization if needed)
                var rates = await CopyRatesAsync(client, symbol, _cfg.ExecutionTf, _cfg.BarsToFetch, ct);
                if (rates == null || rates.Length < 30)
                    return ExecutionDecision.No("Not enough M15 bars.");

                // Only evaluate on M15 candle close
                var lastClosed = rates[^2];
                if (_lastM15Processed.TryGetValue(symbol, out var lastTime) && lastTime == lastClosed.time)
                    return ExecutionDecision.No("M15 candle already processed.");

                _lastM15Processed[symbol] = lastClosed.time;

                // Evaluate continuation trigger
                var decision = EvaluateM15Continuation(symbol, rates, lastClosed, bias, atrH1);

                if (decision.ShouldEnter)
                {
                    // mark entered for this episode
                    if (_armedEpisodeId.TryGetValue(symbol, out var ep))
                        _lastEnteredEpisodeId[symbol] = ep;
                }

                return decision;
            }

            // -----------------------
            // M15 Continuation logic
            // -----------------------
            private ExecutionDecision EvaluateM15Continuation(
                string symbol,
                MqlRates[] m15,
                MqlRates lastClosed,
                TradeDirection bias,
                double atrH1)
            {
                double range = Math.Max(0.0000001, lastClosed.high - lastClosed.low);
                double body = Math.Abs(lastClosed.close - lastClosed.open);
                double bodyToRange = body / range;

                bool isBull = lastClosed.close > lastClosed.open;
                bool isBear = lastClosed.close < lastClosed.open;

                // 1) Candle must align with bias + have decent body (not a doji)
                if (bias == TradeDirection.Bullish)
                {
                    if (!isBull) return ExecutionDecision.No("M15 trigger: candle not bullish.");
                }
                else
                {
                    if (!isBear) return ExecutionDecision.No("M15 trigger: candle not bearish.");
                }

                if (bodyToRange < _cfg.MinBodyToRangeRatio)
                    return ExecutionDecision.No($"M15 trigger: body too small (body/range={bodyToRange:F2}).");

                // 2) Optional: don’t chase oversized M15 candle relative to H1 ATR
                if (atrH1 > 0)
                {
                    double atrMultiple = range / atrH1;
                    if (atrMultiple > _cfg.MaxTriggerCandleRangeAtrMultiple)
                        return ExecutionDecision.No($"M15 trigger: candle too large ({atrMultiple:F2}x H1 ATR).");
                }

                // 3) Micro-break condition (simple structure trigger)
                int lb = Math.Clamp(_cfg.MicroBreakLookbackBars, 3, 20);
                var window = m15.Skip(Math.Max(0, m15.Length - (lb + 2))).Take(lb).ToArray();

                double prevHigh = window.Max(r => r.high);
                double prevLow = window.Min(r => r.low);

                bool brokeMicro = bias == TradeDirection.Bullish
                    ? lastClosed.close > prevHigh
                    : lastClosed.close < prevLow;

                if (!brokeMicro)
                    return ExecutionDecision.No("M15 trigger: no micro-break of recent structure.");

                // If we got here, we have a clean continuation trigger from a base.
                return ExecutionDecision.Yes(bias, "M15 continuation trigger: body strong + micro-break aligned with armed H1 setup.");
            }

            // -----------------------
            // MtApi5 helper
            // -----------------------
            private Task<MqlRates[]> CopyRatesAsync(MtApi5Client client, string symbol, ENUM_TIMEFRAMES tf, int bars, CancellationToken ct)
            {
                // MtApi5 CopyRates is sync; wrap in Task.Run to avoid blocking if you call from async loop
                return Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();

                    // NOTE: adapt this if you use ".s" suffix logic
                    // Example: symbolToUse = TradingConfig.Mode == XauUsdOnly ? symbol + ".s" : symbol;
                    string symbolToUse = symbol;

                    // startPos=1 to avoid current forming candle; count=bars
                    client.CopyRates(symbolToUse, tf, 1, bars, out MqlRates[] rates);
                    return rates;
                }, ct);
            }
        }
    }

}
