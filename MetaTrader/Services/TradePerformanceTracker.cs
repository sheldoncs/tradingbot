namespace MetaTrader.Services
{
    using System;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;
    using StackExchange.Redis;

    /// <summary>
    /// SentinelFX measurement-only 50/100 trade study tracker.
    /// Shared Redis gives every bot one global completed-trade sequence.
    /// This class NEVER opens, closes, modifies, blocks or approves a trade.
    /// </summary>
    using System;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using StackExchange.Redis;

    /// <summary>
    /// SentinelFX measurement-only 50/100 trade study tracker.
    /// Shared Redis gives every bot one global completed-trade sequence.
    /// This class NEVER opens, closes, modifies, blocks or approves a trade.
    /// </summary>
    public sealed class TradePerformanceTracker
    {
        private readonly IDatabase _db;
        private readonly string _studyId;
        private const int FirstCheckpoint = 50;
        private const int FinalCheckpoint = 100;

        // One shared Windows CSV for all bots.
        // Change this path here if you prefer another folder.
        private const string CsvFolder = @"C:\SentinelFX\TradeStudy";

        // Named mutex protects the CSV when two separate bot processes
        // finish trades at nearly the same time.
        private const string CsvMutexName =
            @"Global\SentinelFX_TradeStudy_CsvWriter";

        public TradePerformanceTracker(IConnectionMultiplexer redis, string studyId = "2026-10-02")
        {
            if (redis == null) throw new ArgumentNullException(nameof(redis));
            _db = redis.GetDatabase();
            _studyId = string.IsNullOrWhiteSpace(studyId) ? "2026-10-02" : studyId.Trim();
        }

        public sealed class OpenTradeObservation
        {
            public ulong Ticket { get; set; }
            public string Symbol { get; set; } = "";
            public string BotType { get; set; } = "";
            public string Side { get; set; } = "";
            public string EntryModel { get; set; } = "";
            public string EntryReason { get; set; } = "";
            public DateTime OpenTimeUtc { get; set; }
            public double EntryPrice { get; set; }
            public double Volume { get; set; }
            public double InitialStopLoss { get; set; }
            public double InitialTakeProfit { get; set; }
            public double EntryAtr { get; set; }

            // Frozen entry-location diagnostics from the exact S/R approval.
            public double EntrySrWall { get; set; }
            public double EntrySrClearanceAtr { get; set; }
            public bool EntrySrAhead { get; set; }
            public double EntryEma9DistanceAtr { get; set; }

            public double MfeUsd { get; set; }
            public double MaeUsd { get; set; }
            public double MfePrice { get; set; }
            public double MaePrice { get; set; }
            public DateTime LastUpdateUtc { get; set; }
        }

        public sealed class CompletedTradeObservation
        {
            public long GlobalSequence { get; set; }
            public ulong Ticket { get; set; }
            public string Symbol { get; set; } = "";
            public string BotType { get; set; } = "";
            public string Side { get; set; } = "";
            public string EntryModel { get; set; } = "";
            public string EntryReason { get; set; } = "";
            public string ExitReason { get; set; } = "";
            public DateTime OpenTimeUtc { get; set; }
            public DateTime CloseTimeUtc { get; set; }
            public double EntryPrice { get; set; }
            public double ExitPrice { get; set; }
            public double Volume { get; set; }
            public double InitialStopLoss { get; set; }
            public double InitialTakeProfit { get; set; }
            public double RealizedProfitUsd { get; set; }
            public double MfeUsd { get; set; }
            public double MaeUsd { get; set; }
            public double MfePrice { get; set; }
            public double MaePrice { get; set; }
            public double EntryAtr { get; set; }

            // Frozen entry-location diagnostics copied from the open trade.
            public double EntrySrWall { get; set; }
            public double EntrySrClearanceAtr { get; set; }
            public bool EntrySrAhead { get; set; }
            public double EntryEma9DistanceAtr { get; set; }

            public double MfeAtr { get; set; }
            public double MaeAtr { get; set; }
            public double CapturePercent { get; set; }
            public double DurationMinutes { get; set; }
            public string Result { get; set; } = "";
        }

        public sealed class StudySummary
        {
            public int CompletedTrades { get; set; }
            public int RemainingTo50 { get; set; }
            public int RemainingTo100 { get; set; }
            public int Wins { get; set; }
            public int Losses { get; set; }
            public int Breakeven { get; set; }
            public double WinRatePercent { get; set; }
            public double GrossProfitUsd { get; set; }
            public double GrossLossUsd { get; set; }
            public double NetProfitUsd { get; set; }
            public double AverageWinnerUsd { get; set; }
            public double AverageLoserUsd { get; set; }
            public double ProfitFactor { get; set; }
            public double ExpectancyUsdPerTrade { get; set; }
            public double AverageMfeUsd { get; set; }
            public double AverageMaeUsd { get; set; }
            public double WinnerMfeCapturePercent { get; set; }
            public double LargestWinnerUsd { get; set; }
            public double LargestLoserUsd { get; set; }
            public bool Reached50 { get; set; }
            public bool Reached100 { get; set; }
        }

        private string Prefix => $"sentinelfx:study:{_studyId}";
        private string SequenceKey => $"{Prefix}:sequence";
        private string ActiveKey(ulong ticket) => $"{Prefix}:active:{ticket}";
        private string CompletedTicketsKey => $"{Prefix}:completed:tickets";
        private string CompletedHashKey => $"{Prefix}:completed:records";
        private string SequenceIndexKey => $"{Prefix}:completed:sequence";

        /// <summary>Call once after the REAL MT5 position ticket has been resolved.</summary>
        public async Task RegisterOpenTradeAsync(
            ulong ticket, string symbol, string botType, string side,
            string entryModel, string entryReason, DateTime openTimeUtc,
            double entryPrice, double volume, double initialStopLoss,
            double initialTakeProfit, double entryAtr,
            double entrySrWall = 0.0,
            double entrySrClearanceAtr = 0.0,
            bool entrySrAhead = false,
            double entryEma9DistanceAtr = 0.0)
        {
            if (ticket == 0 || string.IsNullOrWhiteSpace(symbol)) return;

            string ticketText = ticket.ToString(CultureInfo.InvariantCulture);
            if (await _db.SetContainsAsync(CompletedTicketsKey, ticketText)) return;

            string key = ActiveKey(ticket);
            if (await _db.KeyExistsAsync(key)) return; // do not erase MFE/MAE

            var item = new OpenTradeObservation
            {
                Ticket = ticket,
                Symbol = symbol,
                BotType = botType ?? "",
                Side = side ?? "",
                EntryModel = entryModel ?? "",
                EntryReason = entryReason ?? "",
                OpenTimeUtc = EnsureUtc(openTimeUtc),
                EntryPrice = entryPrice,
                Volume = volume,
                InitialStopLoss = initialStopLoss,
                InitialTakeProfit = initialTakeProfit,
                EntryAtr = entryAtr,

                // Freeze these values at entry; never recalculate them later.
                EntrySrWall = entrySrWall,
                EntrySrClearanceAtr = entrySrClearanceAtr,
                EntrySrAhead = entrySrAhead,
                EntryEma9DistanceAtr = entryEma9DistanceAtr,

                MfeUsd = 0,
                MaeUsd = 0,
                MfePrice = entryPrice,
                MaePrice = entryPrice,
                LastUpdateUtc = DateTime.UtcNow
            };

            await _db.StringSetAsync(key, JsonSerializer.Serialize(item));
        }

        /// <summary>
        /// Call during normal open-position management.
        /// currentPrice should preferably be executable price: Bid for BUY, Ask for SELL.
        /// </summary>
        public async Task UpdateOpenTradeAsync(ulong ticket, double currentProfitUsd, double currentPrice)
        {
            if (ticket == 0) return;
            string key = ActiveKey(ticket);
            RedisValue raw = await _db.StringGetAsync(key);
            if (!raw.HasValue) return;

            OpenTradeObservation? item;
            try { item = JsonSerializer.Deserialize<OpenTradeObservation>(raw.ToString()); }
            catch { return; }
            if (item == null) return;

            if (currentProfitUsd > item.MfeUsd)
            {
                item.MfeUsd = currentProfitUsd;
                item.MfePrice = currentPrice;
            }

            if (currentProfitUsd < item.MaeUsd)
            {
                item.MaeUsd = currentProfitUsd;
                item.MaePrice = currentPrice;
            }

            item.LastUpdateUtc = DateTime.UtcNow;
            await _db.StringSetAsync(key, JsonSerializer.Serialize(item));
        }

        /// <summary>
        /// Call only after MT5 confirms the position is closed and final realized P/L is known.
        /// Redis/Lua makes duplicate-ticket protection and global numbering atomic.
        /// </summary>
        public async Task<CompletedTradeObservation?> CompleteTradeAsync(
            ulong ticket, DateTime closeTimeUtc, double exitPrice,
            double realizedProfitUsd, string exitReason)
        {
            if (ticket == 0) return null;

            string ticketText = ticket.ToString(CultureInfo.InvariantCulture);
            if (await _db.SetContainsAsync(CompletedTicketsKey, ticketText)) return null;

            RedisValue raw = await _db.StringGetAsync(ActiveKey(ticket));
            if (!raw.HasValue) return null; // never invent missing entry/MFE/MAE

            OpenTradeObservation? open;
            try { open = JsonSerializer.Deserialize<OpenTradeObservation>(raw.ToString()); }
            catch { return null; }
            if (open == null) return null;

            DateTime closeUtc = EnsureUtc(closeTimeUtc);
            var completed = new CompletedTradeObservation
            {
                Ticket = ticket,
                Symbol = open.Symbol,
                BotType = open.BotType,
                Side = open.Side,
                EntryModel = open.EntryModel,
                EntryReason = open.EntryReason,
                ExitReason = exitReason ?? "",
                OpenTimeUtc = open.OpenTimeUtc,
                CloseTimeUtc = closeUtc,
                EntryPrice = open.EntryPrice,
                ExitPrice = exitPrice,
                Volume = open.Volume,
                InitialStopLoss = open.InitialStopLoss,
                InitialTakeProfit = open.InitialTakeProfit,
                RealizedProfitUsd = realizedProfitUsd,
                MfeUsd = open.MfeUsd,
                MaeUsd = open.MaeUsd,
                MfePrice = open.MfePrice,
                MaePrice = open.MaePrice,
                EntryAtr = open.EntryAtr,
                EntrySrWall = open.EntrySrWall,
                EntrySrClearanceAtr = open.EntrySrClearanceAtr,
                EntrySrAhead = open.EntrySrAhead,
                EntryEma9DistanceAtr = open.EntryEma9DistanceAtr,
                DurationMinutes = Math.Max(0, (closeUtc - open.OpenTimeUtc).TotalMinutes),
                Result = realizedProfitUsd > 0.000001 ? "WIN" :
                         realizedProfitUsd < -0.000001 ? "LOSS" : "BREAKEVEN"
            };

            if (open.EntryAtr > 0)
            {
                completed.MfeAtr = Math.Abs(open.MfePrice - open.EntryPrice) / open.EntryAtr;
                completed.MaeAtr = Math.Abs(open.MaePrice - open.EntryPrice) / open.EntryAtr;
            }

            completed.CapturePercent =
                completed.RealizedProfitUsd > 0 && completed.MfeUsd > 0
                    ? completed.RealizedProfitUsd / completed.MfeUsd * 100.0
                    : 0.0;

            const string lua = @"
local tickets = KEYS[1]
local seqKey = KEYS[2]
local records = KEYS[3]
local seqIndex = KEYS[4]
local active = KEYS[5]
local ticket = ARGV[1]
local json = ARGV[2]

if redis.call('SISMEMBER', tickets, ticket) == 1 then
    return {0, 0}
end

local seq = redis.call('INCR', seqKey)
redis.call('SADD', tickets, ticket)
redis.call('HSET', records, ticket, json)
redis.call('HSET', seqIndex, tostring(seq), ticket)
redis.call('DEL', active)
return {1, seq}
";

            RedisResult result = await _db.ScriptEvaluateAsync(
                lua,
                new RedisKey[] {
                CompletedTicketsKey, SequenceKey, CompletedHashKey,
                SequenceIndexKey, ActiveKey(ticket)
                },
                new RedisValue[] {
                ticketText, JsonSerializer.Serialize(completed)
                });

            RedisResult[] parts = (RedisResult[])result;
            if ((long)parts[0] != 1) return null;

            completed.GlobalSequence = (long)parts[1];

            // Store final JSON containing its assigned sequence.
            await _db.HashSetAsync(
                CompletedHashKey, ticketText, JsonSerializer.Serialize(completed));

            // Keep a permanent, human-readable Windows CSV synchronized
            // after every successfully completed global study trade.
            //
            // Redis remains authoritative. If CSV writing fails, the trade
            // is still safely recorded in Redis and live trading is unaffected.
            try
            {
                await SaveCsvToDiskAsync();
            }
            catch
            {
                // Observation/reporting must never interfere with trading.
            }

            return completed;
        }

        public async Task<StudySummary> GetSummaryAsync()
        {
            HashEntry[] entries = await _db.HashGetAllAsync(CompletedHashKey);

            var trades = entries
                .Select(x => {
                    try { return JsonSerializer.Deserialize<CompletedTradeObservation>(x.Value.ToString()); }
                    catch { return null; }
                })
                .Where(x => x != null)
                .Cast<CompletedTradeObservation>()
                .OrderBy(x => x.GlobalSequence)
                .ToList();

            int total = trades.Count;
            var wins = trades.Where(x => x.RealizedProfitUsd > 0).ToList();
            var losses = trades.Where(x => x.RealizedProfitUsd < 0).ToList();

            double grossProfit = wins.Sum(x => x.RealizedProfitUsd);
            double grossLoss = Math.Abs(losses.Sum(x => x.RealizedProfitUsd));
            double avgWinner = wins.Count == 0 ? 0 : wins.Average(x => x.RealizedProfitUsd);
            double avgLoser = losses.Count == 0 ? 0 : Math.Abs(losses.Average(x => x.RealizedProfitUsd));
            double winRate = total == 0 ? 0 : (double)wins.Count / total;
            double lossRate = total == 0 ? 0 : (double)losses.Count / total;
            var winsWithMfe = wins.Where(x => x.MfeUsd > 0).ToList();

            return new StudySummary
            {
                CompletedTrades = total,
                RemainingTo50 = Math.Max(0, FirstCheckpoint - total),
                RemainingTo100 = Math.Max(0, FinalCheckpoint - total),
                Wins = wins.Count,
                Losses = losses.Count,
                Breakeven = total - wins.Count - losses.Count,
                WinRatePercent = winRate * 100.0,
                GrossProfitUsd = grossProfit,
                GrossLossUsd = grossLoss,
                NetProfitUsd = trades.Sum(x => x.RealizedProfitUsd),
                AverageWinnerUsd = avgWinner,
                AverageLoserUsd = avgLoser,
                ProfitFactor = grossLoss <= 0 ? (grossProfit > 0 ? double.PositiveInfinity : 0) : grossProfit / grossLoss,
                ExpectancyUsdPerTrade = (winRate * avgWinner) - (lossRate * avgLoser),
                AverageMfeUsd = total == 0 ? 0 : trades.Average(x => x.MfeUsd),
                AverageMaeUsd = total == 0 ? 0 : trades.Average(x => Math.Abs(x.MaeUsd)),
                WinnerMfeCapturePercent = winsWithMfe.Count == 0 ? 0 : winsWithMfe.Average(x => x.CapturePercent),
                LargestWinnerUsd = wins.Count == 0 ? 0 : wins.Max(x => x.RealizedProfitUsd),
                LargestLoserUsd = losses.Count == 0 ? 0 : Math.Abs(losses.Min(x => x.RealizedProfitUsd)),
                Reached50 = total >= FirstCheckpoint,
                Reached100 = total >= FinalCheckpoint
            };
        }

        public async Task<string> GetSummaryTextAsync()
        {
            StudySummary s = await GetSummaryAsync();
            string pf = double.IsPositiveInfinity(s.ProfitFactor) ? "INF" : s.ProfitFactor.ToString("F2");

            return
    $@"SENTINELFX TRADE STUDY
=======================
Study:             {_studyId}
Completed:         {s.CompletedTrades}/100
To first review:   {s.RemainingTo50}
To final review:   {s.RemainingTo100}

Wins:              {s.Wins}
Losses:            {s.Losses}
Breakeven:         {s.Breakeven}
Win rate:          {s.WinRatePercent:F1}%

Gross profit:      ${s.GrossProfitUsd:F2}
Gross loss:        ${s.GrossLossUsd:F2}
NET P/L:           ${s.NetProfitUsd:F2}

Average winner:    ${s.AverageWinnerUsd:F2}
Average loser:     ${s.AverageLoserUsd:F2}
Profit factor:     {pf}
Expectancy/trade:  ${s.ExpectancyUsdPerTrade:F2}

Average MFE:       ${s.AverageMfeUsd:F2}
Average MAE:       ${s.AverageMaeUsd:F2}
Winner capture:    {s.WinnerMfeCapturePercent:F1}%

Largest winner:    ${s.LargestWinnerUsd:F2}
Largest loser:     ${s.LargestLoserUsd:F2}

STATUS: {(s.Reached100 ? "FINAL 100-TRADE REVIEW READY" :
               s.Reached50 ? "FIRST 50-TRADE REVIEW READY" :
               "COLLECTING DATA - STRATEGY REMAINS FROZEN")}";
        }

        /// <summary>
        /// Produces a CSV string from the authoritative Redis study.
        /// All Windows bot processes share the same Redis-backed study.
        /// </summary>
        public async Task<string> ExportCsvAsync()
        {
            HashEntry[] entries = await _db.HashGetAllAsync(CompletedHashKey);

            var trades = entries
                .Select(x => {
                    try { return JsonSerializer.Deserialize<CompletedTradeObservation>(x.Value.ToString()); }
                    catch { return null; }
                })
                .Where(x => x != null)
                .Cast<CompletedTradeObservation>()
                .OrderBy(x => x.GlobalSequence)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("GlobalSequence,Ticket,Symbol,BotType,Side,EntryModel,OpenTimeUtc,CloseTimeUtc,EntryPrice,ExitPrice,Volume,InitialSL,InitialTP,RealizedProfitUsd,MfeUsd,MaeUsd,MfePrice,MaePrice,EntryAtr,EntrySrWall,EntrySrClearanceAtr,EntrySrAhead,EntryEma9DistanceAtr,MfeAtr,MaeAtr,CapturePercent,DurationMinutes,Result,EntryReason,ExitReason");

            foreach (var t in trades)
            {
                sb.AppendLine(string.Join(",",
                    t.GlobalSequence, t.Ticket, Csv(t.Symbol), Csv(t.BotType),
                    Csv(t.Side), Csv(t.EntryModel), Csv(t.OpenTimeUtc.ToString("O")),
                    Csv(t.CloseTimeUtc.ToString("O")), N(t.EntryPrice), N(t.ExitPrice),
                    N(t.Volume), N(t.InitialStopLoss), N(t.InitialTakeProfit),
                    N(t.RealizedProfitUsd), N(t.MfeUsd), N(t.MaeUsd),
                    N(t.MfePrice), N(t.MaePrice), N(t.EntryAtr),
                    N(t.EntrySrWall), N(t.EntrySrClearanceAtr),
                    t.EntrySrAhead ? "true" : "false",
                    N(t.EntryEma9DistanceAtr), N(t.MfeAtr),
                    N(t.MaeAtr), N(t.CapturePercent), N(t.DurationMinutes),
                    Csv(t.Result), Csv(t.EntryReason), Csv(t.ExitReason)));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Rebuilds the global study CSV from Redis and atomically replaces
        /// the Windows file. All bot processes share the same named mutex.
        /// </summary>
        public async Task<string> SaveCsvToDiskAsync()
        {
            string csv = await ExportCsvAsync();

            Directory.CreateDirectory(CsvFolder);

            string safeStudyId =
                string.Concat(
                    _studyId.Select(ch =>
                        Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

            string finalPath =
                Path.Combine(
                    CsvFolder,
                    $"SentinelFX_TradeStudy_{safeStudyId}.csv");

            string tempPath =
                finalPath + "." +
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture) +
                ".tmp";

            using var mutex =
                new Mutex(false, CsvMutexName);

            bool acquired = false;

            try
            {
                try
                {
                    acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
                }
                catch (AbandonedMutexException)
                {
                    // Previous writer terminated while holding the mutex.
                    // We now own it and can safely continue.
                    acquired = true;
                }

                if (!acquired)
                    throw new TimeoutException(
                        "Timed out waiting for SentinelFX study CSV writer lock.");

                await File.WriteAllTextAsync(
                    tempPath,
                    csv,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

                // File.Move(..., overwrite:true) is available on modern .NET.
                // This keeps readers from seeing a partially-written CSV.
                File.Move(
                    tempPath,
                    finalPath,
                    true);

                return finalPath;
            }
            finally
            {
                if (acquired)
                    mutex.ReleaseMutex();

                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // Cleanup failure must not affect trading.
                }
            }
        }

        private static DateTime EnsureUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc) return value;
            if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static string N(double value) =>
            value.ToString("G17", CultureInfo.InvariantCulture);

        private static string Csv(string? value)
        {
            value ??= "";
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
