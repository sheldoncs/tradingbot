using AutoMapper;
using chatgptbot.Util;
using MetaTrader.Data.Database;
using MetaTrader.Data.Entities;
using MetaTrader.Dto;
using MetaTrader.Enums;
using MetaTrader.Enums.MetaTrader.Enums;
using MetaTrader.helper;
using MetaTrader.Services.Contract;
using MetaTrader.Services.Exits;
using MetaTrader.Services.Helper;
using MetaTrader.Services.Interfaces;
using MetaTrader.Services.MetaTrader.Services;
using MetaTrader.Services.SMC.MetaTrader.Services;
using MetaTrader.Util;
using MetaTrader.Util.Trading.Execution;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using MtApi5;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Telegram.Bot.Types;
using static MetaTrader.Services.ExitTrade;
using static MetaTrader.Services.Helper.TransitionZoneEngine;
using static MetaTrader.Services.HybridMomentumEngine;
using static System.Collections.Specialized.BitVector32;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace MetaTrader.Services
{
    public class TradeServiceEngine
    {
        private static DateTime _lastSignalCandleTime = DateTime.MinValue;
        private readonly INotificationService _notificationService;
        private readonly ITradingPairProperties _tradingPairProperties;
        private readonly ITradingDbContext _dbContext;
        private TradingWindow tradingWindow = new TradingWindow();
        private readonly IConfiguration _configuration;
        private const ENUM_TIMEFRAMES TIMEFRAME = ENUM_TIMEFRAMES.PERIOD_M30;
        private static int MAGIC_NUMBER = 12345;

        private ulong ticket = 0;

        //private static Func<Results<T>, Task> _onMessageReceived<T>;
        private static Func<object, Task> _onMessageReceived;
        private static Func<object, Task> _logs;
        private static Func<object, Task> _stateUpdate;

        private static System.Timers.Timer pollTimer;

        private static string SYMBOL = "";
        private static ENUM_TIMEFRAMES timeframe = ENUM_TIMEFRAMES.PERIOD_H1; // configurable timeframe
        private static readonly string logFile = "trades.csv";

        private static double? balance = null;
        private string currentSignal = null;
        private double maxProfit = 0.0;
        private bool trendConfirmed = true;
        private bool potentialReversal = true;
        static int fastEmaPeriod = 9;
        static int slowEmaPeriod = 21;

        const int BarsToFetch = 300;              // number of closed bars to fetch

        double RiskPerTradeUsd = 40.0;      // USD risk per trade (used for dynamic lot sizing)
        const double StopLossPoints = 500.0;       // SL distance in "points"
        const double TakeProfitPoints = 600.0;    // TP distance in "points"
        const double TrailingStopPoints = 500.0;   // trailing stop distance in "points"
        const double ContractSize = 100.0;        // XAUUSD contract size (100 oz per standard lot)
        static string lastSignal = null; // "BUY" or "SELL"

        double startingEquity = 0;
        double startingBalance = 0;
        double currentEquity = 0;
        double maxLossRisk = 0;
        static double maxDrawdown = 1.0;
        static double UnrealizedMaxDrawDown = 0;
        private static bool manualTrade = false;
        private const double maxAllowedLot = 0.05;
        private bool notificationSent = false;
        private static double TrailingDistanceUsd = 10.0;
        private static double percentageRisk = 0.05; // 5% of largest balance
        private double beginningBalance = 0.0;
        private bool obTriggered = false;
        private ExitTrade exitStarTrade;
        private double maxBalance = 0;
        private bool tradeBegun;
        private bool slowingTrade = false;
        private static bool closingTrade;
        public static bool IsClosingTrade => closingTrade;

        private DateTime lastCandleTime = DateTime.MinValue;
        private DateTime _lastCloseTime;
        private int candlesSinceClose = 3;
        private bool isTradeClosed = false;
        private bool ResetTrade = true;
        private int _cooldownCandles = 2;     // Number of candles to wait before opening a new trade
        private int _candlesSinceClose = 2;   // Counter of candles passed since last trade closed
        public static MqlRates[] rates { get; set; }
        public string _connectionId { get; set; }
        public string _tframe { get; set; }
        public string _symbol { get; set; }

        private MqlRates[] chunkRates;
        private double Diff = 0;
        private TradingPairsDto tradingPairsDto;
        private int cooldownRemaining = 0;  // number of candles left in cooldown
        DateTime _lastCandleTime = DateTime.MinValue;
        private static bool _orderInProgress = false;
        private static DateTime _lastOrderCandleTime = DateTime.MinValue;
        private readonly TradeCoordinator _tradeCoordinator;
        private readonly TimeframeDiversityExecutor _tfExecutor;
        private readonly M15ConfirmationService _m15Service;
        private readonly PositionManager _positionManager;
        private readonly IServiceProvider _provider;
        // -------------------------

        // local tracking of open trades (because client's API cannot enumerate MT5 positions)
        static List<TrackedTrade> openTrades = new();
        static List<ClosedTrade> closedTrades = new List<ClosedTrade>();
        private SmartTradeExitService tradeExitService = new SmartTradeExitService();
        private static int _lastOrderBlockIndex = -1; // tracks last OB index
        private double maxIncrease = 0;
        private const double fixedLotSize = 0.01;
        private double peakEquity = 300; // update as equity grows

        public double CurrentMomentum { get; set; }
        public double PreviousMomentum { get; set; }
        //private DateTime _lastLossCheckCandleTime = DateTime.MinValue;

        private PerformanceCalculator calculator = new PerformanceCalculator(
                startingEquity: 10000,
                maxDrawdownPct: 3.0,
                expectedTradesPerDay: 3
            );

        List<string> symbolsToTrade = new()
        {
            "XAUUSD",
            "XAUEUR"
        };

        /*
        List<string> symbolsToTrade = new()
        {
            "BNBUSD",
            "XAUUSD.s",
            //"GBPUSD",
            "SOLUSD",
            //"EURUSD",
            //"XRPUSD",
            //"ETHUSD",
            //"BTCUSD",
            //"ADAUSD"
        };
        */
        private static double _lastRealizedProfitUsd = 0.0;
        private static TradeSide? _lastRealizedSide = null;
        private static DateTime _lastRealizedTime = DateTime.MinValue;
        private readonly IHubContext<ChatHub> _chatHub;

        private static readonly Dictionary<string, SymbolContext> _contexts = new Dictionary<string, SymbolContext>();
        private static Dictionary<TradingEngineMode, EngineTrade> engineTrades = new Dictionary<TradingEngineMode, EngineTrade>() {
            {TradingEngineMode.SniperMode , new EngineTrade{ wins = 0, losses = 0} },
            {TradingEngineMode.VelocityMode, new EngineTrade{ wins = 0, losses = 0} },
            {TradingEngineMode.EmaPullbackMode, new EngineTrade{ wins = 0, losses = 0} },
            {TradingEngineMode.BreakoutContinuationMode,new EngineTrade { wins = 0, losses = 0 }},
            {TradingEngineMode.BreakRetestMode,new EngineTrade { wins = 0, losses = 0 } },
            {TradingEngineMode.TrendRideMode,new EngineTrade { wins = 0, losses = 0 }},
            {TradingEngineMode.ExhaustionReversalMode,new EngineTrade { wins = 0, losses = 0 }}
        };

        // --- MT5 client wiring (non-breaking bridge) ---
        private static readonly object _clientSync = new();

        // Keep BOTH fields so NOTHING breaks
        private static MtApi5Client _client;                // IIS/Injected client

        // Optional: helper so you can quickly check if client is ready

        static Dictionary<string, DateTime> LastForcedExit = new();
        static readonly TimeSpan ReentryCooldown = TimeSpan.FromMinutes(30);
        public TradeServiceEngine(
            INotificationService notificationService,
            ITradingPairProperties tradingPairProperties,
            IConfiguration configuration,
            TradeCoordinator tradeCoordinator,
            MtApi5Client client,
            TimeframeDiversityExecutor tfExecutor,
            M15ConfirmationService m15Service,
            PositionManager positionManager,
            IServiceProvider provider)
        {
            _positionManager = positionManager;
            _m15Service = m15Service;
            _tfExecutor = tfExecutor;
            _client = client;
            _provider = provider;
            _tradeCoordinator = tradeCoordinator;
            _notificationService = notificationService;
            _configuration = configuration;
            _tradingPairProperties = tradingPairProperties;
        }

        public void setupInitialProperties(string connectionId)
        {
            _connectionId = connectionId;
        }
        public async Task<string> getSymbolAsync()
        {
            return await Task.FromResult(SYMBOL);
        }
        public async Task<string> getConnectionIdAsync()
        {
            return await Task.FromResult(_connectionId);
        }
        public async Task<string> getTimeFrameStringAsync()
        {
            return await Task.FromResult(_tframe);
        }
        public async Task openConnection(Func<object, Task> onMessageReceived, MtApi5Client clientFromCaller)
        {
            try
            {
                _onMessageReceived = onMessageReceived;

                if (clientFromCaller != null)
                {
                    lock (_clientSync)
                    {
                        _client = clientFromCaller; // ✅ prefer this for IIS
                    }
                }

                Results<string> results = new Results<string>
                {
                    messageType = "string",
                    message = "connected",
                    Data = "Connected to MT5",
                    status = 200
                };
                await _onMessageReceived(results);
                /*
                _client.BeginConnect("127.0.0.1", 8228);
                  LogFile.Write("Connected to MT5 IIS");
                */
            }
            catch (Exception ex)
            {
                LogFile.Write($"openConnection crashed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public async void connect(Func<object, Task> onMessageReceived)
        {
            if (pollTimer != null)
                pollTimer.Close();

            _onMessageReceived = onMessageReceived;

            _client.ConnectionStateChanged += Api_ConnectionStateChanged;

            _client.BeginConnect("127.0.0.1", 8228);

            Results<string> results = new Results<string>
            {
                messageType = "string",
                message = "connecting",
                Data = "Connecting to MT5...",
                status = 200
            };

            await _onMessageReceived(results);

            Console.WriteLine("Connecting to MT5...");
            LogFile.Write("Connecting to MT5...");
        }

        private void Client_ConnectionStateChanged(object sender, Mt5ConnectionEventArgs e)
        {
            Console.WriteLine($"MT5 connection state: {e.Status}");
            LogFile.Write($"MT5 connection state: {e.Status}");
            Results<string> results = new Results<string>();
            results.messageType = "string";
            results.message = "disconnected";
            results.Data = "Disconnected from MT5";
            results.status = 200;
            _onMessageReceived(results);
        }

        private void Api_ConnectionStateChanged(object sender, Mt5ConnectionEventArgs e)
        {
            if (e.Status == Mt5ConnectionState.Connected)
            {
                Results<string> results = new Results<string>();
                results.messageType = "string";
                results.message = "connected";
                results.Data = "Connected to MT5";
                results.status = 200;
                _onMessageReceived(results);
                InitializeTrade();

                startingBalance = _client.AccountInfoDouble(ENUM_ACCOUNT_INFO_DOUBLE.ACCOUNT_EQUITY);
                if (DateTime.UtcNow.Month == 12)
                {
                    DecemberRiskGuard.Initialize(10000);
                }

                Console.WriteLine("Connected to MT5");
                LogFile.Write("Connected to MT5");
                double bid = _client.SymbolInfoDouble(SYMBOL, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);

            }
            else
            {

                Console.WriteLine($"Connection state: {e.Status}");
                LogFile.Write($"Connection state: {e.Status}");
                connectionState(e.Status);
            }
        }

        private void connectionState(Mt5ConnectionState status)
        {
            //streamMessage<string>("string", "connectionState", status.ToString());

            Task.FromResult(streamMessage<string>("string", "connectionState", status.ToString()));
        }

        private static void setFixedUnrealized()
        {
            UnrealizedMaxDrawDown = (maxDrawdown / 100) * _client.AccountInfoDouble(ENUM_ACCOUNT_INFO_DOUBLE.ACCOUNT_EQUITY);
        }
        public void setTradingBegun(bool flag)
        {
            tradeBegun = flag;
        }

        public bool getTradingBegun()
        {
            return tradeBegun;
        }
        async Task streamMessage<T>(string messageType, string message, T data)
        {

            try
            {
                Results<T> results = new Results<T>();
                results.messageType = messageType;
                results.message = message;
                results.Data = data;
                results.status = 200;
                await _onMessageReceived(results);
            }
            catch (Exception ex)
            {
                LogFile.Write($"streamMessage crashed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private KryptoLabelsDto _labels;
        public async Task InitializeAsync(string symbol, CancellationToken ct)
        {
            _labels = await _tradingPairProperties.GetKryptoPairsAsync(symbol);

            startTrading(ct);
        }
        private CancellationToken _cancellationToken;

        private void PollTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            checkTradingWindow(false, _cancellationToken);
        }
        bool IsMt5Ready(MtApi5Client client)
        {
            try
            {
                var copied = client.CopyRates("XAUUSD", ENUM_TIMEFRAMES.PERIOD_H1, 1, BarsToFetch, out MqlRates[] rates);
                return rates != null && rates.Length > 0;
            }
            catch
            {
                return false;
            }
        }
        public void startTrading(CancellationToken ct)
        {
            LogFile.Write("Connected to MT5");
            setFixedUnrealized();
            _cancellationToken = ct;
            /*
            while (true)
            {
                GenerateSignals(false);
            }
            */

            // Start polling timer (every 5 seconds)
            pollTimer = new System.Timers.Timer(5000);
            pollTimer.Elapsed += PollTimer_Elapsed;   // correct
            pollTimer.AutoReset = true;
            pollTimer.Enabled = true;


            ct.Register(() =>
            {
                pollTimer?.Stop();
                pollTimer?.Dispose();
                pollTimer = null;
            });

        }

        public async void checkTradingWindow(bool isIIS, CancellationToken ct)
        {
            DateTime now = DateTime.Now;

            DateTime nowUtc = now.ToUniversalTime();

            if (TradingWindow.IsTradingWindow(now))
            {
                Console.WriteLine($"[{now}] In trading window → running trading logic...");
                LogFile.Write($"[{now}] In trading window → running trading logic...");
                bool startTrade = await TradingWindow.RunTradingTickAsync();

                if (startTrade)
                {
                    GenerateSignals(isIIS, ct);
                }
            }
            else
            {
                // Just log and return.
                var day = now.DayOfWeek;
                if (day == DayOfWeek.Sunday)
                {
                    DateTime nextStart = TradingWindow.GetNextMondayAt2am(now);
                    Console.WriteLine($"[{now}] Outside trading window. Next start: {nextStart}");
                    Console.WriteLine($"[{now}] Outside trading window. Next start: {nextStart}");

                }
                else
                {
                    LogFile.Write($"[{now}] Outside trading window. Next start");
                    Console.WriteLine($"[{now}] Outside trading window. Next start");
                }
            }
        }

        enum PositionSide { Buy, Sell, None }

        async void showBalances()
        {
            currentEquity = _client.AccountInfoDouble(ENUM_ACCOUNT_INFO_DOUBLE.ACCOUNT_EQUITY);

            await streamMessage<double>("double", "startingEquity", startingBalance);
            await streamMessage<double>("double", "currentEquity", currentEquity);

            double drop = startingBalance - currentEquity;

            Console.WriteLine($"Equity: {currentEquity:F2}, Drop: {drop:F2}");
            LogFile.Write($"Equity: {currentEquity:F2}, Drop: {drop:F2}");
            //await streamMessage<string>("string", "output", $"Equity: {currentEquity:F2}, Drop: {drop:F2}");

        }

        private async void generalCloseTrade(double bid, double ask, SymbolContext ctx)
        {
            tradeProfit(ctx.Symbol);

            foreach (var engine in engineTrades)
            {
                int total = engine.Value.wins + engine.Value.losses;
                double winRate = total == 0 ? 0 : (double)engine.Value.wins / total * 100;

                LogFile.Write($"{engine.Key.ToString()}: {winRate:F2}%");
            }

            if (ctx != null)
            {
                ctx.PeakAtr = 0.0;
                ctx.PeakAtrCaptured = false;
                ctx.ResetPullback();
                ctx.ResetExpansion();
            }
            SymbolPriorityEngine.Release(ctx.Symbol);
            var trade = new ClosedTrade
            {
                CloseTime = DateTime.UtcNow,
                Profit = _client.HistoryDealGetDouble(ticket, MtApi5.ENUM_DEAL_PROPERTY_DOUBLE.DEAL_PROFIT),
                Risk = Math.Max((double)tradingPairsDto.riskpertrade, 0.01),
                EntryPrice = currentSignal == "BUY" ? ask : bid,
                StopLoss = openTrades.Select(t => t).FirstOrDefault().StopLoss,
                Volume = openTrades.Select(t => t).FirstOrDefault().Volume
            };



            closedTrades.Add(trade);

            tradingEngine = "";
            tradeBegun = false;
            _orderInProgress = false;
            Diff = 0; // reset after handling
            maxBalance = 0;
            TradeServiceEngine.closingTrade = false;
            currentSignal = null;
            ResetTrade = true;
            CloseTrade(currentSignal == "BUY" ? TradeSide.Buy : TradeSide.Sell, bid, ask);
            /*
            
            
            */

            Console.WriteLine($"Closing Trade {currentSignal}");
            LogFile.Write($"Closing Trade {currentSignal}");
            startingEquity = getStartEquity().Result;
            await streamMessage<double>("double", "startingEquity", startingEquity);
            await streamMessage<double>("double", "currentEquity", startingEquity);
            startingEquity = 0;
            openTrades.Clear();
            GlobalTradeManager.ReleaseTradeLock(_provider);

            TradingEventBus.Decision(new DecisionEvent
            {
                Symbol = ctx.Symbol,
                Action = "Trade Closed",
                Category = DecisionCategory.None.ToString(),
                Reason = $"Closed Trade",
                Message = $"Closed Trade",
                Momentum = 0,
                EarlyMomentum = false,
                EmaCompressed = false,
                Regime = DecisionCategory.None.ToString(),
                Time = DateTime.UtcNow
            });
            TradingEventBus.M15SnapshotUpdate(new M15Snapshot()
            {
                Message = "M15 not Confirmed",
                Symbol = ctx.Symbol,
            });
        }
        public async Task<double> getStartEquity()
        {

            double startingEquity = _client.AccountInfoDouble(ENUM_ACCOUNT_INFO_DOUBLE.ACCOUNT_EQUITY);

            await streamMessage<double>("double", "startingEquity", startingEquity);

            return await Task.FromResult(startingEquity);
        }
        private async void GenerateSignals(bool isIIS, CancellationToken ct)
        {

            try
            {
                LogFile.Write("Begin Strategy...");

                await RunParallelStrategy(ct);

            }
            catch (Exception ex)
            {
                
                Console.WriteLine("Error: " + ex.Message + " "+ ex.StackTrace);
            }
        }
        public static void DailyModeUpdate()
        {
            if (DateTime.UtcNow.Month != 12)
                SymbolRotationEngine.CurrentMode =
                    SymbolRotationEngine.TradingMode.Mixed;
            else
                SymbolRotationEngine.CurrentMode =
                    SymbolRotationEngine.TradingMode.CurrencyOnly;
        }

        private void checkTradingLosses()
        {

            double dailyPL = GetFtmoDailyPL();
            if (dailyPL <= -500.0 || CheckOverallLoss())
            {
                pollTimer.Stop();
                double bid = _client.SymbolInfoDouble(SYMBOL, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);
                double ask = _client.SymbolInfoDouble(SYMBOL, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);
                TradeSide side = currentSignal == "BUY" ? TradeSide.Buy : currentSignal == "SELL" ? TradeSide.Sell : TradeSide.NoTrade;
                CloseTrade(side, bid, ask);
                Console.WriteLine("Daily/Total Trading losses reach");
                currentSignal = null;
            }
        }

        public bool CheckOverallLoss()
        {
            double equity = _client.AccountInfoDouble(MtApi5.ENUM_ACCOUNT_INFO_DOUBLE.ACCOUNT_EQUITY);

            if (equity > peakEquity)
                peakEquity = equity;   // update peak equity

            if (equity <= peakEquity - 1000.0)
            {
                Console.WriteLine("❌ FTMO: Max OVERALL loss reached. STOP trading!");
                return true;
            }

            return false;
        }
        public double GetFtmoDailyPL()
        {
            double closed = GetTodayClosedPL();
            double floating = GetFloatingPL();

            return closed + floating;
        }

        private double GetTodayClosedPL()
        {
            DateTime todayStart = DateTime.Today;
            DateTime now = DateTime.Now;

            // Select today's history
            bool ok = _client.HistorySelect(todayStart, now);
            if (!ok)
            {
                Console.WriteLine("HistorySelect failed!");
                return 0;
            }

            double total = 0;
            int deals = _client.HistoryDealsTotal();

            for (int i = 0; i < deals; i++)
            {
                ulong ticket = _client.HistoryDealGetTicket(i);
                double profit = _client.HistoryDealGetDouble(ticket, MtApi5.ENUM_DEAL_PROPERTY_DOUBLE.DEAL_PROFIT);
                total += profit;
            }

            return total;
        }

        public double GetFloatingPL()
        {
            double total = 0;
            int positions = _client.PositionsTotal();

            for (int i = 0; i < positions; i++)
            {
                ulong ticket = _client.PositionGetTicket(i);

                // Must select position FIRST
                bool ok = _client.PositionSelectByTicket(ticket);
                if (!ok)
                    continue;

                // Now read position profit
                double profit = _client.PositionGetDouble(
                    MtApi5.ENUM_POSITION_PROPERTY_DOUBLE.POSITION_PROFIT
                );

                total += profit;
            }

            return total;
        }

        static double CalculateATR(MqlRates[] rates, int period)
        {
            int len = rates.Length;
            if (len <= period)
                return 0;

            double sumTR = 0;

            // start from the point where we have a previous candle
            for (int i = len - period; i < len; i++)
            {
                double high = rates[i].high;
                double low = rates[i].low;
                double prevClose = rates[i - 1].close;

                double tr = Math.Max(high - low,
                             Math.Max(Math.Abs(high - prevClose),
                                      Math.Abs(low - prevClose)));

                sumTR += tr;
            }

            return sumTR / period; // average true range
        }

        private void InitializeTrade()
        {
            if (ResetTrade)
            {
                var trade = new Trade
                {
                    Ticket = 0,
                    Symbol = "",
                    Side = TradeSide.NoTrade.ToString(),
                    OpenTime = DateTime.UtcNow,
                    ContractSize = 0.00,
                    EntryPrice = 0.00,
                    Volume = (double)0.00,
                    StopLoss = (double)0.00,
                    IsClosed = true,
                    TakeProfit = (double)0.00,
                    PeakOrTrough = 0.00 // initialize peak/trough to entry price
                };
                TradingEventBus.ActiveTrade(trade);

                TradingEventBus.Sniper(new SniperEvent()
                {
                    Regime = MarketRegime.NoQualifyingSetupFound.ToString(),
                    ActionDescription = "No Qualifiying Setup Found.",
                    SniperAction = TradeAction.Hold.ToString()

                });

                ResetTrade = false;
            }
        }
        private void setCoolDownCandleStopLossHit()
        {
            cooldownRemaining = 2;  // number of candles left in cooldown
        }

        private void tradeProfit(string symbol)
        {
            double profit = _client.HistoryDealGetDouble(ticket, ENUM_DEAL_PROPERTY_DOUBLE.DEAL_PROFIT);
            SymbolTradeCooldown.MarkExit(symbol, globalEntryModel, profit);
            if (engineTrades.TryGetValue(EngineConfig.Mode, out var engine))
            {
                if (profit < 0)
                    engine.losses++;
                else if (profit > 0)
                    engine.wins++;
            }
        }
        private void ResetTradeState(string symbol)
        {
            tradeProfit(symbol);

            foreach (var engine in engineTrades)
            {
                int total = engine.Value.wins + engine.Value.losses;
                double winRate = total == 0 ? 0 : (double)engine.Value.wins / total * 100;

                Console.WriteLine($"{engine.Key}: {winRate:F2}%");
            }

            tradeBegun = false;
            obTriggered = false;
            _orderInProgress = false;
            if (ctx != null)
                ctx.AllowSniperNextCandle = false;

            currentSignal = null;
            isTradeClosed = true;
            TradeServiceEngine.closingTrade = false;
            setCoolDownCandleStopLossHit();

            openTrades.Clear();

        }

        double ConvertAtrToUsd(
            string symbol,
            double atrPrice,
            double volume)
        {
            double tickValue =
                _client.SymbolInfoDouble(
                    symbol,
                    ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_TRADE_TICK_VALUE);

            double tickSize =
                _client.SymbolInfoDouble(
                    symbol,
                    ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_TRADE_TICK_SIZE);

            if (tickSize <= 0 || tickValue <= 0)
                return 0;

            double ticks = atrPrice / tickSize;

            return ticks * tickValue * volume;
        }

        private SymbolContext ctx;
        private double _accountEquityBeforeTrade;
        private double bid = 0;
        private double ask = 0;
        private double mid = 0;
        DateTime lastSeenH1Time;
        private TransitionResult transition;
        private static string tradingEngine = "";
        private EntryModel globalEntryModel;
        private static readonly Dictionary<string, DateTime> _entryLockCandle = new();
        private static readonly HashSet<string> _orderInProgressSet = new();

        public async Task RunParallelStrategy(CancellationToken ct)
        {
            // =========================================================
            // 📈 MARKET DATA + INDICATORS
            // =========================================================
            _client.CopyRates(SYMBOL, ENUM_TIMEFRAMES.PERIOD_H1, 1, BarsToFetch, out var rates1H);
            _client.CopyRates(SYMBOL, ENUM_TIMEFRAMES.PERIOD_H4, 1, BarsToFetch, out var rates4H);

            var currentCandleTime = rates1H[^1].time;

            var emaFast1H = CalculateEMA(rates1H, 9);
            var emaSlow1H = CalculateEMA(rates1H, 21);
            var emaFast4H = CalculateEMA(rates4H, 9);
            var emaSlow4H = CalculateEMA(rates4H, 21);

            var lastCandle = rates1H[0].close;
            double atrRaw = CalculateATR(rates1H, 14);

            // =========================================================
            // 🛡️ EQUITY PROTECTION (ACCOUNT LEVEL – FIRST)
            // =========================================================
            double accountEquity = _client.AccountInfoDouble(ENUM_ACCOUNT_INFO_DOUBLE.ACCOUNT_EQUITY);
            var trackedTrade = openTrades.FirstOrDefault();
            if (_client.PositionsTotal() == 0 && GlobalTradeManager.HasActiveLock(_provider))
            {
                LogFile.Write("🔓 No open positions found — releasing stale global lock");
                GlobalTradeManager.ReleaseTradeLock(_provider);
            }

            if (trackedTrade != null)
            {
                GoldBreakEvenLock.Apply(trackedTrade, mid, atrRaw, ModifyTrade);
                if (LiveAdverseMoveGuard.ShouldClose(
                    trackedTrade,
                    trackedTrade.Symbol,
                    bid,
                    ask,
                    atrRaw,
                    rates1H,
                    emaFast1H,
                    emaSlow1H))
                {
                    generalCloseTrade(bid, ask, ctx);
                    return;
                }
            }

            if (trackedTrade != null)
            {

                PerTradeEquityLossGuard.Arm(
                    trackedTrade.Ticket,
                    trackedTrade.Symbol,
                    accountEquity
                );

                if (PerTradeEquityLossGuard.ShouldClose(trackedTrade.Ticket, trackedTrade.Symbol, accountEquity))
                {
                    generalCloseTrade(bid, ask, ctx);
                    PerTradeEquityLossGuard.Reset(trackedTrade.Ticket);
                    return;
                }

                if (!HasAnyOpenPositionBySymbol(trackedTrade.Symbol))
                {
                    if (GlobalTradeManager.HasActiveLock(_provider))
                    {
                        LogFile.Write("🔓 No open positions found on tracked symbols — releasing stale global lock");
                        generalCloseTrade(bid, ask, ctx);
                        return;
                    }
                }
            }

            if (trackedTrade != null)
            {

                if (currentCandleTime != lastSeenH1Time)
                {
                    // 🎯 NEW candle detected
                    lastSeenH1Time = currentCandleTime;

                    MqlRates lastClosed = rates[1];
                    EvaluateTradeProvenState(SYMBOL, trackedTrade, lastClosed, emaFast1H);
                    // ✅ SAFE PLACE to evaluate:
                    // - IsProven logic
                    // - Sniper continuation
                    // - structure logic
                }

                if (EquityGivebackProtection.ShouldCloseAll(accountEquity, "FTMO10K"))
                {
                    LogFile.Write($"🛑 EQUITY GIVEBACK STOP: equity={accountEquity:F2} startingEquity={startingEquity:F2}");
                    PostExitCooldown.RegisterForcedExit(SYMBOL); // 👈 memory added
                    generalCloseTrade(bid, ask, ctx);
                    EquityGivebackProtection.Reset(accountEquity);

                    return;
                }
            }
            // =========================================================
            // 🛡️ RESET TRADE
            // =========================================================
            LogFile.Write("MT5 Connected - Starting Trade Logic help cant understand");
            InitializeTrade();

            showBalances();
            Results<string> results = new Results<string>
            {
                messageType = "string",
                message = "connected",
                Data = "Connected to MT5",
                status = 200
            };
            await _onMessageReceived(results);




            // =========================================================
            // 🔄 FORCE CLOSE FLOW (MANUAL / ENGINE)
            // =========================================================
            if (TradeServiceEngine.IsClosingTrade && _client.PositionsTotal() > 0)
            {
                _tradeCoordinator.Clear();
                ctx.ClearListOfStates();
                generalCloseTrade(bid, ask, ctx);
            }

            // =========================================================
            // 🧹 RESET STATE IF NO POSITIONS EXIST
            // =========================================================
            if (openTrades.Count > 0 && _client.PositionsTotal() == 0)
            {
                ResetTrade = true;
                ResetTradeState(ctx.Symbol);
            }
            if (SYMBOL != null)
            {
                if (PostExitCooldown.IsInCooldown(SYMBOL))
                {
                    LogFile.Write($"⏳ Cooldown active for {SYMBOL} — waiting 30 minutes after forced exit");
                    return;
                }
            }
            // =========================================================
            // 🔁 MANAGE OPEN TRADE (SYMBOL OWNER ONLY)
            // =========================================================
            if (trackedTrade != null)
            {
                if (SYMBOL != trackedTrade.Symbol)
                    return;

                LogFile.Write($"MANAGING OPEN TRADE ON {trackedTrade.Symbol}");

                _client.CopyRates(
                    trackedTrade.Symbol,
                    ENUM_TIMEFRAMES.PERIOD_H1,
                    1,
                    BarsToFetch,
                    out MqlRates[] trackerTradeRates1H
                );

                // =======================================
                // 🔥 ALWAYS UPDATE CONTEXT (CRITICAL)
                // =======================================
                var last = trackerTradeRates1H[^1];
                var prev = trackerTradeRates1H[^2];

                MomentumResult momentum = HybridMomentumEngine.Calculate(last, prev, rates1H, emaFast1H, emaSlow1H, atrRaw, false);
                ctx.PreviousMomentum = ctx.CurrentMomentum;
                ctx.CurrentMomentum = momentum.Hybrid;

                ManageOpenTrades(
                    mid,
                    bid,
                    ask,
                    tradingPairsDto,
                    trackerTradeRates1H,
                    ctx,
                    momentum,
                    currentSignal == "BUY" ? true :
                    currentSignal == "SELL" ? false : null
                );

                double emaCurrentFast = emaFast1H[^1];
                if (MomentumFailureExit.ShouldForceExit(
                    trackedTrade,
                    trackedTrade.Symbol,
                    trackerTradeRates1H,
                    emaCurrentFast,
                    atrRaw))
                {
                    TradeServiceEngine.closingTrade = true;
                    LogFile.Write($"[{trackedTrade.Symbol}] Early exit: momentum failure");
                    return;
                }

                HandleLiveEma9Exit(
                    trackedTrade.Symbol,
                    trackedTrade,
                    _client,
                    atrRaw,
                    emaFast1H,
                    emaSlow1H,
                    ctx);

                return;
            }

            // =========================================================
            // 🔒 GLOBAL ENTRY LOCK (NO NEW TRADES WHILE ANY OPEN)
            // =========================================================
            if (_client.PositionsTotal() > 0)
            {
                LogFile.Write("Global trade lock active — skipping new entries");
                return;
            }


            // =========================================================
            // 📊 PERFORMANCE REPORTING
            // =========================================================
            if (closedTrades != null && closedTrades.Count > 0)
            {
                var context = calculator.Calculate(
                    currentEquity: accountEquity,
                    closedTrades: closedTrades,
                    nowUtc: DateTime.UtcNow
                );

                TradingEventBus.Performance(context);
            }

            // =========================================================
            // 🛑 MONTH / SESSION GUARDS
            // =========================================================
            if (DateTime.UtcNow.Month == 12)
            {
                DecemberRiskGuard.Evaluate(accountEquity);
                if (DecemberRiskGuard.TradingDisabled)
                    return;
            }

            DailyModeUpdate();

            checkTradingLosses();

            // =========================================================
            // 🔍 SYMBOL SELECTION (ONLY IF NO TRADE EXISTS)
            // =========================================================
            if (TradingConfig.Mode == TradingMode.MultiSymbolArmed &&
                openTrades.Count == 0)
            {
                var coordinator = new MarketStateCoordinator(
                    _client,
                    symbolsToTrade,
                    new TradeCoordinator(),
                    new TradeCoordinator(),
                    new TradeCoordinator(),
                    new TradeCoordinator(),
                    _contexts
                );

                SYMBOL = coordinator.ScanAndSelectSymbol();
                if (SYMBOL == null)
                    return;

                ctx = coordinator.GetContext(SYMBOL);

                TradingEventBus.State(new BotStateUpdate
                {
                    state = ctx.State.ToString(),
                    Symbol = SYMBOL,
                    PullbackSufficient = ctx.PullbackSufficient,
                    AtrCoolDown = ctx.AtrCooldown,
                    PriceAccepted = ctx.PriceAccepted,
                    ListOfStates = ctx.GetListOfStates()
                });

                if (!ctx.IsArmed)
                    return;
            }

            Console.WriteLine($"Selected symbol: {SYMBOL}");
            LogFile.Write($"Selected symbol: {SYMBOL}");


            tradingPairsDto = await _tradingPairProperties.GetTradingPairPropertiesAsync(TradingConfig.Mode == TradingMode.XauUsdOnly ? SYMBOL.Substring(0, 6) : SYMBOL);

            bid = _client.SymbolInfoDouble(SYMBOL, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);
            ask = _client.SymbolInfoDouble(SYMBOL, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);
            mid = (bid + ask) / 2.0;
            var priceCalculator = PriceCalculator.GetPrices(_client, SYMBOL);

            // 1️⃣ Update market state first (already done by coordinator)

            // 2️⃣ Only continue if symbol is selected / armed
            if (ctx.State == MarketState.Armed)
            {
                // -----------------------------------------
                // 🎯 1) Run TF Momentum Governance
                // -----------------------------------------

                var (tradeDecision, emaCompressed) = CheckMultiTfMomentum(
                    ctx,
                    ask, bid,
                    emaFast1H, emaSlow1H,
                    emaFast4H, emaSlow4H,
                    rates1H,
                    _client,
                    out _, out _,
                    currentSignal == "BUY" ? true :
                    currentSignal == "SELL" ? false : null
                );

                // 🔴 EXIT first
                if (tradeDecision.Action == TradeAction.CloseTrade)
                {
                    generalCloseTrade(bid, ask, ctx);
                    return;
                }

                if (tradeDecision.Action == TradeAction.Hold)
                {
                    // ✅ If momentum not aligned, skip everything else and wait for next candle
                    return;
                }

                // update global for UI
                _m15Service.Update(SYMBOL);
                ctx.CurrentMomentum = tradeDecision.Momentum;
                var (action, entry) = ExecutionLoop(
                    SYMBOL,
                    _client,
                    rates1H,
                    atrRaw,
                    ctx.AtrPercentile,
                    emaFast1H,
                    emaSlow1H,
                    ctx,
                    _m15Service, tradeDecision, emaCompressed, out var engine);

                tradingEngine = engine;

                if (action == TradeAction.EnterBuy || action == TradeAction.EnterSell)
                {
                    if (engine.Contains("Trend") || engine.Contains("Sniper")
                         || engine.Contains("Exhaustion") || engine.Contains("Cascade"))
                    {

                        double ema9 = emaFast1H[^1];
                        double ema21 = emaSlow1H[^1];
                        double prevEma9 = emaFast1H[^2];
                        double prevEma21 = emaSlow1H[^2];

                        var market = MarketConditionEngine.Calculate(rates1H, emaFast1H, emaSlow1H, atrRaw);

                        transition =
                           TransitionZoneEngine.Evaluate(
                                SYMBOL,
                                atrRaw,
                                 ctx.AtrPercentile,
                                ema9,
                                ema21,
                                prevEma9,
                                prevEma21,
                                ctx.CurrentMomentum,
                                market.BodyRatio,
                                market.RangeAtr,
                                market.LastClose,
                                market.PrevClose,
                                action == TradeAction.EnterBuy ? true : false,
                                action == TradeAction.EnterSell ? true : false);


                        // ---------------------------------
                        // BLOCK HIGH NOISE
                        // ---------------------------------
                        if (transition.State ==
                              TransitionZoneEngine.TransitionState.HighNoise)
                        {
                            LogFile.Write(
                                $"[{SYMBOL}] Blocked: high-noise transition zone");

                            return;
                        }

                    }

                    if (!HasAnyOpenPositionByTicket())
                    {
                        GlobalTradeManager.ReleaseTradeLock(_provider);
                    }

                    if (GlobalTradeManager.HasActiveLock(_provider))
                    {
                        LogFile.Write("Global trade lock active — skipping new entries");
                        return;
                    }

                    if (!GlobalTradeManager.TryAcquireTradeLock(_provider))
                    {
                        LogFile.Write("❌ Failed to acquire global trade lock");
                        return;
                    }

                    LogFile.Write("🔒 Global trade lock acquired");

                    if (!_orderInProgress &&
                          _lastOrderCandleTime != currentCandleTime)
                    {
                        _orderInProgress = true;
                        tradeBegun = true;

                        var candleTime = currentCandleTime;

                        // block if already traded this candle
                        if (_entryLockCandle.TryGetValue(SYMBOL, out var lstCandle) && lstCandle == candleTime)
                            return;

                        // block if an order is in progress for this symbol
                        if (_orderInProgressSet.Contains(SYMBOL))
                            return;

                        _entryLockCandle[SYMBOL] = candleTime;
                        _orderInProgressSet.Add(SYMBOL);

                        try
                        {
                            EntryModel model = engine.Contains("TrendRide") || engine.Contains("fvg")
                                ? EntryModel.Continuation :
                                engine.Contains("Live")
                                ? EntryModel.BreakoutContinuation : engine.Contains("Compression")
                                ? EntryModel.CompressionBreakoutContinuation : EntryModel.Sniper;
                            globalEntryModel = model;

                            if (SymbolTradeCooldown.IsBlocked(SYMBOL, model))
                            {
                                return;
                            }

                            await OpenTrade(
                                action == TradeAction.EnterBuy ? TradeSide.Buy : TradeSide.Sell,
                                action == TradeAction.EnterBuy ? ask : bid,
                                LotSizingMode.Fixed, entry, rates1H, tradeDecision, transition);

                            // ✅ RESET STRUCTURE HERE
                            ResetStructuralState(ctx);

                            // ✅ Lock this candle
                            _lastOrderCandleTime = currentCandleTime;

                            await _notificationService.sendNotification(
                                $"{engine} - {action.ToString()} — entry approved - {SYMBOL}",
                                _configuration["CHAT_ID"],
                                _configuration["BOT_TOKEN"]
                            );
                        }
                        finally
                        {
                            // IMPORTANT: always release in-progress flag
                            _orderInProgressSet.Remove(SYMBOL);
                        }
                        return;
                    }
                }
            }
        }

        bool HasAnyOpenPositionBySymbol(string symbol)
        {

            if (_client.PositionSelect(symbol))
            {
                LogFile.Write($"🔒 Open position still exists on {symbol}");
                return true;
            }


            return false;
        }

        bool HasAnyOpenPositionByTicket()
        {
            List<long> mt5Tickets = GetAllOpenPositionTickets();

            long ticket = mt5Tickets.Select(s => s).SingleOrDefault();

            if (_client.PositionSelectByTicket((ulong)ticket))
            {
                LogFile.Write($"🔒 Open position still exists on {ticket}");
                return true;
            }


            return false;
        }
        public enum TradeCloseReason
        {
            None,
            StopLoss,
            TakeProfit,
            Other
        }
        public static TradeCloseReason CheckTradeClose(
        TrackedTrade trade,
        double bid,
        double ask)
        {
            if (trade.IsClosed)
                return TradeCloseReason.None;

            // SELL trade
            if (trade.EntryPrice > trade.StopLoss)
            {
                if (ask >= trade.StopLoss)
                {
                    trade.IsClosed = true;
                    LogFile.Write($"❌ {trade.Symbol} SL hit");
                    return TradeCloseReason.StopLoss;
                }

                if (bid <= trade.TakeProfit)
                {
                    trade.IsClosed = true;
                    LogFile.Write($"🎯 {trade.Symbol} TP hit");
                    return TradeCloseReason.TakeProfit;
                }
            }
            else // BUY trade
            {
                if (bid <= trade.StopLoss)
                {
                    trade.IsClosed = true;
                    LogFile.Write($"❌ {trade.Symbol} SL hit");
                    return TradeCloseReason.StopLoss;
                }

                if (ask >= trade.TakeProfit)
                {
                    trade.IsClosed = true;
                    LogFile.Write($"🎯 {trade.Symbol} TP hit");
                    return TradeCloseReason.TakeProfit;
                }
            }

            return TradeCloseReason.None;
        }
        public static void EvaluateTradeProvenState(
            string symbol,
            TrackedTrade t,
            MqlRates lastClosed,
            double[] emaFastH1)
        {
            if (t == null || t.IsProven)
                return;

            if (emaFastH1 == null || emaFastH1.Length < 3)
                return;

            // ---------------------------------
            // 1️⃣ EMA9 hold check (closed candle)
            // ---------------------------------
            bool emaHeld =
                t.Side == TradeSide.Buy
                    ? lastClosed.close >= emaFastH1[^1]
                    : lastClosed.close <= emaFastH1[^1];

            // ---------------------------------
            // 2️⃣ EMA9 slope check
            // ---------------------------------
            bool slopeOk =
                t.Side == TradeSide.Buy
                    ? emaFastH1[^1] > emaFastH1[^2]
                    : emaFastH1[^1] < emaFastH1[^2];

            // ---------------------------------
            // 3️⃣ Structure advance check
            // ---------------------------------

            bool structureAdvance =
                t.Side == TradeSide.Buy
                    ? lastClosed.high > t.EntryPrice
                    : lastClosed.low < t.EntryPrice;

            // ---------------------------------
            // 4️⃣ Confirmation count
            // ---------------------------------
            int confirmations =
                (emaHeld ? 1 : 0) +
                (slopeOk ? 1 : 0) +
                (structureAdvance ? 1 : 0);

            if (confirmations >= 2)
            {
                t.IsProven = true;

                LogFile.Write(
                    $"[TradeProven] {symbol} | model={t.Model} | side={t.Side} | confirmations={confirmations}");
            }
        }
        private static void HandleLiveEma9Exit(
            string symbol,
            TrackedTrade t,
            MtApi5Client client,
            double atr,
            double[] emaFastH1,
            double[] emaSlowH1,
            SymbolContext ctx)
        {
            if (t == null || emaFastH1 == null || emaSlowH1 == null)
                return;

            // ---------------------------------
            // Determine side
            // ---------------------------------
            TradeAction side = t.Side.ToString() == TradeAction.Buy.ToString()
                ? TradeAction.EnterBuy
                : TradeAction.EnterSell;

            // ---------------------------------
            // Determine profile based on entry model
            // ---------------------------------
            EntryExitProfile profile =
                t.Model == EntryModel.Velocity && t.IsProven ? EntryExitProfile.VelocityProven :
                t.Model == EntryModel.Velocity ? EntryExitProfile.Velocity :
                t.Model == EntryModel.Sniper ? EntryExitProfile.Sniper :
                EntryExitProfile.Continuation;

            double ema9 = emaFastH1[^1];
            double ema21 = emaSlowH1[^1];

            var decision = LiveEma9ExitGuard.Evaluate(
                symbol,
                client,
                atr,
                ema9,
                ema21,
                side,
                profile,
                tradeOpen: true,
                ctx.IsShockActive,
                out string reason);

            if (decision == LiveExitDecision.ExitToNextRegime)
            {


                LogFile.Write(
                    $"[LiveEma9Exit] {symbol} EXIT→NEXT | model={t.Model} | {reason}");


                TradeServiceEngine.closingTrade = true;
                ctx.AllowSniperNextCandle = true;
            }
            else if (decision == LiveExitDecision.ExitAndDisarm)
            {
                LogFile.Write(
                    $"[LiveEma9Exit] {symbol} EXIT+DISARM | model={t.Model} | {reason}");

                TradeServiceEngine.closingTrade = true;
                ctx.TransitionTo(MarketState.Pullback);
            }
        }
        private void ResetStructuralState(SymbolContext ctx)
        {
            ctx.ActiveOrderBlock = null;
            ctx.OrderBlockCreatedAt = null;
            ctx.PeakAtr = 0.0;
            ctx.PeakAtrCaptured = false;
            ctx.ResetPullback();
            ctx.ResetExpansion();
        }

        private (TradeAction, EntryModel) ExecutionLoop(string symbol,
            MtApi5Client client,
            MqlRates[] rates1H,
            double atr,
            double atrPercentile,
            double[] emaFast,
            double[] emaSlow,
            SymbolContext ctx,
            M15ConfirmationService _m15Service, TradeDecision tradeDecision, bool emaCompressed, out string engine)
        {
            //out TradeAction action
            var regime = MarketRegimeDetector.Detect(
                symbol,
                rates1H,
                emaFast,
                emaSlow,
                atr
            );
            TradingEventBus.State(new BotStateUpdate()
            {
                state = ctx.State.ToString(),
                Symbol = symbol,
                PullbackSufficient = ctx.PullbackSufficient,
                AtrCoolDown = ctx.AtrCooldown,
                PriceAccepted = ctx.PriceAccepted,
                ListOfStates = ctx.GetListOfStates(),
                Regime = regime.ToString(),
                Momentum = tradeDecision.Momentum,
            });

            LogFile.Write($"[{symbol}] Regime: {regime}");

            if (ctx.State == MarketState.Armed)
            {

                bool shockDetected = ShockGuardEngine.DetectShock(
                rates1H,
                atr,
                ShockConfig.LookbackCandles,
                ShockConfig.Multiplier,
                ShockConfig.BodyRatio
               );

                if (shockDetected)
                {
                    ctx.IsShockActive = true;
                    ctx.LastShockCandleTime = rates1H[^1].time;
                    ctx.CandlesSinceShock = 0;
                }

                if (ctx.LastShockCandleTime.HasValue &&
                      ctx.LastShockCandleTime.Value < rates1H[^1].time)
                {
                    ctx.CandlesSinceShock++;
                }

                // auto-clear shock
                if (ctx.IsShockActive &&
                    ctx.CandlesSinceShock >= ShockConfig.LookbackCandles)
                {
                    ctx.IsShockActive = false;
                }

                string phaseReason;

                ctx.Phase = MarketPhaseEngine.Evaluate(
                    ctx,
                    currentH1CandleTime: rates1H[^1].time,
                    hasOpenTrade: openTrades.Any(),
                    shockDetectedThisCandle: ctx.IsShockActive,
                    atrPercentile: atrPercentile,
                    emaCompressed: emaCompressed,
                    m15Confirmed: ctx.M15StructureReady,
                    isArmed: ctx.State == MarketState.Armed,
                    out phaseReason
                );

                ctx.PhaseReason = phaseReason;

                TradingEventBus.MarketPhaseUpdate(ctx.Phase);

                double currentPrice = rates1H[^1].close;

                MarketCondition condition = MarketConditionGuard.Evaluate(
                    symbol,
                    rates1H,
                    currentPrice,
                    emaFast[^1],
                    atr,
                    out var conditionReason);


                if (OrderBlockFvgEngine.ShouldEnter(symbol, rates1H, atr, emaFast, emaSlow, out var obAction))
                {
                    engine = "OrderBlockFvgEngine";
                    if (SupportResistanceEngine.ShouldBlockTrade(
                            symbol,
                            obAction,
                            rates1H,
                            atr,
                            tradeDecision.Momentum,
                            currentPrice))
                    {
                        return (TradeAction.DoNothing, EntryModel.Nothing);
                    }
                    if (!_m15Service.IsTradeAllowed(SYMBOL, obAction, EntryModel.Sniper, tradeDecision.Momentum, out var reason))
                    {
                        return (TradeAction.DoNothing, EntryModel.Nothing);
                    }
                    TradingEventBus.M15SnapshotUpdate(new M15Snapshot()
                    {
                        Message = "M15 Confirmed",
                        Symbol = symbol,
                    });
                    TradingEventBus.Decision(new DecisionEvent
                    {
                        Symbol = ctx.Symbol,
                        Action = obAction.ToString(),
                        Category = DecisionCategory.Sniper.ToString(),
                        Reason = $"OB Pull Back {obAction.ToString()}",
                        Message = $"OB Pull Back Continuation {obAction.ToString()}",
                        Momentum = tradeDecision.Momentum,
                        EarlyMomentum = false,
                        EmaCompressed = false,
                        Regime = DecisionCategory.Continuation.ToString(),
                        Time = DateTime.UtcNow
                    });

                    return (obAction, EntryModel.PullbackContinuation);
                }

                if (BreakoutContinuationEngine.ShouldEnterLive(
                    SYMBOL,
                    rates1H,
                    atr,
                    emaFast,
                    emaSlow,
                    tradeDecision.Momentum,
                    bid,
                    ask,
                    out TradeAction breakoutContinuationAction))
                {
                    engine = "BreakoutContinuationEngine";

                    if (SupportResistanceEngine.ShouldBlockTrade(
                            symbol,
                            breakoutContinuationAction,
                            rates1H,
                            atr,
                            tradeDecision.Momentum,
                            currentPrice))
                    {
                        return (TradeAction.DoNothing, EntryModel.Nothing);
                    }
                    if (!_m15Service.IsTradeAllowed(SYMBOL, breakoutContinuationAction, EntryModel.BreakoutContinuation, tradeDecision.Momentum, out var reason))
                    {
                        return (TradeAction.DoNothing, EntryModel.Nothing);
                    }
                    TradingEventBus.M15SnapshotUpdate(new M15Snapshot()
                    {
                        Message = "M15 Confirmed",
                        Symbol = symbol,
                    });
                    TradingEventBus.Decision(new DecisionEvent
                    {
                        Symbol = ctx.Symbol,
                        Action = breakoutContinuationAction.ToString(),
                        Category = DecisionCategory.Sniper.ToString(),
                        Reason = $"Breakout Continuation {breakoutContinuationAction.ToString()}",
                        Message = $"Breakout Continuation {breakoutContinuationAction.ToString()}",
                        Momentum = tradeDecision.Momentum,
                        EarlyMomentum = false,
                        EmaCompressed = false,
                        Regime = DecisionCategory.Continuation.ToString(),
                        Time = DateTime.UtcNow
                    });

                    return (breakoutContinuationAction, EntryModel.Continuation);
                }

                if (CompressionBreakoutEngine.ShouldEnterLive(
                    SYMBOL,
                    rates1H,
                    atr,
                    emaFast,
                    emaSlow,
                    tradeDecision.Momentum,
                    bid,
                    ask,
                    out TradeAction liveBreakoutAction))
                {
                    engine = "CompressionLiveBreakoutEngine";

                    if (SupportResistanceEngine.ShouldBlockTrade(
                            symbol,
                            liveBreakoutAction,
                            rates1H,
                            atr,
                            tradeDecision.Momentum,
                            currentPrice))
                    {
                        return (TradeAction.DoNothing, EntryModel.Nothing);
                    }
                    if (!_m15Service.IsTradeAllowed(SYMBOL, liveBreakoutAction, EntryModel.CompressionBreakoutContinuation, tradeDecision.Momentum, out var reason))
                    {
                        return (TradeAction.DoNothing, EntryModel.Nothing);
                    }
                    TradingEventBus.M15SnapshotUpdate(new M15Snapshot()
                    {
                        Message = "M15 Confirmed",
                        Symbol = symbol,
                    });
                    TradingEventBus.Decision(new DecisionEvent
                    {
                        Symbol = ctx.Symbol,
                        Action = liveBreakoutAction.ToString(),
                        Category = DecisionCategory.Sniper.ToString(),
                        Reason = $"Breakout Continuation {liveBreakoutAction.ToString()}",
                        Message = $"Breakout Continuation {liveBreakoutAction.ToString()}",
                        Momentum = tradeDecision.Momentum,
                        EarlyMomentum = false,
                        EmaCompressed = false,
                        Regime = DecisionCategory.Continuation.ToString(),
                        Time = DateTime.UtcNow
                    });

                    return (liveBreakoutAction, EntryModel.PullbackContinuation);
                }

                if (CompressionBreakoutEngine.ShouldEnter(
                    SYMBOL,
                    rates1H,
                    atr,
                    emaFast,
                    emaSlow,
                    tradeDecision.Momentum,
                    out var compressionAction))
                {
                    engine = "CompressionBreakoutEngine";

                    if (SupportResistanceEngine.ShouldBlockTrade(
                            symbol,
                            compressionAction,
                            rates1H,
                            atr,
                            tradeDecision.Momentum,
                            currentPrice))
                    {
                        return (TradeAction.DoNothing, EntryModel.Nothing);
                    }
                    if (!_m15Service.IsTradeAllowed(SYMBOL, compressionAction, EntryModel.CompressionBreakoutContinuation, tradeDecision.Momentum, out var reason))
                    {
                        return (TradeAction.DoNothing, EntryModel.Nothing);
                    }
                    TradingEventBus.M15SnapshotUpdate(new M15Snapshot()
                    {
                        Message = "M15 Confirmed",
                        Symbol = symbol,
                    });
                    TradingEventBus.Decision(new DecisionEvent
                    {
                        Symbol = ctx.Symbol,
                        Action = compressionAction.ToString(),
                        Category = DecisionCategory.Sniper.ToString(),
                        Reason = $"Breakout Continuation {compressionAction.ToString()}",
                        Message = $"Breakout Continuation {compressionAction.ToString()}",
                        Momentum = tradeDecision.Momentum,
                        EarlyMomentum = false,
                        EmaCompressed = false,
                        Regime = DecisionCategory.Continuation.ToString(),
                        Time = DateTime.UtcNow
                    });

                    return (compressionAction, EntryModel.PullbackContinuation);
                }
                // 5) Trend Ride Continuation (live, no M15 confirm)
                if (TrendRideContinuationEngine.ShouldEnter(
                symbol,
                rates1H,
                atr,
                emaFast,
                emaSlow,
                atrPercentile,
                ctx,
                _client,
                out var trendRideAction))
                {
                    engine = "trend ride";
                    if (SupportResistanceEngine.ShouldBlockTrade(
                            symbol,
                            trendRideAction,
                            rates1H,
                            atr,
                            tradeDecision.Momentum,
                            currentPrice))
                    {
                        return (TradeAction.DoNothing, EntryModel.Nothing);
                    }
                    bool m15Allowed = _m15Service.IsTradeAllowed(SYMBOL, trendRideAction, EntryModel.Continuation, tradeDecision.Momentum, out var trendReason);

                    string m15DecisionReason;

                    bool allowTrade = M15ConfirmationGate.ShouldAllowTrade(
                        symbol,
                        trendRideAction,
                        EntryModel.TrendRide,
                        m15Allowed,
                        trendReason,
                        ctx,
                        atrPercentile,
                        out m15DecisionReason
                    );

                    if (!allowTrade)
                    {
                        LogFile.Write($"⏸️ M15 blocked TrendRide ({trendRideAction}) — {m15DecisionReason}");
                        return (TradeAction.DoNothing, EntryModel.Nothing);
                    }

                    double exhaustionMomentumThreshold =
                        symbol.Contains("XAU") ? 2.2 :
                        symbol.Contains("ETH") ||
                        symbol.Contains("BTC") ||
                        symbol.Contains("SOL") ||
                        symbol.Contains("BNB") ||
                        symbol.Contains("XRP") ? 3.0 :
                        symbol.Contains("US100") ||
                        symbol.Contains("NAS") ? 2.8 :
                        2.0; // FX

                    bool dangerousLateMove =
                        tradeDecision.Momentum >= exhaustionMomentumThreshold ||
                        regime == MarketRegimeDetector.Regime.Exhaustion;

                    if (!m15Allowed && dangerousLateMove)
                    {
                        LogFile.Write(
                            $"⛔ TrendRide blocked: M15 disagreement during late expansion");

                        return (TradeAction.DoNothing, EntryModel.Nothing);
                    }

                    TradingEventBus.M15SnapshotUpdate(new M15Snapshot()
                    {
                        Message = "M15 Confirmed",
                        Symbol = symbol,
                    });
                    TradingEventBus.Decision(new DecisionEvent
                    {
                        Symbol = ctx.Symbol,
                        Action = trendRideAction.ToString(),
                        Category = DecisionCategory.TrendRide.ToString(),
                        Reason = $"Trend Ride Continuation {trendRideAction.ToString()}",
                        Message = $"Trend Ride Continuation {trendRideAction.ToString()}",
                        Momentum = tradeDecision.Momentum,
                        EarlyMomentum = false,
                        EmaCompressed = false,
                        Regime = DecisionCategory.Continuation.ToString(),
                        Time = DateTime.UtcNow
                    });

                    return (trendRideAction, EntryModel.TrendRideContinuation);
                }
            }

            engine = "None";
            return (TradeAction.DoNothing, EntryModel.Nothing);

        }

        private class BreakoutProfile
        {
            public double BreakBuffer;
            public double MinBreakCandle;
        }

        private static BreakoutProfile GetVelocityBreakoutProfile(string symbol)
        {
            if (symbol.Contains("XAU"))
            {
                return new BreakoutProfile
                {
                    BreakBuffer = 0.12,
                    MinBreakCandle = 0.7
                };
            }

            if (symbol.Contains("US100") || symbol.Contains("NAS"))
            {
                return new BreakoutProfile
                {
                    BreakBuffer = 0.10,
                    MinBreakCandle = 0.65
                };
            }

            if (symbol.Contains("USD") &&
                (symbol.Contains("ETH") || symbol.Contains("BTC") ||
                 symbol.Contains("SOL") || symbol.Contains("AVAX") ||
                 symbol.Contains("BNB") || symbol.Contains("XRP")))
            {
                return new BreakoutProfile
                {
                    BreakBuffer = 0.15,
                    MinBreakCandle = 0.75
                };
            }

            // FX
            return new BreakoutProfile
            {
                BreakBuffer = 0.08,
                MinBreakCandle = 0.6
            };
        }
        private static BreakoutProfile GetBreakoutProfile(string symbol)
        {
            if (symbol.Contains("XAU"))
            {
                return new BreakoutProfile
                {
                    BreakBuffer = 0.15,
                    MinBreakCandle = 0.9
                };
            }

            if (symbol.Contains("US100") || symbol.Contains("NAS") || symbol.Contains("SPX"))
            {
                return new BreakoutProfile
                {
                    BreakBuffer = 0.12,
                    MinBreakCandle = 0.8
                };
            }

            if (symbol.Contains("USD") &&
                (symbol.Contains("ETH") || symbol.Contains("BTC") ||
                 symbol.Contains("SOL") || symbol.Contains("AVAX") ||
                 symbol.Contains("BNB") || symbol.Contains("XRP")))
            {
                return new BreakoutProfile
                {
                    BreakBuffer = 0.25,   // crypto needs stronger break
                    MinBreakCandle = 1.1
                };
            }

            // FX default
            return new BreakoutProfile
            {
                BreakBuffer = 0.10,
                MinBreakCandle = 0.7
            };
        }

        public double[] CalculateEMA(MqlRates[] candles, int period)
        {
            int length = candles.Length;
            double[] ema = new double[length];
            double multiplier = 2.0 / (period + 1);

            // Initialize the first EMA with the simple average of the first N closes
            double sum = 0;
            for (int i = 0; i < period; i++)
                sum += candles[i].close;

            ema[period - 1] = sum / period;

            // Compute EMA for the rest of the candles
            for (int i = period; i < length; i++)
            {
                double close = candles[i].close;
                ema[i] = (close - ema[i - 1]) * multiplier + ema[i - 1];
            }

            return ema;
        }
        static private void TightenStopLossAtr(TrackedTrade t, double mid, double atr)
        {
            double target = atr * 1.0;
            double newSL;

            if (t.Side == TradeSide.Buy)
            {
                newSL = mid - target;
                if (newSL > t.StopLoss)
                {
                    t.StopLoss = newSL;
                    ModifyTrade(t);
                }
            }
            else
            {
                newSL = mid + target;
                if (newSL < t.StopLoss)
                {
                    t.StopLoss = newSL;
                    ModifyTrade(t);
                }
            }
        }
        public async Task OpenTrade(TradeSide side, double price, LotSizingMode sizingMode, EntryModel entry, MqlRates[] rates1H, TradeDecision tradeDecision, TransitionResult transition)
        {

            // compute stop loss and take profit in absolute price units
            decimal slPrice = side == TradeSide.Buy ? (decimal)price - (decimal)tradingPairsDto.stoplosspoints * tradingPairsDto.pipvalue : (decimal)price + (decimal)StopLossPoints * tradingPairsDto.pipvalue;
            decimal tpPrice = side == TradeSide.Buy ? (decimal)price + (decimal)tradingPairsDto.takeprofitpoints * tradingPairsDto.pipvalue : (decimal)price - tradingPairsDto.takeprofitpoints * tradingPairsDto.pipvalue;

            double point = _client.SymbolInfoDouble(SYMBOL, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_POINT);
            double atr = CalculateATR(rates1H, 14);

            var sltp = SlTpProfileEngine.GetSlTp(SYMBOL, atr, point);

            int stopLossPoints = sltp.StopLossPoints;
            int takeProfitPoints = sltp.TakeProfitPoints;

            var (sl, tp) = SlTpPriceBuilder.BuildPrices(
                side,
                price,
                sltp
            );

            int digits = (int)_client.SymbolInfoInteger(SYMBOL, ENUM_SYMBOL_INFO_INTEGER.SYMBOL_DIGITS);

            sl = Math.Round(sl, digits);
            tp = Math.Round(tp, digits);

            // dynamic lot sizing: RiskPerTradeUsd / (SL distance in USD per standard lot)
            double slDistancePrice = Math.Abs(price - (double)slPrice); // in USD
            double tpDistancePrice = Math.Abs((double)tpPrice - price); // in USD

            if (slDistancePrice <= 0)
            {
                Console.WriteLine("Invalid stop-loss distance. Aborting order.");
                LogFile.Write("Invalid stop-loss distance. Aborting order.");
                return;
            }

            decimal rawVolume = (decimal)tradingPairsDto.riskpertrade / ((decimal)slDistancePrice * tradingPairsDto.contractsize);
            double baseLot = Math.Max((double)0.01m, Math.Round((double)rawVolume, 2));
            double finalLot;

            if (sizingMode == LotSizingMode.AtrBased)
            {
                finalLot = baseLot; // locked
            }
            else
            {
                finalLot = fixedLotSize; // capped
            }

            // FTMO / safety cap
            finalLot = Math.Min(finalLot, maxAllowedLot);
            finalLot = Math.Max(finalLot, 0.01);
            finalLot = Math.Round(finalLot, 2, MidpointRounding.ToZero);
            if (finalLot <= 0)
            {
                Console.WriteLine("Calculated volume <= 0, aborting.");
                LogFile.Write("Calculated volume <= 0, aborting.");
                return;
            }

            ENUM_ORDER_TYPE_FILLING filling = ENUM_ORDER_TYPE_FILLING.ORDER_FILLING_IOC;

            double equity = _client.AccountInfoDouble(ENUM_ACCOUNT_INFO_DOUBLE.ACCOUNT_EQUITY);

            var decision = DynamicLotSizingHelper.Calculate(
                SYMBOL,
                equity,
                price,
                (double)sl,
                ctx.AtrPercentile,
                tradeDecision.Momentum > 4.0
            );

            decision.Lot =
              Math.Max(
                  0.01,
                  Math.Floor(
                      ((double)decision.Lot * 0.7) * 100
                  ) / 100);

            var request = new MqlTradeRequest
            {
                Action = ENUM_TRADE_REQUEST_ACTIONS.TRADE_ACTION_DEAL,
                Symbol = SYMBOL,
                Volume = (double)decision.Lot,
                Type = side == TradeSide.Buy ? ENUM_ORDER_TYPE.ORDER_TYPE_BUY : ENUM_ORDER_TYPE.ORDER_TYPE_SELL,
                Price = price,
                Sl = (double)sl,
                Tp = (double)tp,
                Deviation = 100,
                Type_filling = filling,
                Magic = (ulong)MAGIC_NUMBER,
                Comment = "EMA closed-candle"
            };

            LogFile.Write($"Pre Order values. Side={side}, Volume={finalLot}, Entry={price}, SL={sl}, TP={tp}");
            Console.WriteLine($"Pre Order values. Side={side}, Volume={finalLot}, Entry={price}, SL={sl}, TP={tp}");


            if (_client.OrderSend(request, out MqlTradeResult result) && result != null)
            {
                TradeServiceEngine.closingTrade = false;
                ticket = result.Order;
                await streamMessage<string>("string", "output", $"Order placed. Side={side}, Ticket={ticket}, Volume={finalLot}, Entry={price}, SL={sl}, TP={tp}");
                Console.WriteLine($"Order placed. Side={side}, Ticket={ticket}, Volume={finalLot}, Entry={price}, SL={sl}, TP={tp}");
                LogFile.Write($"Order placed. Side={side}, Ticket={ticket}, Volume={finalLot}, Entry={price}, SL={sl}, TP={tp}");
                // track it locally
                var trade = new Trade
                {
                    Ticket = ticket,
                    Symbol = SYMBOL,
                    Side = side.ToString(),
                    OpenTime = DateTime.UtcNow,
                    ContractSize = tradingPairsDto.contractsize,
                    EntryPrice = price,
                    Volume = (double)decision.Lot,
                    StopLoss = (double)sl,
                    IsClosed = false,
                    TakeProfit = (double)tp,
                    PeakOrTrough = price, // initialize peak/trough to entry price
                    Model = entry,
                    IsProven = false
                };

                var tracked = new TrackedTrade
                {
                    Ticket = ticket,
                    Symbol = SYMBOL,
                    Side = side,
                    OpenTime = DateTime.UtcNow,
                    ContractSize = tradingPairsDto.contractsize,
                    InitialRiskUsd = (double)(slDistancePrice * (double)decision.Lot * tradingPairsDto.contractsize),
                    MaxProfitUsd = 0,
                    ProfitUsd = 0,
                    EntryPrice = price,
                    Volume = (double)decision.Lot,
                    StopLoss = (double)sl,
                    IsClosed = false,
                    TakeProfit = (double)tp,
                    PeakOrTrough = price, // initialize peak/trough to entry price
                    Model = entry,
                    EntryCandleTime = rates1H[^1].time,
                    CandlesSinceEntry = 0,

                    IsProven = false
                };
                openTrades.Add(tracked);

                TradingEventBus.ActiveTrade(trade);
            }
            else
            {
                await streamMessage<string>("string", "output", "OrderSend failed or returned null result.");
                Console.WriteLine("OrderSend failed or returned null result.");
                LogFile.Write("OrderSend failed or returned null result.");
                await _notificationService.sendNotification($"OrderSend failed or returned null result. {result}", _configuration.GetValue<string>("CHAT_ID"), _configuration.GetValue<string>("BOT_TOKEN"));

            }

        }


        static DateTime lastH1CandleTime = DateTime.MinValue;
        private static DateTime _lastWeeklyPrintDate = DateTime.MinValue;
        public (TradeDecision, bool) CheckMultiTfMomentum(
            SymbolContext ctx,
            double ask, double bid,
            double[] emaFast1H, double[] emaSlow1H,
            double[] emaFast4H, double[] emaSlow4H,
            MqlRates[] rates1H,
            MtApi5Client _client,
            out MqlRates lastout, out MqlRates prevout,
            bool? isBuy = null
            )
        {

            // ----------------------------------------------------------
            // WEEKLY EXPECTANCY REPORT (run once per day at 23:00 UTC)
            // ----------------------------------------------------------
            if (DateTime.UtcNow.Hour == 23 &&
                _lastWeeklyPrintDate.Date != DateTime.UtcNow.Date)
            {
                WeeklyExpectancyTracker.PrintWeeklyStats();
                _lastWeeklyPrintDate = DateTime.UtcNow.Date;
            }

            // ----------------------------------------------------------
            //  Candle references (H1)
            // ----------------------------------------------------------
            var last = rates1H[^1];
            lastout = last;
            var prev = rates1H[^2];
            prevout = prev;
            var prev2 = rates1H.Length > 3 ? rates1H[^3] : prev;

            // ----------------------------------------------------------
            // EMA values & slopes (H1)
            // ----------------------------------------------------------
            double ema9 = emaFast1H[^1];
            double ema21 = emaSlow1H[^1];

            double slope9 = emaFast1H[^1] - emaFast1H[^2];
            double slope21 = emaSlow1H[^1] - emaSlow1H[^2];

            double lastBody = Math.Abs(last.close - last.open);
            double prevBody = Math.Abs(prev.close - prev.open);

            double upperWick = last.high - Math.Max(last.close, last.open);
            double lowerWick = Math.Min(last.close, last.open) - last.low;

            double atrRaw = CalculateATR(rates1H, 14);
            // ----------------------------------------------------------
            // MOMENTUM H1 + H4
            // ----------------------------------------------------------
            int h1_i1 = emaFast1H.Length - 1;
            int h1_i0 = emaFast1H.Length - 2;

            int h4_i1 = emaFast4H.Length - 1;
            int h4_i0 = emaFast4H.Length - 2;

            double h1Momentum = emaFast1H[h1_i1] - emaSlow1H[h1_i1];
            double h1PrevMomentum = emaFast1H[h1_i0] - emaSlow1H[h1_i0];
            double h1Change = h1Momentum - h1PrevMomentum;

            double h4Momentum = emaFast4H[h4_i1] - emaSlow4H[h4_i1];
            double h4PrevMomentum = emaFast4H[h4_i0] - emaSlow4H[h4_i0];
            double h4Change = h4Momentum - h4PrevMomentum;

            bool h4Bull = h4Momentum > 0;
            bool h4Bear = h4Momentum < 0;

            // ----------------------------------------------------------
            // ⭐ FINAL UPDATED H4 WEAK LOGIC (REPLACES YOUR OLD VERSION)
            // ----------------------------------------------------------
            bool h4NoTrend = Math.Abs(h4Momentum) < atrRaw * 0.20;

            bool h4Reversal =
                (h4Bull && h4Momentum < 0) ||
                (h4Bear && h4Momentum > 0);

            bool h4SlopeCollapse = Math.Abs(h4Change) < atrRaw * 0.03;

            bool h4Weak = h4NoTrend || h4Reversal || h4SlopeCollapse;

            // ----------------------------------------------------------
            //  ATR, mid price, EMA
            // ----------------------------------------------------------
            double midPrice = (bid + ask) / 2.0;

            double atrPoints = atrRaw * 10.0;

            var bullishOB = SMCEngine.DetectBullishOB(rates1H, atrRaw);
            var bearishOB = SMCEngine.DetectBearishOB(rates1H, atrRaw);

            var fvgUp = SMCEngine.DetectBullishFVG(rates1H);
            var fvgDown = SMCEngine.DetectBearishFVG(rates1H);

            var structure = SMCEngine.DetectStructure(rates1H);

            var momentum = HybridMomentumEngine.Calculate(last, prev, rates1H, emaFast1H, emaSlow1H, atrRaw, false);
            // ----------------------------------------------------------
            // TREND MOMENTUM COLLAPSE GUARD
            // Prevent late continuation entries
            // ----------------------------------------------------------
            double slopeNow = emaFast1H[^1] - emaFast1H[^2];
            double slopePrev = emaFast1H[^2] - emaFast1H[^3];

            if (Math.Abs(slopeNow) < Math.Abs(slopePrev) * 0.5)
            {
                TradingEventBus.Decision(new DecisionEvent
                {
                    Symbol = SYMBOL,
                    Action = TradeAction.Hold.ToString(),
                    Category = DecisionCategory.Momentum.ToString(),
                    Reason = "EMA9 slope collapsing",
                    Message = "Trend momentum weakening",
                    Momentum = momentum.Hybrid,
                    EarlyMomentum = false,
                    EmaCompressed = false,
                    Regime = MarketRegime.Exhaustion.ToString(),
                    Time = DateTime.UtcNow
                });


                TradingEventBus.State(new BotStateUpdate
                {
                    state = ctx.State.ToString(),
                    Symbol = SYMBOL,
                    EmaCompressed = false,
                    Time = DateTime.UtcNow,
                    Momentum = momentum.Hybrid,
                    Reason = $"momentum weakening",
                    Action = TradeAction.WeakMom.ToString()
                });

                return (new TradeDecision
                {
                    Action = TradeAction.Hold,
                    LotMultiplier = 1.0,
                    H4Weak = h4Weak,
                    H4Momentum = momentum.Hybrid,
                    H4Reversal = h4Reversal

                }, false);
            }

            // ----------------------------------------------------------
            // EMA COMPRESSION DETECTION (H1)
            // ----------------------------------------------------------
            bool emaCompressed = MarketStateEngine.IsEmaCompressed(
                ema9,
                ema21,
                slope9,
                slope21,
                atrRaw
            );



            if (last.time != lastH1CandleTime)
            {
                lastH1CandleTime = last.time;

                EntryDecisionEngine.ResetEntryLock();

                Console.WriteLine("🕯 New H1 candle → entry lock reset");
                LogFile.Write("🕯 New H1 candle → entry lock reset");
            }


            TradeDecision tradeDecision = new TradeDecision
            {
                Action = TradeAction.Hold,
                LotMultiplier = 1.0,
                H4Weak = h4Weak,
                H4Momentum = momentum.Hybrid,
                H4Reversal = h4Reversal

            };

            if (!momentum.Strong)
            {

                Console.WriteLine($"❌ Hybrid momentum weak → {momentum.Hybrid:F2}");
                LogFile.Write($"❌ Hybrid momentum weak → {momentum.Hybrid:F2}");

                TradingEventBus.State(new BotStateUpdate
                {
                    state = ctx.State.ToString(),
                    Symbol = SYMBOL,
                    EmaCompressed = emaCompressed,
                    Time = DateTime.UtcNow,
                    Momentum = momentum.Hybrid,
                    Reason = $"Hybrid momentum weak",
                    Action = TradeAction.Hold.ToString()
                });

                TradingEventBus.Decision(new DecisionEvent
                {
                    Symbol = SYMBOL,
                    Action = TradeAction.Hold.ToString(),
                    Category = DecisionCategory.Momentum.ToString(),
                    Reason = $"Hybrid momentum weak",
                    Message = $"Hybrid momentum weak",
                    Momentum = momentum.Hybrid,
                    EarlyMomentum = false,
                    EmaCompressed = false,
                    Regime = DecisionCategory.Hold.ToString(),
                    Time = DateTime.UtcNow
                });

                //return (tradeDecision, emaCompressed);

            }

            // Breakout override
            if (momentum.Breakout)
            {
                Console.WriteLine("🔥 Momentum breakout override → allow entries");
                LogFile.Write("🔥 Momentum breakout override → allow entries");

                TradingEventBus.State(new BotStateUpdate
                {
                    state = ctx.State.ToString(),
                    Symbol = SYMBOL,
                    EmaCompressed = emaCompressed,
                    Time = DateTime.UtcNow,
                    Momentum = momentum.Hybrid,
                    Reason = $"🔥 Momentum breakout override → allow entries → {TradeAction.MomentumBreakout.ToString():F2}",
                    Action = TradeAction.MomentumBreakout.ToString()
                });
            }

            // Impulse override
            if (momentum.Impulse)
            {
                TradingEventBus.State(new BotStateUpdate
                {
                    state = ctx.State.ToString(),
                    Symbol = SYMBOL,
                    EmaCompressed = emaCompressed,
                    Time = DateTime.UtcNow,
                    Momentum = momentum.Hybrid,
                    Reason = $"Impulse momentum → strong signal - {TradeAction.StrongSignal.ToString()}",
                    Action = TradeAction.StrongSignal.ToString()
                });
                Console.WriteLine("🚀 Impulse momentum → strong signal");
                LogFile.Write("🚀 Impulse momentum → strong signal");
            }
            // ----------------------------------------------------------
            // Update cooldown (only decrements on new H1 candle)
            // ----------------------------------------------------------
            SmartCooldownEngine.UpdateCooldown(rates1H);

            var trend = TrendEngine.Analyze(
                emaFast1H,
                emaSlow1H,
                rates1H,
                atrRaw);

            bool h1Bull = trend.Direction == TrendDirection.Bullish;
            bool h1Bear = trend.Direction == TrendDirection.Bearish;

            bool bullishBreakout = trend.BullishBreakout;
            bool bearishBreakout = trend.BearishBreakout;

            bool impulseCandle = trend.IsImpulse;
            bool isChoppyTrend = trend.IsChoppy;

            double emaDistancePoints = Math.Abs(midPrice - ema9) * 10.0;

            // ----------------------------------------------------------
            // GLOBAL BLOCKERS
            // ----------------------------------------------------------
            if (prevBody > atrPoints * 0.50)
                return (tradeDecision, emaCompressed);

            if (lastBody > atrPoints * 0.65)
                return (tradeDecision, emaCompressed);

            if (DynamicWickRejection.IsBullishExhaustion(last, prev, atrRaw))
            {
                TradingEventBus.State(new BotStateUpdate
                {
                    state = ctx.State.ToString(),
                    Symbol = SYMBOL,
                    EmaCompressed = emaCompressed,
                    Time = DateTime.UtcNow,
                    Momentum = momentum.Hybrid,
                    Reason = $"🔥 Institutional Bullish Exhaustion detected",
                    Action = TradeAction.DynamicBullishExhaustion.ToString()
                });

                TradingEventBus.Decision(new DecisionEvent
                {
                    Symbol = SYMBOL,
                    Action = TradeAction.Standby.ToString(),
                    Category = DecisionCategory.Exhaustion.ToString(),
                    Reason = $"🔥 Inst. Bullish Exhaustion detectedn",
                    Message = $"🔥 Inst. Bullish Exhaustion detected",
                    Momentum = momentum.Hybrid,
                    EarlyMomentum = false,
                    EmaCompressed = false,
                    Regime = MarketRegime.Exhaustion.ToString(),
                    Time = DateTime.UtcNow
                });

                tradeDecision.Action =
                      TradeAction.DynamicBullishExhaustion;

                tradeDecision.LotMultiplier = 0.65;
            }


            if (DynamicWickRejection.IsBearishExhaustion(last, prev, atrRaw))
            {

                TradingEventBus.State(new BotStateUpdate
                {
                    state = ctx.State.ToString(),
                    Symbol = SYMBOL,
                    EmaCompressed = emaCompressed,
                    Time = DateTime.UtcNow,
                    Momentum = momentum.Hybrid,
                    Reason = $"🔥 Inst. Bearish Exhaustion detected",
                    Action = TradeAction.DynamicBearishExhaustion.ToString()
                });

                TradingEventBus.Decision(new DecisionEvent
                {
                    Symbol = SYMBOL,
                    Action = TradeAction.Standby.ToString(),
                    Category = DecisionCategory.Exhaustion.ToString(),
                    Reason = $"🔥 Inst. Bearish Exhaustion detected",
                    Message = $"🔥 Inst. Bearish Exhaustion detected",
                    Momentum = momentum.Hybrid,
                    EarlyMomentum = false,
                    EmaCompressed = false,
                    Regime = MarketRegime.Exhaustion.ToString(),
                    Time = DateTime.UtcNow
                });

                tradeDecision.Action =
                       TradeAction.DynamicBearishExhaustion;

                tradeDecision.LotMultiplier = 0.65;
            }

            tradeDecision = new TradeDecision
            {
                Action = TradeAction.CloseTrade,
                LotMultiplier = 1.0,
                H4Weak = h4Weak,
                H4Momentum = momentum.Hybrid,
                H4Reversal = h4Reversal
            };

            if (isChoppyTrend)
            {


                TradingEventBus.State(new BotStateUpdate
                {
                    state = ctx.State.ToString(),
                    Symbol = SYMBOL,
                    EmaCompressed = emaCompressed,
                    Time = DateTime.UtcNow,
                    Momentum = momentum.Hybrid,
                    Reason = "Market is Choppy",
                    Action = TradeAction.Choppy.ToString()
                });
                Console.WriteLine($"Market is Choppy");
                LogFile.Write($"Market is Choppy");

                TradingEventBus.Decision(new DecisionEvent
                {
                    Symbol = SYMBOL,
                    Action = TradeAction.Hold.ToString(),
                    Category = DecisionCategory.Momentum.ToString(),
                    Reason = $"Market is Choppy",
                    Message = $"Market is Choppy",
                    Momentum = momentum.Hybrid,
                    EarlyMomentum = false,
                    EmaCompressed = false,
                    Regime = DecisionCategory.Momentum.ToString(),
                    Time = DateTime.UtcNow
                });

                return (tradeDecision, emaCompressed);
            }

            if (IsNewCandleClosed(rates1H))
            {
                if (cooldownRemaining > 0)
                    cooldownRemaining--;
            }



            // Impulse override (only if not reversing)
            if (impulseCandle && !h4Reversal)
            {
                h4Weak = false;
                TradingEventBus.State(new BotStateUpdate
                {
                    state = ctx.State.ToString(),
                    Symbol = SYMBOL,
                    EmaCompressed = emaCompressed,
                    Time = DateTime.UtcNow,
                    Momentum = momentum.Hybrid,
                    Reason = $"Impulse",
                    Action = TradeAction.ImpulseOverride.ToString()
                });


                Console.WriteLine($"Impulse override");
                LogFile.Write($"Impulse override");
            }

            //-----------------------------------------------------------------
            // 1️⃣ EXIT LOGIC ALWAYS FIRST
            //-----------------------------------------------------------------
            if (isBuy != null)  // trade is open
            {
                TradeAction exitDecision = ExitDecisionEngine.CheckExit(
                    isBuy.Value,
                    rates1H,
                    rates1H[^1],      // last
                    atrRaw,
                    h4Weak,
                    bullishBreakout,
                    bearishBreakout
                );


                if (exitDecision == TradeAction.CloseTrade)
                {
                    Console.WriteLine("EXIT → Closing trade");
                    LogFile.Write("EXIT → Closing trade");

                    TradingEventBus.State(new BotStateUpdate
                    {
                        state = ctx.State.ToString(),
                        Symbol = SYMBOL,
                        EmaCompressed = emaCompressed,
                        Time = DateTime.UtcNow,
                        Momentum = momentum.Hybrid,
                        Reason = $"EXIT → Closing trade",
                        Action = TradeAction.CloseTrade.ToString()
                    });

                    // START 2-CANDLE COOLDOWN AFTER LOSS
                    SmartCooldownEngine.StartCooldown(2);

                    TradingEventBus.Decision(new DecisionEvent
                    {
                        Symbol = SYMBOL,
                        Action = TradeAction.CloseTrade.ToString(),
                        Category = DecisionCategory.Exhaustion.ToString(),
                        Reason = $"EXIT → Closing trade",
                        Message = $"EXIT → Closing trade",
                        Momentum = momentum.Hybrid,
                        EarlyMomentum = false,
                        EmaCompressed = false,
                        Regime = DecisionCategory.Exhaustion.ToString(),
                        Time = DateTime.UtcNow
                    });

                    return (tradeDecision, emaCompressed);
                }
            }


            tradeDecision = new TradeDecision
            {
                Action = TradeAction.Hold,
                LotMultiplier = 1.0,
                H4Weak = h4Weak,
                H4Momentum = momentum.Hybrid,
                H4Reversal = h4Reversal
            };

            if (emaCompressed && !momentum.Strong)
            {
                TradingEventBus.State(new BotStateUpdate
                {
                    state = ctx.State.ToString(),
                    Symbol = SYMBOL,
                    EmaCompressed = emaCompressed,
                    Time = DateTime.UtcNow,
                    Momentum = momentum.Hybrid,
                    Reason = $"Entry blocked: EMA compression + weak momentum",
                    Action = TradeAction.WeakMom.ToString()
                });

                Console.WriteLine("⛔ Entry blocked: EMA compression + weak momentum");
                LogFile.Write("⛔ Entry blocked: EMA compression + weak momentum");

                TradingEventBus.Decision(new DecisionEvent
                {
                    Symbol = SYMBOL,
                    Action = TradeAction.Hold.ToString(),
                    Category = DecisionCategory.Compression.ToString(),
                    Reason = $"Entry blocked: EMA compr. + weak momentum",
                    Message = $"Entry blocked: EMA compr. + weak momentum",
                    Momentum = momentum.Hybrid,
                    EarlyMomentum = false,
                    EmaCompressed = false,
                    Regime = DecisionCategory.Compression.ToString(),
                    Time = DateTime.UtcNow
                });

                return (tradeDecision, emaCompressed);
            }

            double lotMultiplier = 1.0;

            // Strong momentum
            if (momentum.Hybrid >= 1.20)
                lotMultiplier = 1.10;

            // Impulse + breakout
            if (momentum.Impulse && trend.BullishBreakout)
                lotMultiplier = 1.20;

            // Extreme (rare)
            if (momentum.Hybrid >= 1.50 && momentum.Impulse && trend.BullishBreakout)
                lotMultiplier = 1.30;

            // HARD CAP
            lotMultiplier = Math.Min(lotMultiplier, 1.30);


            TradingEventBus.Decision(new DecisionEvent
            {
                Symbol = SYMBOL,
                Action = TradeAction.Standby.ToString(),
                Category = DecisionCategory.Neutral.ToString(),
                Reason = "Momentum valid but no actionable entry signal",
                Message = $"Standby → Momentum={momentum.Hybrid:F2}, H4Weak={h4Weak}",
                Momentum = momentum.Hybrid,
                EarlyMomentum = false,
                EmaCompressed = emaCompressed,
                Regime = MarketRegime.Observation.ToString(),
                Time = DateTime.UtcNow
            });

            TradingEventBus.State(new BotStateUpdate
            {
                state = ctx.State.ToString(),
                Symbol = SYMBOL,
                EmaCompressed = emaCompressed,
                Time = DateTime.UtcNow,
                Momentum = momentum.Hybrid,
                Reason = DecisionCategory.None.ToString(),
                Action = DecisionCategory.None.ToString()
            });

            return (new TradeDecision
            {
                Action = TradeAction.Standby, // <-- neutral
                LotMultiplier = lotMultiplier,
                H4Weak = h4Weak,
                H4Momentum = momentum.Hybrid,
                H4Reversal = h4Reversal,
                Momentum = momentum.Hybrid,
            }, emaCompressed);



        }
        bool IsNewCandleClosed(MqlRates[] rates)
        {
            if (rates == null || rates.Length < 2)
                return false;

            // The last CLOSED candle (not the currently forming one)
            DateTime lastClosedTime = rates[rates.Length - 2].time;

            // Compare with previously stored candle time
            if (lastClosedTime != _lastCandleTime)
            {
                _lastCandleTime = lastClosedTime;
                return true; // ✅ A new candle just closed
            }

            return false; // ❌ No new candle closed yet
        }

        private static DateTime GetCurrentClosedCandleTime(MqlRates[] rates)
        {
            if (rates == null || rates.Length < 2)
                return DateTime.MinValue;

            // If you pass shift=1 series into engines, then rates[^1] is already closed.
            // If you pass shift=0 series, then rates[^1] is live and rates[^2] is last closed.
            // Choose ONE convention and stick to it.
            return rates[^1].time; // assumes shift=0 series (live included)
        }

        public enum EntryModel
        {
            Sniper,
            CompressionBreakoutContinuation,
            Continuation,
            BreakoutContinuation,
            TrendRide,
            Pullback,
            Breakout,
            Velocity,
            Nothing,
            BreakRetest,
            Scout,
            TrendRideContinuation,
            PullbackContinuation,
            ExhaustionReversal,
            CascadeBreakdown,
            Cascade,
            Explosion,
            Unknown
        }

        private bool ShouldExitEarly(
            string symbol,
            MqlRates[] rates,
            double[] emaFast,
            double momentumCurrent,
            double momentumPrevious)
        {
            if (rates == null || rates.Length < 3 || emaFast == null || emaFast.Length < 2)
                return false;

            // Use CLOSED candle
            var lastClosed = rates[^2];

            double ema9 = emaFast[^2];

            // --------------------------------
            // 1. Price closes below EMA9
            // --------------------------------
            bool belowEma = lastClosed.close < ema9;

            // --------------------------------
            // 2. Momentum weakening
            // --------------------------------
            bool momentumWeakening = momentumCurrent < momentumPrevious;

            // --------------------------------
            // Final decision
            // --------------------------------
            if (belowEma && momentumWeakening)
            {
                LogFile.Write($"[{symbol}] ⚠️ Early exit: EMA9 break + momentum weakening");
                return true;
            }

            return false;
        }
        public EntryModel Model { get; set; }

        public DateTime EntryCandleTime { get; set; }

        public int CandlesSinceEntry { get; set; }

        static void ManageOpenTrades(double midPrice,
            double bid,
            double ask,
            TradingPairsDto tradingPairsDto,
            MqlRates[] rates1Hr, SymbolContext ctx, MomentumResult momentumResult, bool? isBuy = null)
        {
            bool isCrypto =
                SYMBOL.Contains("BTC") ||
                SYMBOL.Contains("ETH") ||
                SYMBOL.Contains("BNB") ||
                SYMBOL.Contains("SOL") ||
                SYMBOL.Contains("ADA") ||
                SYMBOL.Contains("XRP");
            // --------------------------------------------------------------
            // 🚫 FTMO NEWS BLACKOUT — NO TRADE MANAGEMENT
            // --------------------------------------------------------------
            if (NewsBlackoutEngine.IsBlackout(SYMBOL))
            {
                Console.WriteLine($"🚫 News blackout — trade management paused for {SYMBOL}");
                LogFile.Write($"🚫 News blackout — trade management paused for {SYMBOL}");
                return;
            }

            double atr = CalculateATR(rates1Hr, 14);
            double[] emaFast1H = CalculateEMAUpdate(rates1Hr, 9);
            double[] emaSlow1H = CalculateEMAUpdate(rates1Hr, 21);
            // Infer trend from EMA relationship
            bool isBullTrend = emaFast1H[^1] > emaSlow1H[^1];
            bool isBearTrend = emaFast1H[^1] < emaSlow1H[^1];

            // Compression state (same as entry logic)
            double ema9 = emaFast1H[^1];
            double ema21 = emaSlow1H[^1];

            double slope9 = emaFast1H[^1] - emaFast1H[^2];
            double slope21 = emaSlow1H[^1] - emaSlow1H[^2];

            bool emaCompressed = MarketStateEngine.IsEmaCompressed(
                ema9,
                ema21,
                slope9,
                slope21,
                atr
            );

            bool isFirstContinuation =
            MomentumTimingEngine.IsFirstValidContinuationCandle(
                rates1Hr,
                isBullTrend,
                isBearTrend,
                isCrypto
            );

            // 1. Get MT5 open positions
            bool manuallyAdded = false;
            List<long> mt5Tickets = GetAllOpenPositionTickets();
            LogFile.Write($"MT5 tickets: [{string.Join(",", mt5Tickets)}] | tracked: [{string.Join(",", openTrades.Select(x => x.Ticket))}]");



            long ticket = mt5Tickets.Select(s => s).SingleOrDefault();

            _client.PositionSelectByTicket((ulong)ticket);


            // -----------------------------------------
            // 🔥 1. EXPLOSION FAST EXIT (FIRST)
            // -----------------------------------------
            if (tradingEngine.Contains("Explosion"))
            {
                TrackedTrade trade = openTrades.FirstOrDefault(t => t.Ticket == (ulong)ticket);
                if (ExplosionEntryEngine.ShouldForceExit(
                        trade,
                        ctx.Symbol,
                        atr,
                        bid,
                        ask,
                        emaFast1H))
                {
                    TradeServiceEngine.closingTrade = true;
                    LogFile.Write($"💥 EXPLOSION EXIT TRIGGERED for trade {trade.Ticket}");
                    return;
                }
            }

            if (openTrades.Count == 0 && mt5Tickets.Count > 0)
            {

                var tr = new TrackedTrade()
                {
                    Ticket = (ulong)ticket, // ✅ IMPORTANT
                    Symbol = _client.PositionGetString(ENUM_POSITION_PROPERTY_STRING.POSITION_SYMBOL),
                    EntryPrice = _client.PositionGetDouble(ENUM_POSITION_PROPERTY_DOUBLE.POSITION_PRICE_OPEN),
                    StopLoss = _client.PositionGetDouble(ENUM_POSITION_PROPERTY_DOUBLE.POSITION_SL),
                    TakeProfit = _client.PositionGetDouble(ENUM_POSITION_PROPERTY_DOUBLE.POSITION_TP),
                    Volume = _client.PositionGetDouble(ENUM_POSITION_PROPERTY_DOUBLE.POSITION_VOLUME),

                    // ✅ set side so exit logic has truth
                    Side = _client.PositionGetInteger(ENUM_POSITION_PROPERTY_INTEGER.POSITION_TYPE) == (long)ENUM_POSITION_TYPE.POSITION_TYPE_BUY
                    ? TradeSide.Buy
                    : TradeSide.Sell,

                    // optional but recommended
                    Model = EntryModel.Unknown
                };


                manuallyAdded = true;
                openTrades.Add(tr);
            }

            foreach (var t in openTrades.ToList())
            {
                bool stopAtBreakEven = t.StopLoss >= t.EntryPrice;

                // --------------------------------------------------
                // EXIT LOGIC — only when trade is open
                // --------------------------------------------------
                if (isBuy != null)   // trade exists
                {

                    var velocityDecision = VelocityTradeGuard.Evaluate(
                    t,
                    isBuy == true,
                    bid,
                    ask,
                    emaFast1H,
                    emaSlow1H,
                    rates1Hr,
                    isFirstContinuation,
                    emaCompressed,
                    stopAtBreakEven, atr
                      );

                    if (velocityDecision.ShouldExit)
                    {
                        Console.WriteLine($"❌ Velocity exit → {velocityDecision.Reason}");
                        LogFile.Write($"❌ Velocity exit → {velocityDecision.Reason}");
                        TradeServiceEngine.closingTrade = true;
                        continue;
                    }

                }
            }

            // 2. Detect broker-side closures (SL/TP)
            foreach (var tracked in openTrades.ToList())
            {

                bool selected = _client.PositionSelectByTicket(tracked.Ticket);

                if (!selected)
                {
                    LogFile.Write($"[PPE ERROR] PositionSelectByTicket failed | ticket={tracked.Ticket}");
                    continue;
                }

                tracked.ProfitUsd =
                    _client.PositionGetDouble(ENUM_POSITION_PROPERTY_DOUBLE.POSITION_PROFIT);

                if (ProfitGivebackProtection.ShouldClose(tracked))
                {
                    LogFile.Write($"Trade {tracked.Ticket} CLOSED BY PROFIT GIVEBACK PROTECTION.");
                    TradeServiceEngine.closingTrade = true;
                    return;
                }

                if (!mt5Tickets.Contains((long)tracked.Ticket))
                {
                    Console.WriteLine($"⚠️ BROKER CLOSED TRADE {tracked.Ticket}");
                    Console.WriteLine($"Final Profit: {tracked.ProfitUsd}");
                    TradeServiceEngine.closingTrade = true;
                }

                if (!mt5Tickets.Contains((long)tracked.Ticket) && !manuallyAdded)
                {
                    Console.WriteLine($"Trade {tracked.Ticket} CLOSED BY BROKER (SL or TP hit).");
                    LogFile.Write($"Trade {tracked.Ticket} CLOSED BY BROKER (SL or TP hit).");
                    // Remove from your tracking list
                    TradeServiceEngine.closingTrade = true;
                    return;
                    // Optional: call your trade-close handler
                    // OnTradeClose(tracked);
                }
            }


            // Iterate over copy of list, because we may remove items

            foreach (var t in openTrades.ToList())
            {
                _client.CopyRates(SYMBOL, ENUM_TIMEFRAMES.PERIOD_H1, 1, 120, out MqlRates[] rates1H);
                _client.PositionSelectByTicket(t.Ticket);
                var profitUsd = _client.PositionGetDouble(ENUM_POSITION_PROPERTY_DOUBLE.POSITION_PROFIT);
                // --------------------------------------------------
                // 🛑 ATR SOFT STOP — EARLY LOSS CONTROL (NEW)
                // --------------------------------------------------
                if (AtrSoftStopGuard.IsEligible(t) &&
                    AtrSoftStopEngine.ShouldExit(t, bid, ask, atr))
                {
                    Console.WriteLine(
                        $"🛑 ATR SOFT STOP | {t.Symbol} | adverse ≥ {AtrSoftStopEngine.ATR_FACTOR} ATR"
                    );
                    LogFile.Write(
                        $"🛑 ATR SOFT STOP | {t.Symbol} | adverse ≥ {AtrSoftStopEngine.ATR_FACTOR} ATR"
                    );

                    TradeServiceEngine.closingTrade = true;
                    continue;
                }
                // --------------------------------------------------
                // 🛡 GAIN GIVE-BACK PROTECTION (NEW)
                // --------------------------------------------------
                if (GainProtectionExitEngine.ShouldExit(
                        t,
                        profitUsd,
                        _lastRealizedProfitUsd,
                        _lastRealizedSide,
                        _lastRealizedTime,
                        rates1H[^1].time))
                {
                    Console.WriteLine($"🛡 EXIT: Preventing give-back of previous gain ({_lastRealizedProfitUsd:F2})");
                    LogFile.Write($"🛡 EXIT: Preventing give-back of previous gain ({_lastRealizedProfitUsd:F2})");
                    TradeServiceEngine.closingTrade = true;
                    continue;
                }

                // --------------------------------------
                // 1. EARLY EXIT ENGINE (call HybridExit)
                // --------------------------------------

                double price = midPrice;
                double alphaFast = 2.0 / (9 + 1);      // 9 EMA
                double alphaSlow = 2.0 / (21 + 1);     // 21 EMA

                double emaFastLast = emaFast1H[^1];
                double emaSlowLast = emaSlow1H[^1];

                double emaFastLive = price * alphaFast + emaFastLast * (1 - alphaFast);
                double emaSlowLive = price * alphaSlow + emaSlowLast * (1 - alphaSlow);


                // FAIL FAST HERE (before HybridExit2)
                if (FailFastExitEngine.ShouldExit(t, ask, bid, emaFast1H, rates1H, rates1H[^1].time, atr))
                {
                    Console.WriteLine($"Closing trade {t.Ticket} by FAIL-FAST (momentum stalled).");
                    LogFile.Write($"Closing trade {t.Ticket} by FAIL-FAST (momentum stalled).");
                    TradeServiceEngine.closingTrade = true;

                    continue;
                }
                _client.PositionSelectByTicket(t.Ticket);
                profitUsd = _client.PositionGetDouble(ENUM_POSITION_PROPERTY_DOUBLE.POSITION_PROFIT);

                BreakEvenAndTightenEngine.Manage(
                SYMBOL,
                _client,
                (ulong)ticket,
                t.EntryPrice,
                isBuy == true ? bid : ask,
                t.StopLoss,
                profitUsd,
                isBuy == true
                    ? TradeAction.EnterBuy
                    : TradeAction.EnterSell
                );
                LogFile.Write($"BreakEvenAndTightenEngine evaluated for trade {t.Ticket}.");

                if (TradeManagementEngine.ShouldExit(
                    SYMBOL,
                    isBuy == true
                        ? TradeAction.EnterBuy
                        : TradeAction.EnterSell,
                    rates1H,
                    emaFast1H,
                    emaSlow1H,
                    ctx.CurrentMomentum,
                    ctx.PreviousMomentum, momentumResult))
                {
                    TradeServiceEngine.closingTrade = true;
                    LogFile.Write($"[{SYMBOL}] ❌ Trade closed by TradeManagementEngine");
                    return;
                }
                LogFile.Write($"[{SYMBOL}] TradeManagementEngine evaluated for trade {t.Ticket}.");

                if (!openTrades.Any(x => x.Ticket == t.Ticket))
                    continue; // trade closed by HybridExit

                AtrTrailingStopEngine.Apply(t, midPrice, atr, ModifyTrade);
                double floatingPnL = profitUsd;  // MT5-calculated PnL

                var currentCandleTime = rates1H[^1].time;

                // Only evaluate floating protection once per candle
                if (currentCandleTime == ctx.LastLossCheckCandleTime)
                    continue;

                ctx.LastLossCheckCandleTime = currentCandleTime;

                const double UnrealizedMaxDrawDown = 20;   // configurable

                if (floatingPnL < 0)
                {
                    double absLoss = Math.Abs(floatingPnL);

                    if (absLoss >= UnrealizedMaxDrawDown)
                    {
                        Console.WriteLine(
                            $"🛑 Closing trade {t.Ticket} due to floating DD. Loss={floatingPnL:F2}"
                        );

                        LogFile.Write(
                            $"🛑 Closing trade {t.Ticket} due to floating DD. Loss={floatingPnL:F2}"
                        );

                        TradeServiceEngine.closingTrade = true;

                        // Execute actual close here

                        continue;
                    }
                }



                // 2) trailing stop implemented in C# using peak/trough
                if (t.Side == TradeSide.Buy)
                {
                    // update peak
                    if (midPrice > t.PeakOrTrough) t.PeakOrTrough = midPrice;
                    // if price drops from peak by trailing distance -> close
                    double dropFromPeak = t.PeakOrTrough - midPrice;
                    double trailingPriceDiff = TrailingStopPoints * 0.1; // convert points->price (XAUUSD: 0.1)
                    if (dropFromPeak >= trailingPriceDiff)
                    {
                        Console.WriteLine($"Closing BUY {t.Ticket} by trailing stop (drop {dropFromPeak:F3}).");
                        LogFile.Write($"Closing BUY {t.Ticket} by trailing stop (drop {dropFromPeak:F3}).");
                        TradeServiceEngine.closingTrade = true;
                        continue;
                    }
                }
                else // Sell
                {
                    // update trough
                    if (midPrice < t.PeakOrTrough) t.PeakOrTrough = midPrice;
                    double riseFromTrough = midPrice - t.PeakOrTrough;
                    double trailingPriceDiff = TrailingStopPoints * 0.1;
                    if (riseFromTrough >= trailingPriceDiff)
                    {
                        Console.WriteLine($"Closing SELL {t.Ticket} by trailing stop (rise {riseFromTrough:F3}).");
                        LogFile.Write($"Closing SELL {t.Ticket} by trailing stop (rise {riseFromTrough:F3}).");
                        TradeServiceEngine.closingTrade = true;
                        continue;
                    }
                }
            }
        }
        // ================================
        // ✔ TRAILING STOP
        // ================================
        private static void ApplyTrailingStopAtr(TrackedTrade t, double mid, double atr, double bid, double ask)
        {

            // ---------------------------------------------
            // 🛑 TRAILING ACTIVATION GUARD (CRITICAL)
            // ---------------------------------------------
            double favorableMove =
                t.Side == TradeSide.Sell
                    ? (t.EntryPrice - mid)
                    : (mid - t.EntryPrice);

            // Do NOT trail until trade has moved meaningfully
            if (favorableMove < atr * 0.4)
                return;

            double trail = atr * 1.2;

            if (t.Side == TradeSide.Buy)
            {

                double newSL = mid - trail;
                LogFile.Write(
                 $"🔁 TRAILING SL UPDATE | ticket={t.Ticket} | oldSL={t.StopLoss} → newSL={newSL} | price={mid}"
                );
                if (newSL > t.StopLoss)
                {
                    t.StopLoss = newSL;
                    ModifyTrade(t);
                }
            }
            else
            {

                double newSL = mid + trail;
                LogFile.Write(
                 $"🔁 TRAILING SL UPDATE | ticket={t.Ticket} | oldSL={t.StopLoss} → newSL={newSL} | price={mid}"
                );
                if (newSL < t.StopLoss)
                {
                    t.StopLoss = newSL;
                    ModifyTrade(t);
                }
            }


        }
        public static List<long> GetAllOpenPositionTickets()
        {
            var tickets = new List<long>();
            int total = _client.PositionsTotal();

            for (int i = 0; i < total; i++)
            {
                ulong ticketU = _client.PositionGetTicket(i);
                long ticket = (long)ticketU;
                tickets.Add(ticket);
            }

            return tickets;
        }
        static double[] CalculateEMAUpdate(MqlRates[] candles, int period)
        {
            int length = candles.Length;
            double[] ema = new double[length];
            double multiplier = 2.0 / (period + 1);

            // Initialize the first EMA with the simple average of the first N closes
            double sum = 0;
            for (int i = 0; i < period; i++)
                sum += candles[i].close;

            ema[period - 1] = sum / period;

            // Compute EMA for the rest of the candles
            for (int i = period; i < length; i++)
            {
                double close = candles[i].close;
                ema[i] = (close - ema[i - 1]) * multiplier + ema[i - 1];
            }

            return ema;
        }
        private static void MoveStopLossToBreakEven(TrackedTrade t)
        {
            double entry = t.EntryPrice;

            // -----------------------------
            // Only tighten stops, never loosen
            // -----------------------------
            if (t.Side == TradeSide.Buy && entry <= t.StopLoss)
                return;

            if (t.Side == TradeSide.Sell && entry >= t.StopLoss)
                return;
            t.StopLoss = t.EntryPrice;
            ModifyTrade(t);
        }

        // ================================
        // ✔ TRADE MODIFICATION & DETECTION
        // ================================
        private static void ModifyTrade(TrackedTrade t)
        {
            var request = new MqlTradeRequest
            {
                Action = ENUM_TRADE_REQUEST_ACTIONS.TRADE_ACTION_SLTP,
                Symbol = t.Symbol,
                Sl = t.StopLoss,
                Position = (ulong)t.Ticket,
                Type_filling = ENUM_ORDER_TYPE_FILLING.ORDER_FILLING_IOC
            };

            if (_client.OrderSend(request, out MqlTradeResult result))
            {
                Console.WriteLine($"🔄 Trailing SL updated → {t.EntryPrice:F2}");
                LogFile.Write($"🔄 Trailing SL updated → {t.EntryPrice:F2}");
            }
            else
            {
                LogFile.Write($"❌ Failed to update SL. Error={result.Retcode}");
                Console.WriteLine($"❌ Failed to update SL. Error={result.Retcode}");
            }

        }
        public static double GetProfitUsd(TrackedTrade trade, double bid, double ask)
        {
            double priceDiff = trade.Side == TradeSide.Buy
                ? bid - trade.EntryPrice
                : trade.EntryPrice - ask;

            return priceDiff * trade.ContractSize * trade.Volume; //Lot Size = Volume
        }
        public static void CloseTrade(TradeSide side, double bid, double ask)
        {
            if (openTrades.Count > 0)
            {
                CloseManualOpenPositions();
            }


            double midPrice = (ask + bid) / 2;
            // With this, since TradeSide is a non-nullable enum:
            if (!Enum.IsDefined(typeof(TradeSide), side)) return;

            var toClose = openTrades.Where(t => t.Symbol == SYMBOL && t.Side == side).ToList();

            foreach (var t in toClose)
            {
                WeeklyExpectancyTracker.RecordTrade(
                    t.OpenTime,
                    DateTime.UtcNow,
                    t.Side,
                    GetProfitUsd(t, bid, ask)
                );
                Console.WriteLine($"Closing opposite {side} trade ticket {t.Ticket} before opening new signal.");
                LogFile.Write($"Closing opposite {side} trade ticket {t.Ticket} before opening new signal.");

                t.IsClosed = true;
                var trade = new Trade
                {
                    Ticket = t.Ticket,
                    Symbol = SYMBOL,
                    Side = t.Side.ToString(),
                    OpenTime = DateTime.UtcNow,
                    ContractSize = t.ContractSize,
                    EntryPrice = t.EntryPrice,
                    Volume = (double)t.Volume,
                    StopLoss = (double)t.StopLoss,
                    IsClosed = false,
                    TakeProfit = (double)t.TakeProfit,
                    PeakOrTrough = t.EntryPrice // initialize peak/trough to entry price
                };
                TradingEventBus.ActiveTrade(trade);

                CloseTradeByTicket(t, bid, ask);

                _client.CopyRates(SYMBOL, ENUM_TIMEFRAMES.PERIOD_H1, 1, 120, out MqlRates[] rates1H);

                _lastRealizedProfitUsd = t.ProfitUsd;
                _lastRealizedSide = t.Side;
                _lastRealizedTime = rates1H[^1].time;

                TradeAttemptLimiter.RegisterTradeClose(
                    t.Symbol,
                    t.Side
                );
            }
        }
        static void CloseTradeByTicket(TrackedTrade t, double bid, double ask)
        {

            // To close a position, send a DEAL with Position = ticket and Type = opposite side
            var closeType = t.Side == TradeSide.Buy ? ENUM_ORDER_TYPE.ORDER_TYPE_SELL : ENUM_ORDER_TYPE.ORDER_TYPE_BUY;
            double closePrice = closeType == ENUM_ORDER_TYPE.ORDER_TYPE_SELL ? bid : ask;
            ENUM_ORDER_TYPE_FILLING filling = ENUM_ORDER_TYPE_FILLING.ORDER_FILLING_IOC;
            var closeReq = new MqlTradeRequest
            {
                Action = ENUM_TRADE_REQUEST_ACTIONS.TRADE_ACTION_DEAL,
                Symbol = SYMBOL,
                Volume = t.Volume,
                Type = closeType,
                Price = closePrice,
                Deviation = 10,
                Magic = (ulong)MAGIC_NUMBER,
                Comment = "Close by bot",
                Type_filling = filling,
                // include Position to indicate which position to close (some MtApi5 variants support this)
                Position = t.Ticket
            };

            if (_client.OrderSend(closeReq, out MqlTradeResult closeResult) && closeResult != null)
            {
                TradeServiceEngine.closingTrade = true;
                Console.WriteLine($"CloseOrder sent for ticket {t.Ticket}, result order: {closeResult.Order}");
                LogFile.Write($"CloseOrder sent for ticket {t.Ticket}, result order: {closeResult.Order}");
            }
            else
            {
                Console.WriteLine($"Close attempt failed for ticket {t.Ticket}");
                LogFile.Write($"Close attempt failed for ticket {t.Ticket}");
            }

            // remove from local tracking regardless (we assume close will be processed)
            openTrades.RemoveAll(x => x.Ticket == t.Ticket);
        }
        private static void CloseManualOpenPositions()
        {
            int total = _client.PositionsTotal();
            Console.WriteLine($"Total open positions: {total}");
            LogFile.Write($"Total open positions: {total}");

            for (int i = 0; i < total; i++)
            {
                ulong ticket = _client.PositionGetTicket(i);
                if (ticket > 0)
                {
                    string symbol = _client.PositionGetString(ENUM_POSITION_PROPERTY_STRING.POSITION_SYMBOL);
                    double volume = _client.PositionGetDouble(ENUM_POSITION_PROPERTY_DOUBLE.POSITION_VOLUME);
                    double priceOpen = _client.PositionGetDouble(ENUM_POSITION_PROPERTY_DOUBLE.POSITION_PRICE_OPEN);
                    long magic = _client.PositionGetInteger(ENUM_POSITION_PROPERTY_INTEGER.POSITION_MAGIC);
                    long type = _client.PositionGetInteger(ENUM_POSITION_PROPERTY_INTEGER.POSITION_TYPE);

                    Console.WriteLine($"Ticket={ticket}, Symbol={symbol}, Volume={volume}, OpenPrice={priceOpen}, Magic={magic}");
                    LogFile.Write($"Ticket={ticket}, Symbol={symbol}, Volume={volume}, OpenPrice={priceOpen}, Magic={magic}");

                    Console.WriteLine($"⚠ Closing foreign trade Ticket={ticket}, Symbol={symbol}, Magic={magic}");
                    LogFile.Write($"Closing foreign trade Ticket={ticket}, Symbol={symbol}, Magic={magic}");
                    // Build trade request
                    ENUM_ORDER_TYPE_FILLING filling = ENUM_ORDER_TYPE_FILLING.ORDER_FILLING_IOC;
                    var request = new MqlTradeRequest
                    {
                        Action = ENUM_TRADE_REQUEST_ACTIONS.TRADE_ACTION_DEAL,
                        Symbol = symbol,
                        Volume = volume,
                        Type = type == (int)ENUM_POSITION_TYPE.POSITION_TYPE_BUY
                            ? ENUM_ORDER_TYPE.ORDER_TYPE_SELL
                            : ENUM_ORDER_TYPE.ORDER_TYPE_BUY,
                        Price = type == (int)ENUM_POSITION_TYPE.POSITION_TYPE_BUY
                            ? _client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID)
                            : _client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK),
                        Deviation = 10, // slippage
                        Type_filling = filling,
                        Position = ticket // link to the open position
                    };

                    // Send close request
                    if (_client.OrderSend(request, out MqlTradeResult result))
                    {
                        Console.WriteLine($"✅ Closed foreign trade #{ticket} ({symbol})");
                        LogFile.Write($"✅ Closed foreign trade #{ticket} ({symbol})");
                    }
                    else
                    {
                        LogFile.Write($"❌ Failed to close trade #{ticket}, Error={result.Retcode}");
                        Console.WriteLine($"❌ Failed to close trade #{ticket}, Error={result.Retcode}");
                    }
                }
            }
        }
        private static TradeAction HybridExit2(
    TrackedTrade trade,
    double[] emaFast,
    double[] emaSlow,
    MqlRates[] rates,
    double atr,
    double midPrice,
    double profitUsd,
    TradingPairsDto tradingPairsDto
)
        {

            if (emaFast == null || emaSlow == null || emaFast.Length < 3 || emaSlow.Length < 3)
                return TradeAction.Hold;

            int i1 = emaFast.Length - 1;   // last closed candle
            int iPrev = emaFast.Length - 2;

            // -----------------------------
            // 1️⃣ Momentum weakening detection
            // -----------------------------
            double h1Momentum = emaFast[i1] - emaSlow[i1];
            double h1PrevMomentum = emaFast[iPrev] - emaSlow[iPrev];

            double momentumChange = h1Momentum - h1PrevMomentum;

            bool buyWeakening =
                trade.Side == TradeSide.Buy &&
                (h1Momentum <= 0 || momentumChange < 0);

            bool sellWeakening =
                trade.Side == TradeSide.Sell &&
                (h1Momentum >= 0 || momentumChange > 0);

            if (buyWeakening || sellWeakening)
                trade.WeakCount++;
            else
                trade.WeakCount = 0;

            // 2 confirmations → exit
            if (trade.WeakCount >= 2)
            {
                LogFile.Write($"HybridExit2: Momentum weakening exit | weakCount={trade.WeakCount}");
                return TradeAction.CloseTrade;
            }

            // -----------------------------
            // 2️⃣ Break-even protection
            // -----------------------------
            bool stopBelowEntry = trade.StopLoss < trade.EntryPrice;
            bool stopAboveEntry = trade.StopLoss > trade.EntryPrice;
            LogFile.Write($"HybridExit2: Profit={profitUsd:F2}, BE Threshold={tradingPairsDto.profitusdbreakeven}, StopBelowEntry={stopBelowEntry}, StopAboveEntry={stopAboveEntry}");
            if (profitUsd > (double)tradingPairsDto.profitusdbreakeven)
            {
                if (trade.Side == TradeSide.Buy && stopBelowEntry)
                {
                    LogFile.Write($"HybridExit2: Move SL to BE (BUY)");
                    return TradeAction.MoveStopToBreakEven;
                }

                if (trade.Side == TradeSide.Sell && stopAboveEntry)
                {
                    LogFile.Write($"HybridExit2: Move SL to BE (SELL)");
                    return TradeAction.MoveStopToBreakEven;
                }
            }

            // -----------------------------
            // 3️⃣ ATR trailing tighten
            // -----------------------------
            LogFile.Write($"HybridExit2: Profit={profitUsd:F2}, Tighten Threshold={tradingPairsDto.profitusdtightenstop}");
            if (profitUsd > (double)tradingPairsDto.profitusdtightenstop)
            {
                double trailDistance = atr * 0.35;

                if (trade.Side == TradeSide.Buy)
                {
                    double newSL = midPrice - trailDistance;

                    if (newSL > trade.StopLoss)
                    {
                        LogFile.Write($"HybridExit2: Tighten stop BUY");
                        return TradeAction.TightenStop;
                    }
                }
                else
                {
                    double newSL = midPrice + trailDistance;

                    if (newSL < trade.StopLoss)
                    {
                        LogFile.Write($"HybridExit2: Tighten stop SELL");
                        return TradeAction.TightenStop;
                    }
                }
            }

            // -----------------------------
            // 4️⃣ Hard fail-safe
            // -----------------------------
            if (profitUsd <= (double)tradingPairsDto.lossusdclosetrade * -1)
            {
                LogFile.Write($"HybridExit2: Hard loss exit triggered");
                return TradeAction.CloseTrade;
            }

            return TradeAction.Hold;
        }
        private static bool IsNewCandleClosedUpdate(MqlRates[] rates)
        {
            var last = rates[^1];

            DateTime candleOpen = last.time;
            DateTime nextCandle = candleOpen.AddMinutes(60);

            return DateTime.UtcNow >= nextCandle;
        }

    }
}
