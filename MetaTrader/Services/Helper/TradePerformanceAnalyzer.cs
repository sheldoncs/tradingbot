namespace MetaTrader.Services.Helper
{
    using global::MetaTrader.Util;
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public class TradePerformanceAnalyzer
    {
        // ============================================================
        // INTERNAL TYPES
        // ============================================================

        public enum TradeOutcome
        {
            Open,
            Win,
            Loss,
            Breakeven
        }

        public class TradeRecord
        {
            public ulong Ticket { get; set; }

            public string Symbol { get; set; }
            public string EntryModel { get; set; }
            public string Side { get; set; }

            public DateTime EntryTime { get; set; }
            public DateTime? ExitTime { get; set; }

            public double EntryPrice { get; set; }
            public double ExitPrice { get; set; }

            public double EntryAtr { get; set; }

            public double InitialStopLoss { get; set; }
            public double InitialRiskUsd { get; set; }

            public double CurrentProfitUsd { get; set; }

            // Highest floating profit reached.
            public double MaxFavorableExcursionUsd { get; set; }

            // Lowest floating P/L reached.
            // Example:
            // Trade reaches -$7.20 before recovering.
            // MAE = -7.20
            public double MaxAdverseExcursionUsd { get; set; }

            public double RealizedProfitUsd { get; set; }

            public string HighestRiskStageReached { get; set; }

            public string ExitReason { get; set; }

            public bool StopLossModified { get; set; }

            public bool ManualStopDetected { get; set; }

            public TradeOutcome Outcome { get; set; }

            public double ProfitGivebackUsd
            {
                get
                {
                    if (MaxFavorableExcursionUsd <= 0)
                        return 0;

                    return Math.Max(
                        0,
                        MaxFavorableExcursionUsd - RealizedProfitUsd
                    );
                }
            }

            public double CaptureEfficiency
            {
                get
                {
                    if (MaxFavorableExcursionUsd <= 0)
                        return 0;

                    return RealizedProfitUsd /
                           MaxFavorableExcursionUsd;
                }
            }

            public double RiskRewardRealized
            {
                get
                {
                    if (InitialRiskUsd <= 0)
                        return 0;

                    return RealizedProfitUsd /
                           InitialRiskUsd;
                }
            }

            public double MfeRiskMultiple
            {
                get
                {
                    if (InitialRiskUsd <= 0)
                        return 0;

                    return MaxFavorableExcursionUsd /
                           InitialRiskUsd;
                }
            }

            public double MaeRiskMultiple
            {
                get
                {
                    if (InitialRiskUsd <= 0)
                        return 0;

                    return Math.Abs(MaxAdverseExcursionUsd) /
                           InitialRiskUsd;
                }
            }
        }

        // ============================================================
        // STORAGE
        // ============================================================

        private readonly Dictionary<ulong, TradeRecord> _trades =
            new Dictionary<ulong, TradeRecord>();


        // ============================================================
        // REGISTER NEW TRADE
        // ============================================================

        public void RegisterTrade(
            ulong ticket,
            string symbol,
            string entryModel,
            string side,
            double entryPrice,
            double atr,
            double initialStopLoss,
            double initialRiskUsd)
        {
            if (_trades.ContainsKey(ticket))
                return;

            var record = new TradeRecord
            {
                Ticket = ticket,

                Symbol = symbol,
                EntryModel = entryModel,
                Side = side,

                EntryTime = DateTime.Now,

                EntryPrice = entryPrice,

                EntryAtr = atr,

                InitialStopLoss = initialStopLoss,
                InitialRiskUsd = initialRiskUsd,

                CurrentProfitUsd = 0,

                MaxFavorableExcursionUsd = 0,
                MaxAdverseExcursionUsd = 0,

                RealizedProfitUsd = 0,

                HighestRiskStageReached = "Initial",

                ExitReason = "",

                StopLossModified = false,
                ManualStopDetected = false,

                Outcome = TradeOutcome.Open
            };

            _trades[ticket] = record;

            LogFile.Write(
                $"📊 ANALYZER REGISTER | " +
                $"ticket={ticket} | " +
                $"symbol={symbol} | " +
                $"model={entryModel} | " +
                $"side={side} | " +
                $"entry={entryPrice:F5} | " +
                $"ATR={atr:F5} | " +
                $"risk=${initialRiskUsd:F2} | " +
                $"SL={initialStopLoss:F5}"
            );
        }


        // ============================================================
        // LIVE TRADE UPDATE
        // ============================================================

        public void UpdateTrade(
            ulong ticket,
            double currentProfitUsd,
            string currentRiskStage,
            bool stopLossModified = false,
            bool manualStopDetected = false)
        {
            TradeRecord trade;

            if (!_trades.TryGetValue(ticket, out trade))
                return;

            if (trade.Outcome != TradeOutcome.Open)
                return;

            trade.CurrentProfitUsd = currentProfitUsd;

            // --------------------------------------------------------
            // MFE
            // Maximum Favorable Excursion
            // --------------------------------------------------------

            if (currentProfitUsd >
                trade.MaxFavorableExcursionUsd)
            {
                trade.MaxFavorableExcursionUsd =
                    currentProfitUsd;
            }

            // --------------------------------------------------------
            // MAE
            // Maximum Adverse Excursion
            // --------------------------------------------------------

            if (currentProfitUsd <
                trade.MaxAdverseExcursionUsd)
            {
                trade.MaxAdverseExcursionUsd =
                    currentProfitUsd;
            }

            // --------------------------------------------------------
            // SL information
            // --------------------------------------------------------

            if (stopLossModified)
                trade.StopLossModified = true;

            if (manualStopDetected)
                trade.ManualStopDetected = true;

            // --------------------------------------------------------
            // Risk stage tracking
            // --------------------------------------------------------

            UpdateHighestStage(
                trade,
                currentRiskStage
            );
        }


        // ============================================================
        // CLOSE TRADE
        // ============================================================

        public void CloseTrade(
            ulong ticket,
            double exitPrice,
            double realizedProfitUsd,
            string exitReason)
        {
            TradeRecord trade;

            if (!_trades.TryGetValue(ticket, out trade))
            {
                LogFile.Write(
                    $"⚠️ ANALYZER CLOSE UNKNOWN TICKET | " +
                    $"ticket={ticket}"
                );

                return;
            }

            if (trade.Outcome != TradeOutcome.Open)
                return;

            trade.ExitTime = DateTime.Now;

            trade.ExitPrice = exitPrice;

            trade.RealizedProfitUsd =
                realizedProfitUsd;

            trade.CurrentProfitUsd =
                realizedProfitUsd;

            trade.ExitReason =
                exitReason ?? "";

            // Make sure final result can update MFE/MAE.
            if (realizedProfitUsd >
                trade.MaxFavorableExcursionUsd)
            {
                trade.MaxFavorableExcursionUsd =
                    realizedProfitUsd;
            }

            if (realizedProfitUsd <
                trade.MaxAdverseExcursionUsd)
            {
                trade.MaxAdverseExcursionUsd =
                    realizedProfitUsd;
            }

            // --------------------------------------------------------
            // Outcome
            // --------------------------------------------------------

            if (realizedProfitUsd > 0.50)
            {
                trade.Outcome =
                    TradeOutcome.Win;
            }
            else if (realizedProfitUsd < -0.50)
            {
                trade.Outcome =
                    TradeOutcome.Loss;
            }
            else
            {
                trade.Outcome =
                    TradeOutcome.Breakeven;
            }

            LogTradeResult(trade);
        }


        // ============================================================
        // UPDATE HIGHEST RISK STAGE
        // ============================================================

        private void UpdateHighestStage(
            TradeRecord trade,
            string newStage)
        {
            if (string.IsNullOrWhiteSpace(newStage))
                return;

            int existingRank =
                GetStageRank(
                    trade.HighestRiskStageReached
                );

            int newRank =
                GetStageRank(newStage);

            if (newRank > existingRank)
            {
                trade.HighestRiskStageReached =
                    newStage;
            }
        }


        // ============================================================
        // STAGE RANK
        // ============================================================

        private int GetStageRank(string stage)
        {
            if (string.IsNullOrWhiteSpace(stage))
                return -1;

            switch (stage.ToLower())
            {
                case "initial":
                    return 0;

                case "developing":
                    return 1;

                case "protected":
                    return 2;

                case "trailing":
                    return 3;

                default:
                    return -1;
            }
        }


        // ============================================================
        // LOG COMPLETED TRADE
        // ============================================================

        private void LogTradeResult(
            TradeRecord t)
        {
            double minutes = 0;

            if (t.ExitTime.HasValue)
            {
                minutes =
                    (t.ExitTime.Value -
                     t.EntryTime)
                    .TotalMinutes;
            }

            LogFile.Write(
                $"📊 TRADE PERFORMANCE | " +
                $"ticket={t.Ticket} | " +
                $"symbol={t.Symbol} | " +
                $"model={t.EntryModel} | " +
                $"side={t.Side} | " +
                $"result=${t.RealizedProfitUsd:F2} | " +
                $"MFE=${t.MaxFavorableExcursionUsd:F2} | " +
                $"MAE=${t.MaxAdverseExcursionUsd:F2} | " +
                $"giveback=${t.ProfitGivebackUsd:F2} | " +
                $"capture={t.CaptureEfficiency:P1} | " +
                $"RR={t.RiskRewardRealized:F2}R | " +
                $"MFE-R={t.MfeRiskMultiple:F2}R | " +
                $"MAE-R={t.MaeRiskMultiple:F2}R | " +
                $"stage={t.HighestRiskStageReached} | " +
                $"exit={t.ExitReason} | " +
                $"duration={minutes:F1}min"
            );
        }


        // ============================================================
        // OVERALL SUMMARY
        // ============================================================

        public void PrintSummary()
        {
            List<TradeRecord> closed =
                _trades.Values
                .Where(
                    x =>
                        x.Outcome !=
                        TradeOutcome.Open
                )
                .ToList();

            if (closed.Count == 0)
            {
                LogFile.Write(
                    "📊 PERFORMANCE SUMMARY | No closed trades."
                );

                return;
            }

            int wins =
                closed.Count(
                    x =>
                        x.Outcome ==
                        TradeOutcome.Win
                );

            int losses =
                closed.Count(
                    x =>
                        x.Outcome ==
                        TradeOutcome.Loss
                );

            int breakevens =
                closed.Count(
                    x =>
                        x.Outcome ==
                        TradeOutcome.Breakeven
                );

            double netProfit =
                closed.Sum(
                    x =>
                        x.RealizedProfitUsd
                );

            double grossProfit =
                closed
                .Where(
                    x =>
                        x.RealizedProfitUsd > 0
                )
                .Sum(
                    x =>
                        x.RealizedProfitUsd
                );

            double grossLoss =
                Math.Abs(
                    closed
                    .Where(
                        x =>
                            x.RealizedProfitUsd < 0
                    )
                    .Sum(
                        x =>
                            x.RealizedProfitUsd
                    )
                );

            double avgWin =
                closed
                .Where(
                    x =>
                        x.RealizedProfitUsd > 0
                )
                .Select(
                    x =>
                        x.RealizedProfitUsd
                )
                .DefaultIfEmpty(0)
                .Average();

            double avgLoss =
                closed
                .Where(
                    x =>
                        x.RealizedProfitUsd < 0
                )
                .Select(
                    x =>
                        x.RealizedProfitUsd
                )
                .DefaultIfEmpty(0)
                .Average();

            double winRate =
                closed.Count > 0
                    ? (double)wins /
                      closed.Count
                    : 0;

            double profitFactor =
                grossLoss > 0
                    ? grossProfit /
                      grossLoss
                    : grossProfit;

            double avgCapture =
                closed
                .Where(
                    x =>
                        x.MaxFavorableExcursionUsd > 0
                )
                .Select(
                    x =>
                        x.CaptureEfficiency
                )
                .DefaultIfEmpty(0)
                .Average();

            double avgMae =
                closed
                .Select(
                    x =>
                        x.MaxAdverseExcursionUsd
                )
                .DefaultIfEmpty(0)
                .Average();

            double avgMfe =
                closed
                .Select(
                    x =>
                        x.MaxFavorableExcursionUsd
                )
                .DefaultIfEmpty(0)
                .Average();

            double expectancy =
                closed.Count > 0
                    ? netProfit /
                      closed.Count
                    : 0;

            LogFile.Write(
                $"📈 PERFORMANCE SUMMARY | " +
                $"trades={closed.Count} | " +
                $"wins={wins} | " +
                $"losses={losses} | " +
                $"BE={breakevens} | " +
                $"winRate={winRate:P1} | " +
                $"net=${netProfit:F2} | " +
                $"avgWin=${avgWin:F2} | " +
                $"avgLoss=${avgLoss:F2} | " +
                $"profitFactor={profitFactor:F2} | " +
                $"expectancy=${expectancy:F2}/trade | " +
                $"avgMFE=${avgMfe:F2} | " +
                $"avgMAE=${avgMae:F2} | " +
                $"avgCapture={avgCapture:P1}"
            );
        }


        // ============================================================
        // SUMMARY BY SYMBOL + ENTRY MODEL
        // ============================================================

        public void PrintDetailedSummary()
        {
            var closed =
                _trades.Values
                .Where(
                    x =>
                        x.Outcome !=
                        TradeOutcome.Open
                )
                .ToList();

            if (closed.Count == 0)
                return;

            var groups =
                closed
                .GroupBy(
                    x =>
                        new
                        {
                            x.Symbol,
                            x.EntryModel
                        }
                );

            foreach (var group in groups)
            {
                List<TradeRecord> trades =
                    group.ToList();

                int wins =
                    trades.Count(
                        x =>
                            x.Outcome ==
                            TradeOutcome.Win
                    );

                int losses =
                    trades.Count(
                        x =>
                            x.Outcome ==
                            TradeOutcome.Loss
                    );

                double net =
                    trades.Sum(
                        x =>
                            x.RealizedProfitUsd
                    );

                double grossWin =
                    trades
                    .Where(
                        x =>
                            x.RealizedProfitUsd > 0
                    )
                    .Sum(
                        x =>
                            x.RealizedProfitUsd
                    );

                double grossLoss =
                    Math.Abs(
                        trades
                        .Where(
                            x =>
                                x.RealizedProfitUsd < 0
                        )
                        .Sum(
                            x =>
                                x.RealizedProfitUsd
                        )
                    );

                double avgWin =
                    trades
                    .Where(
                        x =>
                            x.RealizedProfitUsd > 0
                    )
                    .Select(
                        x =>
                            x.RealizedProfitUsd
                    )
                    .DefaultIfEmpty(0)
                    .Average();

                double avgLoss =
                    trades
                    .Where(
                        x =>
                            x.RealizedProfitUsd < 0
                    )
                    .Select(
                        x =>
                            x.RealizedProfitUsd
                    )
                    .DefaultIfEmpty(0)
                    .Average();

                double winRate =
                    trades.Count > 0
                        ? (double)wins /
                          trades.Count
                        : 0;

                double profitFactor =
                    grossLoss > 0
                        ? grossWin /
                          grossLoss
                        : grossWin;

                double expectancy =
                    trades.Count > 0
                        ? net /
                          trades.Count
                        : 0;

                double avgMfe =
                    trades
                    .Select(
                        x =>
                            x.MaxFavorableExcursionUsd
                    )
                    .DefaultIfEmpty(0)
                    .Average();

                double avgMae =
                    trades
                    .Select(
                        x =>
                            x.MaxAdverseExcursionUsd
                    )
                    .DefaultIfEmpty(0)
                    .Average();

                double avgCapture =
                    trades
                    .Where(
                        x =>
                            x.MaxFavorableExcursionUsd > 0
                    )
                    .Select(
                        x =>
                            x.CaptureEfficiency
                    )
                    .DefaultIfEmpty(0)
                    .Average();

                LogFile.Write(
                    $"📊 MODEL SUMMARY | " +
                    $"symbol={group.Key.Symbol} | " +
                    $"model={group.Key.EntryModel} | " +
                    $"trades={trades.Count} | " +
                    $"wins={wins} | " +
                    $"losses={losses} | " +
                    $"winRate={winRate:P1} | " +
                    $"net=${net:F2} | " +
                    $"avgWin=${avgWin:F2} | " +
                    $"avgLoss=${avgLoss:F2} | " +
                    $"PF={profitFactor:F2} | " +
                    $"expectancy=${expectancy:F2} | " +
                    $"avgMFE=${avgMfe:F2} | " +
                    $"avgMAE=${avgMae:F2} | " +
                    $"capture={avgCapture:P1}"
                );
            }
        }


        // ============================================================
        // GET ONE TRADE
        // ============================================================

        public TradeRecord GetTrade(
            ulong ticket)
        {
            TradeRecord trade;

            if (_trades.TryGetValue(
                ticket,
                out trade))
            {
                return trade;
            }

            return null;
        }


        // ============================================================
        // CHECK IF TRADE EXISTS
        // ============================================================

        public bool ContainsTrade(
            ulong ticket)
        {
            return _trades.ContainsKey(
                ticket
            );
        }


        // ============================================================
        // OPEN TRADES
        // ============================================================

        public List<TradeRecord>
            GetOpenTrades()
        {
            return _trades.Values
                .Where(
                    x =>
                        x.Outcome ==
                        TradeOutcome.Open
                )
                .ToList();
        }


        // ============================================================
        // CLOSED TRADES
        // ============================================================

        public List<TradeRecord>
            GetClosedTrades()
        {
            return _trades.Values
                .Where(
                    x =>
                        x.Outcome !=
                        TradeOutcome.Open
                )
                .ToList();
        }
    }
}
