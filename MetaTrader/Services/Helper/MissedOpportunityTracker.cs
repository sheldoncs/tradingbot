namespace MetaTrader.Services.Helper
{
    using StackExchange.Redis;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// SentinelFX observation-only tracker for rejected/blocked entry candidates.
    /// It never opens, closes, modifies, or approves a trade.
    ///
    /// Purpose:
    ///   TradePerformanceTracker answers: "How did trades we took perform?"
    ///   MissedOpportunityTracker answers: "What happened after meaningful setups we rejected?"
    ///
    /// Typical flow:
    ///   1. When an engine/filter rejects a meaningful candidate, call RegisterRejectedCandidateAsync().
    ///   2. On each strategy cycle, call UpdateOpenCandidatesAsync() with the symbol's current bid/ask.
    ///   3. Mature candidates automatically finalize after ObservationMinutes and are written to CSV.
    ///
    /// All bot processes may share the same Redis and StudyId. Redis provides the global sequence and
    /// candidate state; a named Windows mutex serializes the combined CSV writer.
    /// </summary>
    public sealed class MissedOpportunityTracker
    {
        private readonly IDatabase _db;
        private readonly string _studyId;
        private readonly string _prefix;

        private const string CsvFolder = @"C:\SentinelFX\TradeStudy";
        private const string CsvMutexName = @"Global\SentinelFX_MissedOpportunity_CsvWriter";

        // Prevent one per-second rejection from becoming thousands of rows.
        public int DuplicateSuppressionMinutes { get; set; } = 15;

        // Candidate is finalized after this horizon. MFE/MAE checkpoints are also captured at 15/30/60.
        public int ObservationMinutes { get; set; } = 60;

        // Optional: only classify a completed observation as a meaningful missed opportunity when
        // favorable excursion reaches this many ATR. This affects classification only, never trading.
        public double MeaningfulMoveAtr { get; set; } = 0.50;

        private string SequenceKey => $"{_prefix}:sequence";
        private string ActiveIndexKey => $"{_prefix}:active:index";
        private string CompletedRecordsKey => $"{_prefix}:completed:records";

        public MissedOpportunityTracker(IConnectionMultiplexer redis, string studyId = "2026-10-02")
        {
            if (redis == null) throw new ArgumentNullException(nameof(redis));

            _db = redis.GetDatabase();
            _studyId = string.IsNullOrWhiteSpace(studyId) ? "2026-10-02" : studyId.Trim();
            _prefix = $"sentinelfx:missed:{_studyId}";
        }

        public sealed class RejectedCandidateInput
        {
            public string Symbol { get; set; }
            public string BotType { get; set; }
            public string Direction { get; set; }          // BUY / SELL
            public string Engine { get; set; }             // LiveImpulse, TrendContinuation, etc.
            public string BlockReason { get; set; }        // Extended / low momentum / S/R / chop ...
            public string MarketState { get; set; }
            public DateTime TimeUtc { get; set; } = DateTime.UtcNow;

            // Use executable entry-side price where possible:
            // BUY -> ask, SELL -> bid.
            public double ReferencePrice { get; set; }
            public double Bid { get; set; }
            public double Ask { get; set; }
            public double Ema9 { get; set; }
            public double Ema21 { get; set; }
            public double Atr { get; set; }
            public double DistanceFromEma9Atr { get; set; }
            public double Momentum { get; set; }

            // Optional pre-calculated diagnostics. NULL means "not measured"; zero
            // is a legitimate measurement. Existing callers can omit these safely.
            public double? BodyAtr { get; set; }
            public double? BreakAtr { get; set; }
            public double? CandlePosition { get; set; }

            // Optional inputs used to calculate missing diagnostics at registration.
            // LiveOpen = current H1 open (previous CLOSED H1 close if using the
            // existing synthetic live candle); RecentHigh/Low = CLOSED H1 structure.
            public double LiveOpen { get; set; }
            public double RecentHigh { get; set; }
            public double RecentLow { get; set; }

            // Candle position MUST NOT use the synthetic open-to-midpoint range.
            // Pass real MT5 current-H1 extremes and set this flag only when verified.
            public bool HasRealIntrabarExtremes { get; set; }
            public double RealIntrabarHigh { get; set; }
            public double RealIntrabarLow { get; set; }
            public string Notes { get; set; }
        }

        private sealed class ActiveCandidate
        {
            public long Sequence { get; set; }
            public string CandidateId { get; set; }
            public string Symbol { get; set; }
            public string BotType { get; set; }
            public string Direction { get; set; }
            public string Engine { get; set; }
            public string BlockReason { get; set; }
            public string MarketState { get; set; }
            public DateTime TimeUtc { get; set; }
            public DateTime LastUpdateUtc { get; set; }
            public double ReferencePrice { get; set; }
            public double Bid { get; set; }
            public double Ask { get; set; }
            public double Ema9 { get; set; }
            public double Ema21 { get; set; }
            public double Atr { get; set; }
            public double DistanceFromEma9Atr { get; set; }
            public double Momentum { get; set; }
            public double? BodyAtr { get; set; }
            public double? BreakAtr { get; set; }
            public double? CandlePosition { get; set; }
            public string Notes { get; set; }

            public double MfeUsdOrPrice { get; set; }
            public double MaeUsdOrPrice { get; set; }
            public double MfePrice { get; set; }
            public double MaePrice { get; set; }
            public double MfeAtr { get; set; }
            public double MaeAtr { get; set; }

            public double? Mfe15Atr { get; set; }
            public double? Mae15Atr { get; set; }
            public double? Mfe30Atr { get; set; }
            public double? Mae30Atr { get; set; }
            public double? Mfe60Atr { get; set; }
            public double? Mae60Atr { get; set; }
        }

        public sealed class CompletedCandidate
        {
            public long Sequence { get; set; }
            public string CandidateId { get; set; }
            public string Symbol { get; set; }
            public string BotType { get; set; }
            public string Direction { get; set; }
            public string Engine { get; set; }
            public string BlockReason { get; set; }
            public string MarketState { get; set; }
            public DateTime TimeUtc { get; set; }
            public DateTime CompletedUtc { get; set; }
            public double ReferencePrice { get; set; }
            public double Ema9 { get; set; }
            public double Ema21 { get; set; }
            public double Atr { get; set; }
            public double DistanceFromEma9Atr { get; set; }
            public double Momentum { get; set; }
            public double? BodyAtr { get; set; }
            public double? BreakAtr { get; set; }
            public double? CandlePosition { get; set; }
            public string Notes { get; set; }

            public double MfePrice { get; set; }
            public double MaePrice { get; set; }
            public double MfeAtr { get; set; }
            public double MaeAtr { get; set; }
            public double? Mfe15Atr { get; set; }
            public double? Mae15Atr { get; set; }
            public double? Mfe30Atr { get; set; }
            public double? Mae30Atr { get; set; }
            public double? Mfe60Atr { get; set; }
            public double? Mae60Atr { get; set; }
            public bool MeaningfulMiss { get; set; }
            public string Classification { get; set; }
        }

        /// <summary>
        /// Registers one rejected candidate. Repeated identical rejection reasons for the same
        /// symbol/direction/engine are suppressed for DuplicateSuppressionMinutes.
        /// Returns true only when a new observation was actually created.
        /// </summary>
        public async Task<bool> RegisterRejectedCandidateAsync(RejectedCandidateInput input)
        {
            if (input == null || string.IsNullOrWhiteSpace(input.Symbol) ||
                string.IsNullOrWhiteSpace(input.Direction) || string.IsNullOrWhiteSpace(input.Engine) ||
                input.ReferencePrice <= 0)
                return false;

            string direction = NormalizeDirection(input.Direction);
            string signature = BuildSignature(input.Symbol, direction, input.Engine, input.BlockReason);
            string suppressionKey = $"{_prefix}:suppress:{signature}";

            // Atomic NX gate across all Windows bot processes sharing Redis.
            bool accepted = await _db.StringSetAsync(
                suppressionKey,
                "1",
                TimeSpan.FromMinutes(Math.Max(1, DuplicateSuppressionMinutes)),
                When.NotExists);

            if (!accepted)
                return false;

            // Resolve measurements before persisting. Missing data stays NULL
            // throughout Redis -> completed record -> CSV (blank cells, not zeros).
            double? bodyAtr = ResolveBodyAtr(input);
            double? breakAtr = ResolveBreakAtr(input, direction);
            double? candlePosition = ResolveCandlePosition(input, direction);

            long sequence = await _db.StringIncrementAsync(SequenceKey);
            string candidateId = $"{sequence:D8}";

            var c = new ActiveCandidate
            {
                Sequence = sequence,
                CandidateId = candidateId,
                Symbol = input.Symbol.Trim(),
                BotType = input.BotType ?? "",
                Direction = direction,
                Engine = input.Engine.Trim(),
                BlockReason = input.BlockReason ?? "",
                MarketState = input.MarketState ?? "",
                TimeUtc = input.TimeUtc == default ? DateTime.UtcNow : input.TimeUtc.ToUniversalTime(),
                LastUpdateUtc = DateTime.UtcNow,
                ReferencePrice = input.ReferencePrice,
                Bid = input.Bid,
                Ask = input.Ask,
                Ema9 = input.Ema9,
                Ema21 = input.Ema21,
                Atr = input.Atr,
                DistanceFromEma9Atr = input.DistanceFromEma9Atr,
                Momentum = input.Momentum,
                BodyAtr = bodyAtr,
                BreakAtr = breakAtr,
                CandlePosition = candlePosition,
                Notes = input.Notes ?? "",
                MfePrice = input.ReferencePrice,
                MaePrice = input.ReferencePrice,
                MfeAtr = 0,
                MaeAtr = 0
            };

            string key = ActiveKey(candidateId);
            string json = JsonSerializer.Serialize(c);
            await _db.StringSetAsync(key, json, TimeSpan.FromHours(24));
            await _db.SetAddAsync(ActiveIndexKey, candidateId);

            return true;
        }

        /// <summary>
        /// Call this from the normal symbol loop. It updates every active observation for this symbol.
        /// For BUY observations favorable movement is measured from reference ask toward current bid;
        /// for SELL observations from reference bid toward current ask. This is deliberately conservative.
        /// </summary>
        public async Task UpdateOpenCandidatesAsync(string symbol, double bid, double ask, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(symbol) || bid <= 0 || ask <= 0)
                return;

            DateTime now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
            RedisValue[] ids = await _db.SetMembersAsync(ActiveIndexKey);

            foreach (RedisValue idValue in ids)
            {
                string id = idValue.ToString();
                string key = ActiveKey(id);
                RedisValue raw = await _db.StringGetAsync(key);

                if (!raw.HasValue)
                {
                    await _db.SetRemoveAsync(ActiveIndexKey, id);
                    continue;
                }

                ActiveCandidate c;
                try { c = JsonSerializer.Deserialize<ActiveCandidate>(raw.ToString()); }
                catch { continue; }

                if (c == null || !string.Equals(c.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                    continue;

                double executablePrice = c.Direction == "BUY" ? bid : ask;
                double favorablePriceMove = c.Direction == "BUY"
                    ? executablePrice - c.ReferencePrice
                    : c.ReferencePrice - executablePrice;

                double adversePriceMove = c.Direction == "BUY"
                    ? c.ReferencePrice - executablePrice
                    : executablePrice - c.ReferencePrice;

                if (favorablePriceMove > 0)
                {
                    double favorablePrice = executablePrice;
                    double favorableAtr = c.Atr > 0 ? favorablePriceMove / c.Atr : 0;
                    if (favorableAtr > c.MfeAtr)
                    {
                        c.MfeAtr = favorableAtr;
                        c.MfePrice = favorablePrice;
                    }
                }

                if (adversePriceMove > 0)
                {
                    double adversePrice = executablePrice;
                    double adverseAtr = c.Atr > 0 ? adversePriceMove / c.Atr : 0;
                    if (adverseAtr > c.MaeAtr)
                    {
                        c.MaeAtr = adverseAtr;
                        c.MaePrice = adversePrice;
                    }
                }

                double ageMinutes = (now - c.TimeUtc).TotalMinutes;
                CaptureCheckpoint(c, ageMinutes);
                c.LastUpdateUtc = now;

                if (ageMinutes >= Math.Max(1, ObservationMinutes))
                {
                    await CompleteCandidateAsync(c, now);
                }
                else
                {
                    await _db.StringSetAsync(key, JsonSerializer.Serialize(c), TimeSpan.FromHours(24));
                }
            }
        }

        private static void CaptureCheckpoint(ActiveCandidate c, double ageMinutes)
        {
            if (ageMinutes >= 15 && !c.Mfe15Atr.HasValue)
            {
                c.Mfe15Atr = c.MfeAtr;
                c.Mae15Atr = c.MaeAtr;
            }
            if (ageMinutes >= 30 && !c.Mfe30Atr.HasValue)
            {
                c.Mfe30Atr = c.MfeAtr;
                c.Mae30Atr = c.MaeAtr;
            }
            if (ageMinutes >= 60 && !c.Mfe60Atr.HasValue)
            {
                c.Mfe60Atr = c.MfeAtr;
                c.Mae60Atr = c.MaeAtr;
            }
        }

        private async Task CompleteCandidateAsync(ActiveCandidate c, DateTime completedUtc)
        {
            // NX completion key prevents duplicate finalization when several processes update simultaneously.
            string completedGuardKey = $"{_prefix}:completed:{c.CandidateId}";
            bool ownsCompletion = await _db.StringSetAsync(
                completedGuardKey,
                "1",
                TimeSpan.FromDays(30),
                When.NotExists);

            if (!ownsCompletion)
            {
                await _db.KeyDeleteAsync(ActiveKey(c.CandidateId));
                await _db.SetRemoveAsync(ActiveIndexKey, c.CandidateId);
                return;
            }

            CaptureCheckpoint(c, 60);

            bool meaningful = c.MfeAtr >= MeaningfulMoveAtr;
            string classification;
            if (meaningful && c.MfeAtr > c.MaeAtr)
                classification = "Potential missed opportunity";
            else if (!meaningful && c.MaeAtr > c.MfeAtr)
                classification = "Good block / adverse move dominated";
            else
                classification = "Inconclusive";

            var completed = new CompletedCandidate
            {
                Sequence = c.Sequence,
                CandidateId = c.CandidateId,
                Symbol = c.Symbol,
                BotType = c.BotType,
                Direction = c.Direction,
                Engine = c.Engine,
                BlockReason = c.BlockReason,
                MarketState = c.MarketState,
                TimeUtc = c.TimeUtc,
                CompletedUtc = completedUtc,
                ReferencePrice = c.ReferencePrice,
                Ema9 = c.Ema9,
                Ema21 = c.Ema21,
                Atr = c.Atr,
                DistanceFromEma9Atr = c.DistanceFromEma9Atr,
                Momentum = c.Momentum,
                BodyAtr = c.BodyAtr,
                BreakAtr = c.BreakAtr,
                CandlePosition = c.CandlePosition,
                Notes = c.Notes,
                MfePrice = c.MfePrice,
                MaePrice = c.MaePrice,
                MfeAtr = c.MfeAtr,
                MaeAtr = c.MaeAtr,
                Mfe15Atr = c.Mfe15Atr,
                Mae15Atr = c.Mae15Atr,
                Mfe30Atr = c.Mfe30Atr,
                Mae30Atr = c.Mae30Atr,
                Mfe60Atr = c.Mfe60Atr,
                Mae60Atr = c.Mae60Atr,
                MeaningfulMiss = meaningful,
                Classification = classification
            };

            await _db.SortedSetAddAsync(
                CompletedRecordsKey,
                JsonSerializer.Serialize(completed),
                completed.Sequence);

            await _db.KeyDeleteAsync(ActiveKey(c.CandidateId));
            await _db.SetRemoveAsync(ActiveIndexKey, c.CandidateId);

            try { await SaveCsvToDiskAsync(); }
            catch
            {
                // Observation logging must never interfere with trading.
            }
        }

        public async Task<string> ExportCsvAsync()
        {
            RedisValue[] rows = await _db.SortedSetRangeByRankAsync(CompletedRecordsKey, 0, -1, Order.Ascending);
            var sb = new StringBuilder();
            sb.AppendLine("Sequence,CandidateId,TimeUtc,CompletedUtc,Symbol,BotType,Direction,Engine,BlockReason,MarketState,ReferencePrice,EMA9,EMA21,ATR,DistanceFromEMA9ATR,Momentum,BodyATR,BreakATR,CandlePosition,MFEPrice,MAEPrice,MFE_ATR,MAE_ATR,MFE_15m_ATR,MAE_15m_ATR,MFE_30m_ATR,MAE_30m_ATR,MFE_60m_ATR,MAE_60m_ATR,MeaningfulMiss,Classification,Notes");

            foreach (RedisValue row in rows)
            {
                CompletedCandidate c;
                try { c = JsonSerializer.Deserialize<CompletedCandidate>(row.ToString()); }
                catch { continue; }
                if (c == null) continue;

                sb.Append(c.Sequence).Append(',')
                  .Append(Csv(c.CandidateId)).Append(',')
                  .Append(Csv(c.TimeUtc.ToString("O", CultureInfo.InvariantCulture))).Append(',')
                  .Append(Csv(c.CompletedUtc.ToString("O", CultureInfo.InvariantCulture))).Append(',')
                  .Append(Csv(c.Symbol)).Append(',')
                  .Append(Csv(c.BotType)).Append(',')
                  .Append(Csv(c.Direction)).Append(',')
                  .Append(Csv(c.Engine)).Append(',')
                  .Append(Csv(c.BlockReason)).Append(',')
                  .Append(Csv(c.MarketState)).Append(',')
                  .Append(Num(c.ReferencePrice)).Append(',')
                  .Append(Num(c.Ema9)).Append(',')
                  .Append(Num(c.Ema21)).Append(',')
                  .Append(Num(c.Atr)).Append(',')
                  .Append(Num(c.DistanceFromEma9Atr)).Append(',')
                  .Append(Num(c.Momentum)).Append(',')
                  .Append(NullableNum(c.BodyAtr)).Append(',')
                  .Append(NullableNum(c.BreakAtr)).Append(',')
                  .Append(NullableNum(c.CandlePosition)).Append(',')
                  .Append(Num(c.MfePrice)).Append(',')
                  .Append(Num(c.MaePrice)).Append(',')
                  .Append(Num(c.MfeAtr)).Append(',')
                  .Append(Num(c.MaeAtr)).Append(',')
                  .Append(NullableNum(c.Mfe15Atr)).Append(',')
                  .Append(NullableNum(c.Mae15Atr)).Append(',')
                  .Append(NullableNum(c.Mfe30Atr)).Append(',')
                  .Append(NullableNum(c.Mae30Atr)).Append(',')
                  .Append(NullableNum(c.Mfe60Atr)).Append(',')
                  .Append(NullableNum(c.Mae60Atr)).Append(',')
                  .Append(c.MeaningfulMiss ? "true" : "false").Append(',')
                  .Append(Csv(c.Classification)).Append(',')
                  .Append(Csv(c.Notes))
                  .AppendLine();
            }

            return sb.ToString();
        }

        public async Task SaveCsvToDiskAsync()
        {
            string csv = await ExportCsvAsync();
            Directory.CreateDirectory(CsvFolder);

            string safeStudyId = string.Concat(_studyId.Select(ch =>
                Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

            string finalPath = Path.Combine(
                CsvFolder,
                $"SentinelFX_MissedOpportunities_{safeStudyId}.csv");

            using var mutex = new Mutex(false, CsvMutexName);
            bool hasMutex = false;
            try
            {
                try { hasMutex = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { hasMutex = true; }

                if (!hasMutex)
                    return;

                string tempPath = finalPath + ".tmp." + Environment.ProcessId;
                try
                {
                    await File.WriteAllTextAsync(tempPath, csv, new UTF8Encoding(true));
                    File.Move(tempPath, finalPath, true);
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); } catch { }
                    }
                }
            }
            finally
            {
                if (hasMutex)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        // ------------------------------------------------------------
        // CANDIDATE DIAGNOSTICS (measurement only; never changes trading)
        // ------------------------------------------------------------
        private static bool Finite(double x) =>
            !double.IsNaN(x) && !double.IsInfinity(x);

        private static double? ResolveBodyAtr(RejectedCandidateInput input)
        {
            if (input.BodyAtr.HasValue)
                return Finite(input.BodyAtr.Value) && input.BodyAtr.Value >= 0
                    ? input.BodyAtr : null;

            if (!Finite(input.Atr) || input.Atr <= 0 ||
                !Finite(input.LiveOpen) || input.LiveOpen <= 0 ||
                !Finite(input.Bid) || !Finite(input.Ask) ||
                input.Bid <= 0 || input.Ask < input.Bid)
                return null;

            double midpoint = (input.Bid + input.Ask) / 2.0;
            return Math.Abs(midpoint - input.LiveOpen) / input.Atr;
        }

        private static double? ResolveBreakAtr(RejectedCandidateInput input, string direction)
        {
            if (input.BreakAtr.HasValue)
                return Finite(input.BreakAtr.Value) ? input.BreakAtr : null;

            if (!Finite(input.Atr) || input.Atr <= 0)
                return null;

            if (direction == "BUY" && Finite(input.Ask) && input.Ask > 0 &&
                Finite(input.RecentHigh) && input.RecentHigh > 0)
                return (input.Ask - input.RecentHigh) / input.Atr;

            if (direction == "SELL" && Finite(input.Bid) && input.Bid > 0 &&
                Finite(input.RecentLow) && input.RecentLow > 0)
                return (input.RecentLow - input.Bid) / input.Atr;

            return null;
        }

        private static double? ResolveCandlePosition(RejectedCandidateInput input, string direction)
        {
            // Even an explicitly supplied position is not accepted without a
            // verified real-H1-range flag. This prevents synthetic candle leakage.
            if (!input.HasRealIntrabarExtremes ||
                !Finite(input.RealIntrabarHigh) || !Finite(input.RealIntrabarLow) ||
                input.RealIntrabarHigh <= input.RealIntrabarLow ||
                !Finite(input.Bid) || !Finite(input.Ask) ||
                input.Bid <= 0 || input.Ask < input.Bid)
                return null;

            double mid = (input.Bid + input.Ask) / 2.0;
            if (mid < input.RealIntrabarLow || mid > input.RealIntrabarHigh)
                return null; // stale/inconsistent extrema; do not hide bad input

            double range = input.RealIntrabarHigh - input.RealIntrabarLow;
            double position = direction == "BUY"
                ? (mid - input.RealIntrabarLow) / range
                : (input.RealIntrabarHigh - mid) / range;

            return Finite(position) ? position : null;
        }

        private string ActiveKey(string id) => $"{_prefix}:active:{id}";

        private static string BuildSignature(string symbol, string direction, string engine, string reason)
        {
            string raw = $"{symbol}|{direction}|{engine}|{reason}".ToUpperInvariant();
            var sb = new StringBuilder(raw.Length);
            foreach (char ch in raw)
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            return sb.ToString();
        }

        private static string NormalizeDirection(string direction)
        {
            string d = (direction ?? "").Trim().ToUpperInvariant();
            return d.Contains("SELL") ? "SELL" : "BUY";
        }

        private static string Csv(string value)
        {
            value ??= "";
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string Num(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
        private static string NullableNum(double? value) => value.HasValue ? Num(value.Value) : "";
    }

}
