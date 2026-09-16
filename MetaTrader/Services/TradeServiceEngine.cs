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
using MetaTrader.Services.Helper.MetaTrader.Services.Helper;
using MetaTrader.Services.Interfaces;
using MetaTrader.Services.MetaTrader.Services;
using MetaTrader.Services.SMC.MetaTrader.Services;
using MetaTrader.Util;
using MetaTrader.Util.Trading.Execution;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using MtApi5;
using StackExchange.Redis;
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
        private static bool _fastOrderInProgress = false;
        private static DateTime _lastOrderCandleTime = DateTime.MinValue;
        private readonly TradeCoordinator _tradeCoordinator;
        private readonly TimeframeDiversityExecutor _tfExecutor;
        private readonly M15ConfirmationService _m15Service;
        private readonly PositionManager _positionManager;
        private readonly IServiceProvider _provider;
        // -------------------------

        // local tracking of open trades (because client's API cannot enumerate MT5 positions)
        static List<TrackedTrade> openTrades = new();

        // Protect local trade tracking from duplicate/concurrent
        // MT5 transaction callbacks.
        private static readonly object _openTradesSync = new object();

        // PositionSelectByTicket + PositionGet* share MT5's currently selected
        // position. Serialize those calls so BNBUSD and ETHUSD cannot
        // contaminate each other's position/risk-management state.
        private static readonly object _mt5PositionSync = new object();
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
            //"SOLUSD",
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
        private static IConnectionMultiplexer _redis;
        private static RedisProfitEngine _profitEngine;
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

            _redis = ConnectionMultiplexer.Connect("localhost:6379");
            _profitEngine = new RedisProfitEngine(_redis);
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

        public void CheckRecentlyClosedPositions()
        {
            lock (_openTradesSync)
            {
                foreach (var trade in openTrades.ToList())
                {
                    if (trade == null || trade.Ticket == 0)
                        continue;

                    bool exactTicketValid = false;
                    string exactTicketSymbol = null;

                    lock (_mt5PositionSync)
                    {
                        if (_client.PositionSelectByTicket(trade.Ticket))
                        {
                            exactTicketSymbol =
                                _client.PositionGetString(
                                    ENUM_POSITION_PROPERTY_STRING.POSITION_SYMBOL
                                );

                            exactTicketValid =
                                string.Equals(
                                    exactTicketSymbol,
                                    trade.Symbol,
                                    StringComparison.OrdinalIgnoreCase
                                );
                        }
                    }

                    // A valid tracked ticket is authoritative. Never overwrite it.
                    if (exactTicketValid)
                        continue;

                    // If the ticket is selectable but belongs to another symbol,
                    // do not silently turn a BNBUSD trade into ETHUSD or vice versa.
                    if (!string.IsNullOrWhiteSpace(exactTicketSymbol) &&
                        !string.Equals(
                            exactTicketSymbol,
                            trade.Symbol,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        LogFile.Write(
                            $"[{trade.Symbol}] 🚨 TRACKED TICKET SYMBOL MISMATCH | " +
                            $"ticket={trade.Ticket} | brokerSymbol={exactTicketSymbol} | " +
                            $"action=DO NOT REASSIGN"
                        );

                        continue;
                    }

                    // Only recover if the stored ticket is genuinely unavailable.
                    ulong recoveredTicket =
                        FindOpenPositionTicketBySymbol(trade.Symbol);

                    if (recoveredTicket > 0)
                    {
                        ulong oldTicket = trade.Ticket;
                        trade.Ticket = recoveredTicket;

                        LogFile.Write(
                            $"[{trade.Symbol}] 🔄 POSITION TICKET RECOVERED | " +
                            $"oldTicket={oldTicket} | newTicket={recoveredTicket}"
                        );

                        continue;
                    }

                    LogFile.Write(
                        $"[{trade.Symbol}] Position confirmed missing | " +
                        $"ticket={trade.Ticket} | checking close reason..."
                    );

                    CheckCloseReason(trade);
                    openTrades.Remove(trade);
                }
            }
        }

        private void CheckCloseReason(TrackedTrade trade)
        {
            LogFile.Write(
                $"[{trade.Symbol}] CLOSED | " +
                $"ticket={trade.Ticket} | " +
                $"OriginalSL={trade.StopLoss} | " +
                $"OriginalTP={trade.TakeProfit}");
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

        private static bool HasValidPullbackForSymbol(
        string symbol,
        MqlRates[] rates,
        double[] emaFast,
        double[] emaSlow,
        double atr,
        TradeAction tradeAction)
        {
            if (rates == null || rates.Length < 5 ||
                emaFast == null || emaSlow == null ||
                emaFast.Length < 5 || emaSlow.Length < 5 ||
                atr <= 0)
                return false;

            string s = symbol.ToUpperInvariant();

            var c0 = rates[^1];
            var c1 = rates[^2];

            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];

            double distToEma9 = Math.Abs(c0.close - ema9) / atr;
            double distToEma21 = Math.Abs(c0.close - ema21) / atr;

            bool touchedEma9 = c0.low <= ema9 && c0.high >= ema9;
            bool touchedEma21 = c0.low <= ema21 && c0.high >= ema21;

            double ema9Tolerance =
                IsGold(s) ? 0.35 :
                IsCrypto(s) ? 0.55 :
                IsIndex(s) ? 0.50 :
                IsFx(s) ? 0.40 :
                0.45;

            double ema21Tolerance =
                IsGold(s) ? 0.65 :
                IsCrypto(s) ? 0.90 :
                IsIndex(s) ? 0.85 :
                IsFx(s) ? 0.70 :
                0.75;

            bool nearEma =
                touchedEma9 ||
                touchedEma21 ||
                distToEma9 <= ema9Tolerance ||
                distToEma21 <= ema21Tolerance;

            bool pullbackDirection =
                tradeAction == TradeAction.EnterBuy
                    ? c0.close < c1.close   // buy trend: price pulled down
                    : c0.close > c1.close;  // sell trend: price pulled up

            return nearEma && pullbackDirection;
        }

        private static bool IsGold(string symbol)
        {
            return symbol.Contains("XAU") || symbol.Contains("XAG");
        }

        private static bool IsCrypto(string symbol)
        {
            return symbol.Contains("BTC") ||
                   symbol.Contains("ETH") ||
                   symbol.Contains("SOL") ||
                   symbol.Contains("BNB") ||
                   symbol.Contains("XRP") ||
                   symbol.Contains("ADA");
        }

        private static bool IsIndex(string symbol)
        {
            return symbol.Contains("US100") ||
                   symbol.Contains("NAS") ||
                   symbol.Contains("US30") ||
                   symbol.Contains("SPX");
        }

        private static bool IsFx(string symbol)
        {
            return !IsGold(symbol) && !IsCrypto(symbol) && !IsIndex(symbol);
        }
        private static bool IsDistanceFromEmaFavorable(
            MqlRates[] rates,
            double[] emaFast,
            double[] emaSlow,
            double atr,
            TradeAction tradeAction)
        {
            var c0 = rates[^1];

            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];

            double distToEma9 = Math.Abs(c0.close - ema9) / atr;
            double distToEma21 = Math.Abs(c0.close - ema21) / atr;

            if (tradeAction == TradeAction.EnterBuy)
            {
                // Buy: price should have pulled back toward the EMA from above.
                return c0.close >= ema9 &&
                       (distToEma9 <= 0.45 || distToEma21 <= 0.75);
            }
            else if (tradeAction == TradeAction.EnterSell)
            {
                // Sell: price should have pulled back toward the EMA from below.
                return c0.close <= ema9 &&
                       (distToEma9 <= 0.45 || distToEma21 <= 0.75);
            }
            return true;
        }


        private async Task generalCloseTrade(
     double bid,
     double ask,
     SymbolContext ctx)
        {
            if (ctx == null)
            {
                LogFile.Write(
                    "generalCloseTrade aborted - SymbolContext is null."
                );

                return;
            }


            // ========================================================
            // CAPTURE SIDE BEFORE currentSignal IS RESET
            // ========================================================

            TradeSide closingSide =
                currentSignal == "BUY"
                    ? TradeSide.Buy
                    : currentSignal == "SELL"
                        ? TradeSide.Sell
                        : TradeSide.NoTrade;


            string closingSignal =
                currentSignal;


            // ========================================================
            // RELEASE GLOBAL LOCK IF POSITION IS ALREADY GONE
            // ========================================================

            if (!HasAnyOpenPositionBySymbol(ctx.Symbol))
            {
                GlobalTradeManager.ReleaseTradeLock(
                    _provider
                );
            }


            barsSinceEntry = 0;


            await tradeProfit(
                ctx.Symbol
            );


            // ========================================================
            // REMOVE TREND TRACKING
            // ========================================================

            if (globalEntryModel ==
                EntryModel.TrendRideContinuation)
            {
                var tracked =
                    openTrades.FirstOrDefault();

                if (tracked != null)
                {
                    TradeTrackingService.RemoveTrade(
                        tracked.Ticket
                    );
                }
            }


            // ========================================================
            // ENGINE PERFORMANCE
            // ========================================================

            foreach (var engine in engineTrades)
            {
                int total =
                    engine.Value.wins +
                    engine.Value.losses;


                double winRate =
                    total == 0
                        ? 0
                        : (double)engine.Value.wins /
                          total *
                          100;


                LogFile.Write(
                    $"{engine.Key}: {winRate:F2}%"
                );
            }


            // ========================================================
            // RESET CONTEXT STRUCTURE
            // ========================================================

            ctx.PeakAtr = 0.0;
            ctx.PeakAtrCaptured = false;

            ctx.ResetPullback();
            ctx.ResetExpansion();


            SymbolPriorityEngine.Release(
                ctx.Symbol
            );


            // ========================================================
            // CAPTURE TRACKED TRADE SAFELY
            // ========================================================

            var trackedTrade =
                openTrades.FirstOrDefault();


            double closedProfit =
                _client.HistoryDealGetDouble(
                    ticket,
                    MtApi5.ENUM_DEAL_PROPERTY_DOUBLE.DEAL_PROFIT
                );


            var trade =
                new ClosedTrade
                {
                    CloseTime =
                        DateTime.UtcNow,

                    Profit =
                        closedProfit,

                    EntryPrice =
                        closingSide == TradeSide.Buy
                            ? ask
                            : bid,

                    StopLoss =
                        trackedTrade != null
                            ? trackedTrade.StopLoss
                            : 0.0,

                    Volume =
                        trackedTrade != null
                            ? trackedTrade.Volume
                            : 0.0
                };


            closedTrades.Add(
                trade
            );


            // ========================================================
            // IMPULSE RESULT STATE
            //
            // IMPORTANT:
            // Only a PROFITABLE impulse remains consumed.
            //
            // A losing impulse must NOT prevent a later valid impulse.
            // ========================================================

            if (ctx.IsImpulse)
            {
                if (trade.Profit > 0)
                {
                    ctx.ImpulseTradeWon =
                        true;


                    ctx.LastImpulseWinTime =
                        DateTime.UtcNow;


                    ctx.ImpulseConsumed =
                        true;


                    LogFile.Write(
                        $"[{ctx.Symbol}] Impulse trade WON | " +
                        $"profit={trade.Profit:F2} | " +
                        $"ImpulseConsumed=true"
                    );
                }
                else
                {
                    ctx.ImpulseTradeWon =
                        false;


                    ctx.ImpulseConsumed =
                        false;


                    LogFile.Write(
                        $"[{ctx.Symbol}] Impulse trade LOST | " +
                        $"profit={trade.Profit:F2} | " +
                        $"ImpulseConsumed=false"
                    );
                }
            }


            // ========================================================
            // RESET ENGINE STATE
            // ========================================================

            tradingEngine = "";

            tradeBegun = false;

            _orderInProgress = false;

            _fastOrderInProgress = false;

            Diff = 0;

            maxBalance = 0;

            TradeServiceEngine.closingTrade =
                false;


            // ========================================================
            // CLOSE USING THE SIDE CAPTURED BEFORE RESET
            // ========================================================

            if (closingSide != TradeSide.NoTrade)
            {
                CloseTrade(
                    closingSide,
                    bid,
                    ask
                );
            }


            LogFile.Write(
                $"Closing Trade | " +
                $"symbol={ctx.Symbol} | " +
                $"side={closingSignal} | " +
                $"profit={trade.Profit:F2}"
            );


            Console.WriteLine(
                $"Closing Trade {closingSignal}"
            );


            // NOW it is safe to clear currentSignal
            currentSignal = null;


            ResetTrade = true;


            startingEquity =
                await getStartEquity();


            await streamMessage<double>(
                "double",
                "startingEquity",
                startingEquity
            );


            await streamMessage<double>(
                "double",
                "currentEquity",
                startingEquity
            );


            startingEquity = 0;


            openTrades.Clear();


            // ========================================================
            // UI / EVENT BUS
            // ========================================================

            TradingEventBus.Decision(
                new DecisionEvent
                {
                    Symbol =
                        ctx.Symbol,

                    Action =
                        "Trade Closed",

                    Category =
                        DecisionCategory.None.ToString(),

                    Reason =
                        "Closed Trade",

                    Message =
                        "Closed Trade",

                    Momentum =
                        0,

                    EarlyMomentum =
                        false,

                    EmaCompressed =
                        false,

                    Regime =
                        DecisionCategory.None.ToString(),

                    Time =
                        DateTime.UtcNow
                }
            );


            TradingEventBus.M15SnapshotUpdate(
                new M15Snapshot
                {
                    Message =
                        "M15 not Confirmed",

                    Symbol =
                        ctx.Symbol
                }
            );
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

                Console.WriteLine("Error: " + ex.Message + " " + ex.StackTrace);
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

        private async Task tradeProfit(string symbol)
        {
            double profit = _client.HistoryDealGetDouble(ticket, ENUM_DEAL_PROPERTY_DOUBLE.DEAL_PROFIT);

            await _profitEngine.AddClosedProfit(profit);
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
        private static EntryModel globalEntryModel = EntryModel.Unknown;
        private static readonly Dictionary<string, DateTime> _entryLockCandle = new();
        private static readonly HashSet<string> _orderInProgressSet = new();

        private bool isGoodImpulseLocation(
            SymbolContext ctx,
            TradeAction tradeAction,
            MqlRates[] rates1H)
        {
            if (ctx == null ||
                rates1H == null ||
                rates1H.Length < 22)
            {
                return false;
            }


            var emaFast =
                CalculateEMA(
                    rates1H,
                    9
                );


            var emaSlow =
                CalculateEMA(
                    rates1H,
                    21
                );


            double atr =
                CalculateATR(
                    rates1H,
                    14
                );


            if (atr <= 0 ||
                emaFast == null ||
                emaFast.Length == 0 ||
                emaSlow == null ||
                emaSlow.Length == 0)
            {
                return false;
            }


            // ========================================================
            // CURRENT LOCATION
            // ========================================================

            double currentPrice =
                tradeAction == TradeAction.EnterBuy
                    ? rates1H[^1].close
                    : rates1H[^1].close;


            double currentEma9 =
                emaFast[^1];


            double emaDistanceAtr =
                Math.Abs(
                    currentPrice -
                    currentEma9
                ) / atr;



            // ========================================================
            // IMPULSE ALREADY CONSUMED
            //
            // Do not permanently block a symbol just because an older
            // impulse was consumed.
            //
            // Allow the impulse state to reset when:
            //
            // 1. A proper pullback occurred
            //
            // OR
            //
            // 2. Price has returned to a reasonable EMA9 location,
            //    meaning this is no longer the same exhausted extension.
            // ========================================================

            if (ctx.ImpulseConsumed)
            {
                bool validPullback =
                    HasValidPullbackForSymbol(
                        ctx.Symbol,
                        rates1H,       // FIXED: was "rates"
                        emaFast,
                        emaSlow,
                        atr,
                        tradeAction
                    );


                // ----------------------------------------------------
                // Fresh-location reset
                //
                // Around <= 1.10 ATR still gives Gold enough room for
                // a legitimate continuation impulse without allowing
                // highly extended chasing.
                // ----------------------------------------------------

                double freshResetDistanceAtr =
                   GetImpulseResetDistance(ctx.Symbol);

                bool freshLocation =
                  emaDistanceAtr <= freshResetDistanceAtr;

                if (validPullback ||
                    freshLocation)
                {
                    ctx.ImpulseConsumed =
                        false;


                    LogFile.Write(
                        $"[{ctx.Symbol}] Impulse reset | " +
                        $"validPullback={validPullback} | " +
                        $"freshLocation={freshLocation} | " +
                        $"emaDistATR={emaDistanceAtr:F2}"
                    );
                }
                else
                {
                    LogFile.Write(
                        $"[{ctx.Symbol}] Impulse blocked - previous impulse consumed | " +
                        $"validPullback={validPullback} | " +
                        $"emaDistATR={emaDistanceAtr:F2} | " +
                        $"reason=price still extended"
                    );


                    return false;
                }
            }


            return true;
        }

        private double GetImpulseResetDistance(string symbol)
        {
            string s =
                symbol?.ToUpperInvariant() ?? "";


            if (s.Contains("XAU") ||
                s.Contains("GOLD"))
            {
                return 1.10;
            }


            if (s.Contains("BTC") ||
                s.Contains("ETH"))
            {
                return 1.25;
            }


            if (s.Contains("SOL") ||
                s.Contains("BNB") ||
                s.Contains("XRP") ||
                s.Contains("ADA"))
            {
                return 1.20;
            }


            if (s.Contains("US100") ||
                s.Contains("NAS") ||
                s.Contains("USTEC"))
            {
                return 1.15;
            }


            // FX
            return 1.00;
        }

        private bool ShouldAvoidChoppyMarket(
    SymbolContext ctx,
    MqlRates[] rates,
    double[] emaFast,
    double[] emaSlow,
    double atr)
        {
            if (ctx == null ||
                rates == null ||
                rates.Length < 6 ||
                emaFast == null ||
                emaFast.Length < 2 ||
                emaSlow == null ||
                emaSlow.Length < 2 ||
                atr <= 0)
            {
                return true;
            }


            var c0 = rates[^1];
            var c1 = rates[^2];
            var c2 = rates[^3];
            var c3 = rates[^4];


            double ema9 = emaFast[^1];
            double ema9Prev = emaFast[^2];

            double ema21 = emaSlow[^1];
            double ema21Prev = emaSlow[^2];


            // ========================================================
            // EMA / PRICE STRUCTURE
            // ========================================================

            double emaSpreadAtr =
                Math.Abs(ema9 - ema21) / atr;


            double priceToEma9Atr =
                Math.Abs(c0.close - ema9) / atr;


            double priceToEma21Atr =
                Math.Abs(c0.close - ema21) / atr;


            bool compressedEmas =
                emaSpreadAtr < 0.25;


            bool priceNearEma9 =
                priceToEma9Atr < 0.45;


            bool priceNearEma21 =
                priceToEma21Atr < 0.60;


            bool compressionState =
                ctx.State == MarketState.Compression;


            // ========================================================
            // CANDLE CHOP / WHIPSAW
            // ========================================================

            bool alternatingCandles =
                (c0.close > c0.open && c1.close < c1.open) ||
                (c0.close < c0.open && c1.close > c1.open);


            int directionChanges = 0;


            MqlRates[] recent =
            {
        c0,
        c1,
        c2,
        c3
    };


            for (int i = 0; i < recent.Length - 1; i++)
            {
                bool currentBull =
                    recent[i].close >
                    recent[i].open;


                bool previousBull =
                    recent[i + 1].close >
                    recent[i + 1].open;


                if (currentBull != previousBull)
                {
                    directionChanges++;
                }
            }


            bool whipsaw =
                directionChanges >= 2;


            // ========================================================
            // CURRENT CANDLE STRENGTH
            // ========================================================

            double body =
                Math.Abs(
                    c0.close -
                    c0.open
                );


            double bodyAtr =
                body / atr;


            // ========================================================
            // DIRECTIONAL EMA STRUCTURE
            // ========================================================

            bool ema9Rising =
                ema9 > ema9Prev;


            bool ema9Falling =
                ema9 < ema9Prev;


            bool ema21Rising =
                ema21 >= ema21Prev;


            bool ema21Falling =
                ema21 <= ema21Prev;


            bool bullishAlignment =
                ema9 > ema21;


            bool bearishAlignment =
                ema9 < ema21;


            // ========================================================
            // COMPRESSION ESCAPE
            //
            // IMPORTANT:
            //
            // A stale Compression state must NOT block a genuine
            // directional move.
            //
            // Require:
            //
            // BUY:
            // - bullish candle
            // - meaningful body
            // - price above EMA9
            // - EMA9 above EMA21
            // - EMA9 rising
            //
            // SELL:
            // - bearish candle
            // - meaningful body
            // - price below EMA9
            // - EMA9 below EMA21
            // - EMA9 falling
            // ========================================================

            bool bullishEscape =
                c0.close > c0.open &&
                bodyAtr >= 0.60 &&
                c0.close > ema9 &&
                bullishAlignment &&
                ema9Rising;


            bool bearishEscape =
                c0.close < c0.open &&
                bodyAtr >= 0.60 &&
                c0.close < ema9 &&
                bearishAlignment &&
                ema9Falling;


            // ========================================================
            // STRONGER TREND ESCAPE
            //
            // This catches a market which may not currently have
            // one huge candle but has clearly transitioned out of chop.
            // ========================================================

            bool bullishTrendEscape =
                bullishAlignment &&
                ema9Rising &&
                ema21Rising &&
                c0.close > ema9 &&
                c1.close > ema9 &&
                emaSpreadAtr >= 0.20;


            bool bearishTrendEscape =
                bearishAlignment &&
                ema9Falling &&
                ema21Falling &&
                c0.close < ema9 &&
                c1.close < ema9 &&
                emaSpreadAtr >= 0.20;


            // ========================================================
            // IF MARKET HAS ESCAPED, DO NOT BLOCK
            // ========================================================

            if (bullishEscape ||
                bearishEscape ||
                bullishTrendEscape ||
                bearishTrendEscape)
            {
                LogFile.Write(
                    $"[{ctx.Symbol}] ✅ CHOP FILTER ESCAPE | " +
                    $"bullEscape={bullishEscape} | " +
                    $"bearEscape={bearishEscape} | " +
                    $"bullTrendEscape={bullishTrendEscape} | " +
                    $"bearTrendEscape={bearishTrendEscape} | " +
                    $"bodyATR={bodyAtr:F2} | " +
                    $"emaSpreadATR={emaSpreadAtr:F2} | " +
                    $"priceToEMA9ATR={priceToEma9Atr:F2} | " +
                    $"state={ctx.State}"
                );

                return false;
            }


            // ========================================================
            // TRUE CHOP
            //
            // Do NOT block merely because ctx.State says Compression.
            //
            // Require actual current evidence of chop as well.
            // ========================================================

            bool closeToBothEmas =
                priceNearEma9 &&
                priceNearEma21;


            bool actualChop =
                compressedEmas ||
                whipsaw ||
                (
                    alternatingCandles &&
                    closeToBothEmas
                );


            bool shouldBlock =
                compressionState &&
                actualChop;


            // ========================================================
            // DIAGNOSTIC
            // ========================================================

            if (shouldBlock)
            {
                LogFile.Write(
                    $"[{ctx.Symbol}] ⛔ CHOP FILTER BLOCK | " +
                    $"state={ctx.State} | " +
                    $"compressedEmas={compressedEmas} | " +
                    $"emaSpreadATR={emaSpreadAtr:F2} | " +
                    $"whipsaw={whipsaw} | " +
                    $"directionChanges={directionChanges} | " +
                    $"alternating={alternatingCandles} | " +
                    $"priceNearEMA9={priceNearEma9} | " +
                    $"priceNearEMA21={priceNearEma21} | " +
                    $"bodyATR={bodyAtr:F2}"
                );
            }


            return shouldBlock;
        }
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

            if (trackedTrade != null)
            {
                bool trackedTicketValid = false;
                string trackedBrokerSymbol = null;

                lock (_mt5PositionSync)
                {
                    if (_client.PositionSelectByTicket(trackedTrade.Ticket))
                    {
                        trackedBrokerSymbol =
                            _client.PositionGetString(
                                ENUM_POSITION_PROPERTY_STRING.POSITION_SYMBOL
                            );

                        trackedTicketValid =
                            string.Equals(
                                trackedBrokerSymbol,
                                trackedTrade.Symbol,
                                StringComparison.OrdinalIgnoreCase
                            );
                    }
                }

                if (trackedTicketValid)
                {
                    LogFile.Write(
                        $"[{trackedTrade.Symbol}] ✅ TRACKED POSITION VALID | " +
                        $"ticket={trackedTrade.Ticket}"
                    );
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(trackedBrokerSymbol) &&
                        !string.Equals(
                            trackedBrokerSymbol,
                            trackedTrade.Symbol,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        LogFile.Write(
                            $"[{trackedTrade.Symbol}] 🚨 TRACKED TICKET SYMBOL MISMATCH | " +
                            $"ticket={trackedTrade.Ticket} | brokerSymbol={trackedBrokerSymbol} | " +
                            $"action=SKIP MANAGEMENT"
                        );

                        return;
                    }

                    ulong recoveredTicket =
                        FindOpenPositionTicketBySymbol(trackedTrade.Symbol);

                    if (recoveredTicket > 0)
                    {
                        ulong oldTicket = trackedTrade.Ticket;
                        trackedTrade.Ticket = recoveredTicket;

                        LogFile.Write(
                            $"[{trackedTrade.Symbol}] 🔄 POSITION TICKET RECOVERED | " +
                            $"oldTicket={oldTicket} | newTicket={recoveredTicket}"
                        );
                    }
                    else
                    {
                        LogFile.Write(
                            $"[{trackedTrade.Symbol}] 🔓 Broker position no longer exists | " +
                            $"trackedTicket={trackedTrade.Ticket} | cleaning stale local tracking"
                        );

                        lock (_openTradesSync)
                        {
                            openTrades.Remove(trackedTrade);
                        }

                        GlobalTradeManager.ReleaseTradeLock(_provider);

                        currentSignal = null;
                        tradeBegun = false;
                        _orderInProgress = false;
                        _fastOrderInProgress = false;
                        ResetTrade = true;

                        return;
                    }
                }
            }

            if (trackedTrade != null)
            {
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
                    await generalCloseTrade(bid, ask, ctx);
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
                    try
                    {
                        LogFile.Write("DEBUG: Before generalCloseTrade");

                        await generalCloseTrade(bid, ask, ctx);

                        LogFile.Write("DEBUG: After generalCloseTrade");
                    }
                    catch (Exception ex)
                    {
                        LogFile.Write($"❌ generalCloseTrade crash: {ex}");
                    }

                    PerTradeEquityLossGuard.Reset(trackedTrade.Ticket);
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
                await generalCloseTrade(bid, ask, ctx);
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

                double liveOpen = rates1H[^1].close;

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

                ctx.PreviousMomentum = ctx.CurrentMomentum;
                ctx.CurrentMomentum = momentum.Hybrid;

                if (globalEntryModel == EntryModel.TrendRideContinuation)
                {
                    lock (_mt5PositionSync)
                    {
                        if (_client.PositionSelectByTicket(trackedTrade.Ticket))
                        {
                            string profitSymbol =
                                _client.PositionGetString(
                                    ENUM_POSITION_PROPERTY_STRING.POSITION_SYMBOL
                                );

                            if (string.Equals(
                                    profitSymbol,
                                    trackedTrade.Symbol,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                trackedTrade.ProfitUsd =
                                    _client.PositionGetDouble(
                                        ENUM_POSITION_PROPERTY_DOUBLE.POSITION_PROFIT
                                    );
                            }
                        }
                    }

                    TradeTrackingService.UpdateProfit(
                        trackedTrade.Ticket,
                        trackedTrade.ProfitUsd
                    );

                    double currentPrice = trackerTradeRates1H[^1].close;
                    var (shouldExit, reason) =
                        TradeTrackingService.EvaluateStallExit(
                            trackedTrade.Ticket,
                            SYMBOL,
                            trackedTrade.ProfitUsd,
                            momentum.Hybrid,
                            trackedTrade.EntryPrice,
                            currentPrice,
                            atrRaw,
                            rates1H
                        );

                    if (shouldExit)
                    {
                        LogFile.Write($"{reason}");
                        await generalCloseTrade(bid, ask, ctx);
                    }
                }

                bid = _client.SymbolInfoDouble(ctx.Symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);
                ask = _client.SymbolInfoDouble(ctx.Symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);
                double mid = (bid + ask) / 2.0;
                await ManageOpenTrades(
                    mid,
                    bid,
                    ask,
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

                if (string.IsNullOrWhiteSpace(SYMBOL))
                    return;

                // =========================================================
                // ✅ RELOAD ALL MARKET DATA FOR THE NEWLY SELECTED SYMBOL
                //
                // IMPORTANT:
                // RunParallelStrategy may begin with one Gold symbol
                // (for example XAUUSD) and the coordinator can then select
                // another (for example XAUEUR).
                //
                // Never reuse the old symbol's H1/H4 rates, EMA values,
                // ATR, or candle time with the newly selected symbol's
                // BID/ASK. Reload everything immediately after selection.
                // =========================================================
                _client.CopyRates(
                    SYMBOL,
                    ENUM_TIMEFRAMES.PERIOD_H1,
                    1,
                    BarsToFetch,
                    out rates1H
                );

                _client.CopyRates(
                    SYMBOL,
                    ENUM_TIMEFRAMES.PERIOD_H4,
                    1,
                    BarsToFetch,
                    out rates4H
                );

                if (rates1H == null ||
                    rates1H.Length < 22 ||
                    rates4H == null ||
                    rates4H.Length < 22)
                {
                    LogFile.Write(
                        $"[{SYMBOL}] ❌ SYMBOL DATA RELOAD FAILED | " +
                        $"H1Bars={(rates1H == null ? 0 : rates1H.Length)} | " +
                        $"H4Bars={(rates4H == null ? 0 : rates4H.Length)}"
                    );

                    return;
                }

                emaFast1H =
                    CalculateEMA(
                        rates1H,
                        9
                    );

                emaSlow1H =
                    CalculateEMA(
                        rates1H,
                        21
                    );

                emaFast4H =
                    CalculateEMA(
                        rates4H,
                        9
                    );

                emaSlow4H =
                    CalculateEMA(
                        rates4H,
                        21
                    );

                atrRaw =
                    CalculateATR(
                        rates1H,
                        14
                    );

                if (atrRaw <= 0 ||
                    emaFast1H == null ||
                    emaFast1H.Length == 0 ||
                    emaSlow1H == null ||
                    emaSlow1H.Length == 0 ||
                    emaFast4H == null ||
                    emaFast4H.Length == 0 ||
                    emaSlow4H == null ||
                    emaSlow4H.Length == 0)
                {
                    LogFile.Write(
                        $"[{SYMBOL}] ❌ SYMBOL INDICATOR RELOAD FAILED"
                    );

                    return;
                }

                bid =
                    _client.SymbolInfoDouble(
                        SYMBOL,
                        ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID
                    );

                ask =
                    _client.SymbolInfoDouble(
                        SYMBOL,
                        ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK
                    );

                currentCandleTime =
                    rates1H[^1].time;

                // Sanity guard. If the selected symbol's live price is wildly
                // inconsistent with its own EMA9, stop the cycle rather than
                // risk trading with mixed/corrupt market data.
                double selectedMid =
                    (bid + ask) / 2.0;

                double selectedDistanceAtr =
                    atrRaw > 0
                        ? Math.Abs(
                            selectedMid -
                            emaFast1H[^1]
                          ) / atrRaw
                        : double.MaxValue;

                if (selectedDistanceAtr > 20.0)
                {
                    LogFile.Write(
                        $"[{SYMBOL}] 🚨 SYMBOL DATA SANITY BLOCK | " +
                        $"mid={selectedMid:F5} | " +
                        $"ema9={emaFast1H[^1]:F5} | " +
                        $"ATR={atrRaw:F5} | " +
                        $"distanceATR={selectedDistanceAtr:F2}"
                    );

                    return;
                }

                LogFile.Write(
                    $"[{SYMBOL}] ✅ SYMBOL DATA RELOADED | " +
                    $"close={rates1H[^1].close:F5} | " +
                    $"ema9={emaFast1H[^1]:F5} | " +
                    $"ema21={emaSlow1H[^1]:F5} | " +
                    $"ATR={atrRaw:F5} | " +
                    $"bid={bid:F5} | " +
                    $"ask={ask:F5} | " +
                    $"distanceATR={selectedDistanceAtr:F2}"
                );

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

                // ==========================================
                // ⚡ Early Trade Detection
                // ==========================================

                var last = rates1H[^1];
                var prev = rates1H[^2];

                // Current live price
                double livePrice = (bid + ask) / 2.0;

                // Because rates1H contains CLOSED candles,
                // the most recent closed candle's close becomes
                // the synthetic open of the currently forming H1 candle.
                //
                // This is also consistent with the live-candle convention
                // being used by FastEntry.
                double liveOpen = last.close;

                // ----------------------------------------------------------
                // 🔥 LIVE HYBRID MOMENTUM
                //
                // IMPORTANT:
                // "last" is passed as prevClosed because it is the candle
                // immediately preceding the synthetic live candle.
                // ----------------------------------------------------------
                var earlyMomentum = HybridMomentumEngine.CalculateLive(
                    liveOpen,
                    livePrice,
                    last,
                    rates1H,
                    emaFast1H,
                    emaSlow1H,
                    atrRaw,
                    false
                );


                LogFile.Write(
                    $"[{SYMBOL}] 📡 LIVE HYBRID INPUT | " +
                    $"liveOpen={liveOpen:F5} | " +
                    $"livePrice={livePrice:F5} | " +
                    $"bid={bid:F5} | ask={ask:F5} | " +
                    $"hybrid={earlyMomentum.Hybrid:F2} | " +
                    $"pm={earlyMomentum.PriceMomentum:F2} | " +
                    $"rising={earlyMomentum.Rising} | " +
                    $"falling={earlyMomentum.Falling} | " +
                    $"strong={earlyMomentum.Strong} | " +
                    $"breakout={earlyMomentum.Breakout} | " +
                    $"impulse={earlyMomentum.Impulse}"
                );

                if (ShouldAvoidChoppyMarket(
                ctx,
                rates1H,
                emaFast1H,
                emaSlow1H,
                atrRaw))
                {
                    LogFile.Write(
                        $"[{SYMBOL}] ⛔ ALL ENTRIES BLOCKED | " +
                        $"reason=Compression / choppy market"
                    );

                    return;
                }

                var (fastAction, fastModel) = FastEntryEngine.Evaluate(
                    SYMBOL,
                    rates1H,
                    atrRaw,
                    emaFast1H,
                    emaSlow1H,
                    earlyMomentum.Hybrid, // ✅ FIXED
                    bid,
                    ask, _m15Service, out var tradeType, out var isImpulse
                );

                if (fastAction == TradeAction.EnterBuy || fastAction == TradeAction.EnterSell)
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
                            fastAction == TradeAction.EnterBuy ? true : false,
                            fastAction == TradeAction.EnterSell ? true : false);

                    // 🚫 BLOCK SAME CANDLE TRADES
                    if (_lastOrderCandleTime == currentCandleTime)
                    {
                        LogFile.Write($"⛔ Skipped: already traded this candle {currentCandleTime}");
                        return;
                    }
                    LogFile.Write($"⚡ FAST ENGINE: {fastAction} detected on {SYMBOL} with model {fastModel} IN progress ${_fastOrderInProgress}");
                    //if (!_fastOrderInProgress)
                    //{
                    //    _fastOrderInProgress = true;

                    //    LogFile.Write($"⚡ FAST ENGINE TRIGGERED: {fastAction} {SYMBOL}");

                    //    if (!GlobalTradeManager.TryAcquireTradeLock(_provider))
                    //    {
                    //        LogFile.Write("❌ FAST ENGINE failed to acquire lock");
                    //        return;
                    //    }

                    //    var fastTradeDecision = new TradeDecision
                    //    {
                    //        Momentum = earlyMomentum.Hybrid,
                    //        // set only what OpenTrade actually uses
                    //    };


                    //    _client.CopyRates(SYMBOL, ENUM_TIMEFRAMES.PERIOD_H1, 1, BarsToFetch, out var openTrade1H);

                    //    ctx.IsImpulse = isImpulse;

                    //    if (isImpulse) {

                    //       LogFile.Write($"⚡ FAST ENGINE: impulse detected for {isGoodImpulseLocation}");
                    //        if (isGoodImpulseLocation(ctx, fastAction, openTrade1H))
                    //        {
                    //            LogFile.Write($"⚡ FAST ENGINE: impulse location favorable for {fastAction} on {SYMBOL}");
                    //        }
                    //        else
                    //        {
                    //            LogFile.Write($"⚡ FAST ENGINE: impulse location NOT favorable for {fastAction} on {SYMBOL}");
                    //            GlobalTradeManager.ReleaseTradeLock(_provider);
                    //            _fastOrderInProgress = false;
                    //            return;
                    //        }
                    //    }

                    //    double dailyPnL = await _profitEngine.GetDailyProfit();
                    //    LogFile.Write($"⚡ FAST ENGINE: dailyPnL={dailyPnL:F2} for {SYMBOL}");
                    //    if (!HardRiskGuardEngine.CanEnter(dailyPnL))
                    //        return;

                    //    await OpenTrade(
                    //        fastAction == TradeAction.EnterBuy ? TradeSide.Buy : TradeSide.Sell,
                    //        fastAction == TradeAction.EnterBuy ? ask : bid,
                    //        LotSizingMode.Fixed,
                    //        fastModel,
                    //        openTrade1H,
                    //        fastTradeDecision,
                    //        transition, tradeType
                    //    );
                    //    if (isImpulse) ctx.ImpulseConsumed = true; // mark as consumed if impulse trade
                    //    _lastOrderCandleTime = currentCandleTime;

                    //    await _notificationService.sendNotification(
                    //            $"{fastAction.ToString()} - {fastModel.ToString()} — entry approved - {SYMBOL}",
                    //            _configuration["CHAT_ID"],
                    //            _configuration["BOT_TOKEN"]
                    //        );
                    //    return; // 🔥 STOP — do not continue to state engine
                    //}

                    if (!_fastOrderInProgress)
                    {
                        LogFile.Write($"⚡ FAST ENGINE TRIGGERED: {fastAction} {SYMBOL}");

                        if (!GlobalTradeManager.TryAcquireTradeLock(_provider))
                        {
                            LogFile.Write("❌ FAST ENGINE failed to acquire lock");
                            return;
                        }

                        var fastTradeDecision = new TradeDecision
                        {
                            Momentum = earlyMomentum.Hybrid,
                            // set only what OpenTrade actually uses
                        };

                        _client.CopyRates(SYMBOL, ENUM_TIMEFRAMES.PERIOD_H1, 1, BarsToFetch, out var openTrade1H);

                        ctx.IsImpulse = isImpulse;

                        if (isImpulse)
                        {
                            if (!isGoodImpulseLocation(ctx, fastAction, openTrade1H))
                            {
                                LogFile.Write($"⚡ FAST ENGINE: impulse location NOT favorable");
                                GlobalTradeManager.ReleaseTradeLock(_provider);
                                return; // 🚫 DO NOT touch _fastOrderInProgress
                            }
                        }



                        // 🔥 ONLY SET HERE
                        _fastOrderInProgress = true;

                        // --------------------------------------------------
                        // 🚫 LAST DEFENCE AGAINST DUPLICATE ENTRIES
                        // --------------------------------------------------
                        if (HasAnyOpenPositionBySymbol(SYMBOL))
                        {
                            LogFile.Write($"⛔ Trade already exists for {SYMBOL}");
                            _orderInProgress = false;
                            _fastOrderInProgress = false;
                            GlobalTradeManager.ReleaseTradeLock(_provider);
                            return;
                        }

                        double dailyPnL = await _profitEngine.GetDailyProfit();
                        //if (!HardRiskGuardEngine.CanEnter(dailyPnL))
                        //    return;

                        await OpenTrade(
                            fastAction == TradeAction.EnterBuy ? TradeSide.Buy : TradeSide.Sell,
                            fastAction == TradeAction.EnterBuy ? ask : bid,
                            LotSizingMode.Fixed,
                            fastModel,
                            openTrade1H,
                            fastTradeDecision,
                            transition, tradeType
                        );

                        // ------------------------------------
                        // Wait until MT5 confirms the position
                        // ------------------------------------
                        for (int i = 0; i < 20; i++)
                        {
                            if (HasAnyOpenPositionBySymbol(SYMBOL))
                                break;

                            await Task.Delay(100);
                        }

                        await _notificationService.sendNotification(
                                $"{fastAction.ToString()} - {fastModel.ToString()} — entry approved - {SYMBOL}",
                                _configuration["CHAT_ID"],
                                _configuration["BOT_TOKEN"]
                            );

                        if (isImpulse) ctx.ImpulseConsumed = true;

                        _lastOrderCandleTime = currentCandleTime;

                        return;
                    }
                }

                // ==========================================
                // ⚡ FAST ENTRY ENGINE (PRE-ARMED)
                // ==========================================
                if (!ctx.IsArmed)
                    return;
            }

            Console.WriteLine($"Selected symbol: {SYMBOL}");
            LogFile.Write($"Selected symbol: {SYMBOL}");

            // =========================================================
            // FAST-ENTRY-ONLY ARCHITECTURE
            //
            // FastEntryEngine above is the ONLY component allowed to
            // create a NEW position.
            //
            // The former secondary path:
            //
            //   CheckMultiTfMomentum(...)
            //       -> ExecutionLoop(...)
            //       -> legacy entry engines
            //       -> OpenTrade(...)
            //
            // has been removed.
            //
            // Existing open-position management, exits and Unified Risk
            // Management remain unchanged.
            // =========================================================

            LogFile.Write(
                $"[{SYMBOL}] FAST-ENTRY-ONLY | " +
                $"FastEntryEngine produced no executable trade this cycle"
            );

            return;
        }

        bool HasAnyOpenPositionBySymbol(string symbol)
        {
            return FindOpenPositionTicketBySymbol(symbol) > 0;
        }

        /// <summary>
        /// Returns the actual MT5 POSITION ticket for the open position
        /// on the requested symbol.
        ///
        /// IMPORTANT:
        /// MqlTradeResult.Order is an order ticket.  Risk management and
        /// PositionSelectByTicket(...) must use the live POSITION ticket.
        ///
        /// The bot currently allows only one trade at a time, so the first
        /// matching symbol owned by this EA is the position we want.
        /// </summary>
        private static ulong FindOpenPositionTicketBySymbol(string symbol)
        {
            if (_client == null ||
                string.IsNullOrWhiteSpace(symbol))
            {
                return 0;
            }

            lock (_mt5PositionSync)
            {
                int total =
                    _client.PositionsTotal();

                for (int i = 0; i < total; i++)
                {
                    ulong candidateTicket =
                        _client.PositionGetTicket(i);

                    if (candidateTicket == 0)
                        continue;

                    if (!_client.PositionSelectByTicket(candidateTicket))
                        continue;

                    string candidateSymbol =
                        _client.PositionGetString(
                            ENUM_POSITION_PROPERTY_STRING.POSITION_SYMBOL
                        );

                    long candidateMagic =
                        _client.PositionGetInteger(
                            ENUM_POSITION_PROPERTY_INTEGER.POSITION_MAGIC
                        );

                    if (!string.Equals(
                            candidateSymbol,
                            symbol,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (candidateMagic != MAGIC_NUMBER)
                        continue;

                    return candidateTicket;
                }
            }

            return 0;
        }


        // =============================================================
        // ROBUST POSITION SNAPSHOT RESOLVER
        //
        // result.Order is an ORDER ticket, not necessarily the live
        // POSITION ticket. Resolve the actual broker position by exact
        // symbol + magic number, then read the broker position data while
        // MT5 position access is serialized.
        // =============================================================
        private static bool TryResolveOpenPositionSnapshot(
            string symbol,
            out ulong positionTicket,
            out double entryPrice,
            out double volume,
            out double stopLoss,
            out double takeProfit)
        {
            positionTicket = 0;
            entryPrice = 0.0;
            volume = 0.0;
            stopLoss = 0.0;
            takeProfit = 0.0;

            if (_client == null ||
                string.IsNullOrWhiteSpace(symbol))
            {
                return false;
            }

            lock (_mt5PositionSync)
            {
                int total =
                    _client.PositionsTotal();

                for (int i = 0; i < total; i++)
                {
                    ulong candidateTicket =
                        _client.PositionGetTicket(i);

                    if (candidateTicket == 0)
                        continue;

                    if (!_client.PositionSelectByTicket(candidateTicket))
                        continue;

                    string symbolBefore =
                        _client.PositionGetString(
                            ENUM_POSITION_PROPERTY_STRING.POSITION_SYMBOL
                        );

                    long candidateMagic =
                        _client.PositionGetInteger(
                            ENUM_POSITION_PROPERTY_INTEGER.POSITION_MAGIC
                        );

                    if (!string.Equals(
                            symbolBefore,
                            symbol,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (candidateMagic != MAGIC_NUMBER)
                        continue;

                    double candidateEntry =
                        _client.PositionGetDouble(
                            ENUM_POSITION_PROPERTY_DOUBLE.POSITION_PRICE_OPEN
                        );

                    double candidateVolume =
                        _client.PositionGetDouble(
                            ENUM_POSITION_PROPERTY_DOUBLE.POSITION_VOLUME
                        );

                    double candidateSL =
                        _client.PositionGetDouble(
                            ENUM_POSITION_PROPERTY_DOUBLE.POSITION_SL
                        );

                    double candidateTP =
                        _client.PositionGetDouble(
                            ENUM_POSITION_PROPERTY_DOUBLE.POSITION_TP
                        );

                    string symbolAfter =
                        _client.PositionGetString(
                            ENUM_POSITION_PROPERTY_STRING.POSITION_SYMBOL
                        );

                    if (!string.Equals(
                            symbolBefore,
                            symbolAfter,
                            StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(
                            symbolAfter,
                            symbol,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        LogFile.Write(
                            $"[{symbol}] ⚠ POSITION SNAPSHOT CHANGED DURING READ | " +
                            $"candidateTicket={candidateTicket} | " +
                            $"before={symbolBefore} | after={symbolAfter} | " +
                            $"action=RETRY"
                        );

                        continue;
                    }

                    positionTicket = candidateTicket;
                    entryPrice = candidateEntry;
                    volume = candidateVolume;
                    stopLoss = candidateSL;
                    takeProfit = candidateTP;

                    return true;
                }
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

        // =============================================================
        // LEGACY SECONDARY ENTRY LOOP REMOVED
        //
        // New crypto positions may originate only from FastEntryEngine.
        // =============================================================

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

        public static bool IsCleanSpace(
            MqlRates[] rates,
            int lookahead = 6
        )
        {
            if (rates == null || rates.Length < lookahead + 2)
                return false;

            int overlapCount = 0;

            for (int i = 1; i <= lookahead; i++)
            {
                var c = rates[^i];
                var prev = rates[^(i + 1)];

                if (Math.Min(c.high, prev.high) > Math.Max(c.low, prev.low))
                    overlapCount++;
            }

            // 🔥 ONLY block EXTREME chop
            return overlapCount < lookahead * 0.8;
        }

        public async Task OpenTrade(TradeSide side, double price, LotSizingMode sizingMode,
                                     EntryModel entry, MqlRates[] rates1H, TradeDecision tradeDecision,
                                      TransitionResult transition, MetaTrader.Services.TradeType tradeType)
        {

            double point = _client.SymbolInfoDouble(SYMBOL, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_POINT);
            double atr = CalculateATR(rates1H, 14);

            var sltp = SlTpProfileEngine.GetSlTp(SYMBOL, atr, point, tradeType);

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
                Sl = sl,
                Tp = tp,
                Deviation = 100,
                Type_filling = filling,
                Magic = (ulong)MAGIC_NUMBER,
                Comment = "EMA closed-candle"
            };

            if (_client.OrderSend(request, out MqlTradeResult result) && result != null)
            {
                TradeServiceEngine.closingTrade = false;

                // ----------------------------------------------------------
                // result.Order is the ORDER ticket.
                // Do NOT store it as the TrackedTrade position ticket.
                // ----------------------------------------------------------
                ulong orderTicket = result.Order;

                LogFile.Write(
                    $"[{SYMBOL}] ✅ ORDER ACCEPTED | " +
                    $"orderTicket={orderTicket} | " +
                    $"dealTicket={result.Deal}"
                );

                // ----------------------------------------------------------
                // Resolve the ACTUAL broker POSITION.
                //
                // Never assume result.Order is the position ticket.
                // Retry until MT5 exposes a position that matches this exact
                // symbol and this EA's magic number.
                // ----------------------------------------------------------
                ulong positionTicket = 0;
                double actualEntryPrice = 0.0;
                double actualVolume = 0.0;
                double actualSL = 0.0;
                double actualTP = 0.0;

                bool positionResolved = false;

                for (int attempt = 1; attempt <= 100; attempt++)
                {
                    positionResolved =
                        TryResolveOpenPositionSnapshot(
                            SYMBOL,
                            out positionTicket,
                            out actualEntryPrice,
                            out actualVolume,
                            out actualSL,
                            out actualTP
                        );

                    if (positionResolved &&
                        positionTicket > 0)
                    {
                        LogFile.Write(
                            $"[{SYMBOL}] ✅ ACTUAL POSITION RESOLVED | " +
                            $"attempt={attempt} | " +
                            $"orderTicket={orderTicket} | " +
                            $"positionTicket={positionTicket} | " +
                            $"entry={actualEntryPrice:F5} | " +
                            $"volume={actualVolume:F2}"
                        );

                        break;
                    }

                    if (attempt == 1 ||
                        attempt % 10 == 0)
                    {
                        LogFile.Write(
                            $"[{SYMBOL}] ⏳ WAITING FOR ACTUAL POSITION | " +
                            $"attempt={attempt}/100 | " +
                            $"orderTicket={orderTicket} | " +
                            $"dealTicket={result.Deal}"
                        );
                    }

                    await Task.Delay(100);
                }

                if (!positionResolved ||
                    positionTicket == 0)
                {
                    LogFile.Write(
                        $"[{SYMBOL}] 🚨 CRITICAL - ORDER ACCEPTED BUT POSITION COULD NOT BE RESOLVED | " +
                        $"orderTicket={orderTicket} | " +
                        $"dealTicket={result.Deal} | " +
                        $"action=DO NOT USE ORDER TICKET AS POSITION TICKET"
                    );

                    return;
                }


                ticket = positionTicket;

                var trade = new Trade
                {
                    Ticket = positionTicket,
                    Symbol = SYMBOL,
                    Side = side.ToString(),
                    OpenTime = DateTime.UtcNow,
                    EntryPrice = actualEntryPrice,
                    Volume = actualVolume,
                    StopLoss = actualSL,
                    IsClosed = false,
                    TakeProfit = actualTP,
                    PeakOrTrough = actualEntryPrice,
                    Model = entry,
                    IsProven = false
                };

                var tracked = new TrackedTrade
                {
                    Ticket = positionTicket,
                    Symbol = SYMBOL,
                    Side = side,
                    MaxProfitUsd = 0,
                    ProfitUsd = 0,
                    OpenTime = DateTime.UtcNow,
                    EntryPrice = actualEntryPrice,
                    Volume = actualVolume,
                    StopLoss = actualSL,
                    IsClosed = false,
                    TakeProfit = actualTP,
                    PeakOrTrough = actualEntryPrice,
                    Model = entry,
                    EntryCandleTime = rates1H[^1].time,
                    CandlesSinceEntry = 0,
                    IsProven = false
                };

                lock (_openTradesSync)
                {
                    // One trade at a time. Avoid duplicate local objects if
                    // transaction callbacks fired while OpenTrade was waiting.
                    openTrades.RemoveAll(
                        x => x.Symbol.Equals(
                            SYMBOL,
                            StringComparison.OrdinalIgnoreCase
                        )
                    );

                    openTrades.Add(tracked);
                }

                LogFile.Write(
                    $"[{SYMBOL}] ✅ POSITION TRACKED | " +
                    $"orderTicket={orderTicket} | " +
                    $"positionTicket={positionTicket} | " +
                    $"entry={actualEntryPrice:F5} | " +
                    $"volume={actualVolume:F2} | " +
                    $"SL={actualSL:F5} | " +
                    $"TP={actualTP:F5}"
                );

                if (globalEntryModel ==
                    EntryModel.TrendRideContinuation)
                {
                    TradeTrackingService.RegisterTrade(
                        positionTicket
                    );
                }

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

            double liveOpen = rates1H[^1].close;

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
            bool emaCompressed = Util.MarketStateEngine.IsEmaCompressed(
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
            ImpulseBreakout,
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

        private static int barsSinceEntry = 0;
        static async Task ManageOpenTrades(
    double midPrice,
    double bid,
    double ask,
    MqlRates[] rates1Hr,
    SymbolContext ctx,
    MomentumResult momentumResult,
    bool? isBuy = null)
        {
            double atr =
                CalculateATR(
                    rates1Hr,
                    14
                );


            double[] emaFast1H =
                CalculateEMAUpdate(
                    rates1Hr,
                    9
                );


            double[] emaSlow1H =
                CalculateEMAUpdate(
                    rates1Hr,
                    21
                );


            // ==================================================
            // 1. ALWAYS SYNC OPEN TRADES FIRST
            // ==================================================

            List<long> mt5Tickets =
                GetAllOpenPositionTickets();


            if (openTrades.Count == 0 &&
                mt5Tickets.Count > 0)
            {
                long ticket =
                    mt5Tickets.First();


                if (_client.PositionSelectByTicket(
                        (ulong)ticket))
                {
                    var recoveredTrade =
                        new TrackedTrade
                        {
                            Ticket =
                                (ulong)ticket,

                            Symbol =
                                _client.PositionGetString(
                                    ENUM_POSITION_PROPERTY_STRING.POSITION_SYMBOL
                                ),

                            EntryPrice =
                                _client.PositionGetDouble(
                                    ENUM_POSITION_PROPERTY_DOUBLE.POSITION_PRICE_OPEN
                                ),

                            StopLoss =
                                _client.PositionGetDouble(
                                    ENUM_POSITION_PROPERTY_DOUBLE.POSITION_SL
                                ),

                            TakeProfit =
                                _client.PositionGetDouble(
                                    ENUM_POSITION_PROPERTY_DOUBLE.POSITION_TP
                                ),

                            Volume =
                                _client.PositionGetDouble(
                                    ENUM_POSITION_PROPERTY_DOUBLE.POSITION_VOLUME
                                ),

                            Side =
                                _client.PositionGetInteger(
                                    ENUM_POSITION_PROPERTY_INTEGER.POSITION_TYPE
                                )
                                ==
                                (long)ENUM_POSITION_TYPE.POSITION_TYPE_BUY

                                    ? TradeSide.Buy
                                    : TradeSide.Sell,

                            // This path should now be rare.  It is only a
                            // recovery path when local tracking was lost.
                            Model =
                                globalEntryModel
                        };

                    lock (_openTradesSync)
                    {
                        if (!openTrades.Any(
                                x => x.Ticket == recoveredTrade.Ticket))
                        {
                            openTrades.Add(recoveredTrade);
                        }
                    }

                    LogFile.Write(
                        $"[{recoveredTrade.Symbol}] 🔄 RECOVERED OPEN POSITION | " +
                        $"ticket={recoveredTrade.Ticket} | " +
                        $"model={recoveredTrade.Model}"
                    );
                }
            }


            // ==================================================
            // 2. LOOP THROUGH OPEN TRADES
            // ==================================================

            foreach (var t in openTrades.ToList())
            {
                string brokerSymbol;
                double brokerProfit;
                double brokerSL;
                double brokerTP;
                double brokerVolume;

                lock (_mt5PositionSync)
                {
                    if (!_client.PositionSelectByTicket(
                            t.Ticket))
                    {
                        LogFile.Write(
                            $"[{t.Symbol}] ⚠️ RISK MANAGER POSITION NOT SELECTABLE | " +
                            $"ticket={t.Ticket}"
                        );

                        continue;
                    }

                    brokerSymbol =
                        _client.PositionGetString(
                            ENUM_POSITION_PROPERTY_STRING.POSITION_SYMBOL
                        );

                    if (!string.Equals(
                            brokerSymbol,
                            t.Symbol,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        LogFile.Write(
                            $"[{t.Symbol}] 🚨 RISK MANAGER SYMBOL MISMATCH | " +
                            $"ticket={t.Ticket} | brokerSymbol={brokerSymbol} | action=SKIP"
                        );

                        continue;
                    }

                    brokerProfit =
                        _client.PositionGetDouble(
                            ENUM_POSITION_PROPERTY_DOUBLE.POSITION_PROFIT
                        );

                    brokerSL =
                        _client.PositionGetDouble(
                            ENUM_POSITION_PROPERTY_DOUBLE.POSITION_SL
                        );

                    brokerTP =
                        _client.PositionGetDouble(
                            ENUM_POSITION_PROPERTY_DOUBLE.POSITION_TP
                        );

                    brokerVolume =
                        _client.PositionGetDouble(
                            ENUM_POSITION_PROPERTY_DOUBLE.POSITION_VOLUME
                        );
                }

                t.ProfitUsd =
                    brokerProfit;

                t.StopLoss =
                    brokerSL;

                t.TakeProfit =
                    brokerTP;

                t.Volume =
                    brokerVolume;


                // ==================================================
                // GET SYMBOL TICK SIZE / TICK VALUE
                //
                // CRITICAL FOR DOLLAR PROFIT PROTECTION.
                //
                // tickSize:
                // minimum real price movement supported by broker.
                //
                // tickValue:
                // account-currency value of one tick for one lot.
                //
                // These are retrieved for EACH open symbol.
                // ==================================================

                double tickSize =
                    _client.SymbolInfoDouble(
                        t.Symbol,
                        ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_TRADE_TICK_SIZE
                    );


                double tickValue =
                    _client.SymbolInfoDouble(
                        t.Symbol,
                        ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_TRADE_TICK_VALUE
                    );


                // ==================================================
                // FALLBACK TICK VALUE
                //
                // Some brokers may return zero for SYMBOL_TRADE_TICK_VALUE.
                //
                // Try PROFIT tick value as fallback.
                // ==================================================

                if (tickValue <= 0)
                {
                    tickValue =
                        _client.SymbolInfoDouble(
                            t.Symbol,
                            ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_TRADE_TICK_VALUE_PROFIT
                        );
                }


                // ==================================================
                // GET BROKER MINIMUM STOP DISTANCE
                //
                // SYMBOL_TRADE_STOPS_LEVEL is returned in POINTS.
                //
                // Convert points to actual PRICE distance before
                // passing it to UnifiedRiskManagementEngine.
                // ==================================================

                double pointSize =
                    _client.SymbolInfoDouble(
                        t.Symbol,
                        ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_POINT
                    );


                long stopLevelPoints =
                    _client.SymbolInfoInteger(
                        t.Symbol,
                        ENUM_SYMBOL_INFO_INTEGER.SYMBOL_TRADE_STOPS_LEVEL
                    );


                double brokerMinStopDistancePrice =
                    stopLevelPoints > 0 &&
                    pointSize > 0

                        ? stopLevelPoints *
                          pointSize

                        : 0.0;


                // ==================================================
                // SYMBOL PROPERTY DIAGNOSTIC
                //
                // This allows us to verify MT5 is giving the bot the
                // expected values for XAU, crypto, FX, etc.
                // ==================================================

                LogFile.Write(
                    $"[{t.Symbol}] ⚙️ SYMBOL RISK INFO | " +

                    $"ticket={t.Ticket} | " +

                    $"volume={t.Volume:F2} | " +

                    $"tickSize={tickSize:F8} | " +

                    $"tickValue={tickValue:F5} | " +

                    $"point={pointSize:F8} | " +

                    $"stopsLevelPoints={stopLevelPoints} | " +

                    $"minStopPrice={brokerMinStopDistancePrice:F8}"
                );


                // ==================================================
                // CURRENT CANDLE INFORMATION
                // ==================================================

                var c0 =
                    rates1Hr[^1];


                t.LastCandleBodyUsd =
                    Math.Abs(
                        c0.close -
                        c0.open
                    );


                // ==================================================
                // 3. UNIFIED RISK DECISION
                //
                // IMPORTANT:
                //
                // Pass:
                //
                // brokerMinStopDistancePrice
                // tickSize
                // tickValue
                //
                // IN THAT ORDER.
                // ==================================================

                var riskDecision =
                    UnifiedRiskManagementEngine.Evaluate(
                        t,
                        t.Symbol,
                        bid,
                        ask,
                        atr,
                        rates1Hr,
                        emaFast1H,
                        emaSlow1H,
                        brokerMinStopDistancePrice,
                        tickSize,
                        tickValue
                    );


                LogFile.Write(
                    $"[{t.Symbol}] Unified Risk Decision | " +
                    $"action={riskDecision.Action} | " +
                    $"stage={riskDecision.Stage} | " +
                    $"profit={t.ProfitUsd:F2} | " +
                    $"peak={t.MaxProfitUsd:F2} | " +
                    $"SL={t.StopLoss:F5} | " +
                    $"tickSize={tickSize:F8} | " +
                    $"tickValue={tickValue:F5} | " +
                    $"reason={riskDecision.Reason}"
                );


                // ==================================================
                // 4. EXECUTE ONE RISK ACTION
                // ==================================================

                switch (riskDecision.Action)
                {
                    // ==============================================
                    // HOLD
                    // ==============================================

                    case UnifiedRiskManagementEngine.RiskAction.Hold:
                        {
                            // Nothing to do.
                            // Let the trade continue.

                            break;
                        }


                    // ==============================================
                    // MOVE STOP LOSS
                    // ==============================================

                    case UnifiedRiskManagementEngine.RiskAction.MoveStopLoss:
                        {
                            double oldSL =
                                t.StopLoss;


                            double newSL =
                                riskDecision.NewStopLoss;


                            // ==================================================
                            // FINAL SAFETY CHECK
                            //
                            // UnifiedRiskManagementEngine should already normalize
                            // the SL, but reject obviously invalid values here too.
                            // ==================================================

                            if (newSL <= 0)
                            {
                                LogFile.Write(
                                    $"[{t.Symbol}] ⚠️ Unified Risk returned invalid SL | " +
                                    $"ticket={t.Ticket} | " +
                                    $"newSL={newSL:F5}"
                                );

                                break;
                            }


                            // ==================================================
                            // PREVENT SAME-TICK MODIFICATIONS
                            //
                            // This gives another layer of protection against
                            // MT5 retcode 10025 / NO CHANGES.
                            // ==================================================

                            bool effectivelySameStop =
                                false;


                            if (oldSL > 0 &&
                                tickSize > 0)
                            {
                                double stopDifference =
                                    Math.Abs(
                                        newSL -
                                        oldSL
                                    );


                                effectivelySameStop =
                                    stopDifference <
                                    tickSize *
                                    0.5;
                            }


                            if (effectivelySameStop)
                            {
                                LogFile.Write(
                                    $"[{t.Symbol}] ✅ SL modification skipped - " +
                                    $"already at equivalent broker tick | " +
                                    $"ticket={t.Ticket} | " +
                                    $"oldSL={oldSL:F5} | " +
                                    $"requestedSL={newSL:F5} | " +
                                    $"tickSize={tickSize:F8}"
                                );

                                break;
                            }


                            LogFile.Write(
                                $"[{t.Symbol}] 🔒 Unified Risk requesting SL move | " +
                                $"ticket={t.Ticket} | " +
                                $"oldSL={oldSL:F5} → " +
                                $"newSL={newSL:F5} | " +
                                $"tickSize={tickSize:F8}"
                            );


                            try
                            {
                                // ==================================================
                                // Temporarily apply requested SL to tracked object.
                                // ==================================================

                                t.StopLoss =
                                    newSL;


                                // ==================================================
                                // SEND MT5 MODIFICATION
                                // ==================================================

                                ModifyTrade(t);


                                // Give MT5 a short opportunity to process.
                                await Task.Delay(50);


                                // ==================================================
                                // RE-READ BROKER SL
                                //
                                // Broker becomes authoritative after modification.
                                // ==================================================

                                if (_client.PositionSelectByTicket(
                                        t.Ticket))
                                {
                                    double confirmedBrokerSL =
                                        _client.PositionGetDouble(
                                            ENUM_POSITION_PROPERTY_DOUBLE.POSITION_SL
                                        );


                                    t.StopLoss =
                                        confirmedBrokerSL;


                                    double differenceFromRequested =
                                        Math.Abs(
                                            confirmedBrokerSL -
                                            newSL
                                        );


                                    bool protectionApplied =
                                        tickSize <= 0 ||

                                        differenceFromRequested <=
                                        tickSize *
                                        0.5;


                                    LogFile.Write(
                                        $"[{t.Symbol}] ✅ Unified Risk SL sync | " +
                                        $"ticket={t.Ticket} | " +
                                        $"requestedSL={newSL:F5} | " +
                                        $"brokerSL={confirmedBrokerSL:F5} | " +
                                        $"difference={differenceFromRequested:F8} | " +
                                        $"tickSize={tickSize:F8} | " +
                                        $"applied={protectionApplied}"
                                    );
                                }
                                else
                                {
                                    // Position may have been stopped out while
                                    // modification was being processed.

                                    LogFile.Write(
                                        $"[{t.Symbol}] ℹ️ Position disappeared during SL update | " +
                                        $"ticket={t.Ticket}"
                                    );
                                }
                            }
                            catch (Exception ex)
                            {
                                // ==================================================
                                // IMPORTANT:
                                //
                                // Restore actual broker SL, NOT blindly old cached SL.
                                // ==================================================

                                if (_client.PositionSelectByTicket(
                                        t.Ticket))
                                {
                                    t.StopLoss =
                                        _client.PositionGetDouble(
                                            ENUM_POSITION_PROPERTY_DOUBLE.POSITION_SL
                                        );
                                }
                                else
                                {
                                    t.StopLoss =
                                        oldSL;
                                }


                                LogFile.Write(
                                    $"[{t.Symbol}] ❌ Unified Risk SL failed | " +
                                    $"ticket={t.Ticket} | " +
                                    $"oldSL={oldSL:F5} | " +
                                    $"attemptedSL={newSL:F5} | " +
                                    $"brokerSL={t.StopLoss:F5} | " +
                                    $"tickSize={tickSize:F8} | " +
                                    $"error={ex.Message}"
                                );
                            }


                            break;
                        }


                    // ==============================================
                    // CLOSE POSITION
                    // ==============================================

                    case UnifiedRiskManagementEngine.RiskAction.Close:
                        {
                            LogFile.Write(
                                $"[{t.Symbol}] 🚨 UNIFIED RISK CLOSE REQUEST | " +
                                $"ticket={t.Ticket} | " +
                                $"profit={t.ProfitUsd:F2} | " +
                                $"peak={t.MaxProfitUsd:F2} | " +
                                $"reason={riskDecision.Reason}"
                            );


                            TradeServiceEngine.closingTrade =
                                true;


                            // ==================================================
                            // IMPORTANT
                            //
                            // This preserves your existing architecture.
                            //
                            // Your separate close path/event handler is expected
                            // to execute the actual close when closingTrade=true.
                            // ==================================================

                            await Task.Delay(100);


                            // ==================================================
                            // VERIFY WHETHER POSITION ACTUALLY DISAPPEARED
                            // ==================================================

                            bool stillOpen =
                                _client.PositionSelectByTicket(
                                    t.Ticket
                                );


                            if (!stillOpen)
                            {
                                LogFile.Write(
                                    $"[{t.Symbol}] ✅ Position confirmed closed | " +
                                    $"ticket={t.Ticket}"
                                );


                                openTrades.Remove(t);
                            }
                            else
                            {
                                // Refresh latest broker information so our tracked
                                // object does not retain stale values.

                                t.ProfitUsd =
                                    _client.PositionGetDouble(
                                        ENUM_POSITION_PROPERTY_DOUBLE.POSITION_PROFIT
                                    );


                                t.StopLoss =
                                    _client.PositionGetDouble(
                                        ENUM_POSITION_PROPERTY_DOUBLE.POSITION_SL
                                    );


                                LogFile.Write(
                                    $"[{t.Symbol}] ⚠️ Position still open after close request | " +
                                    $"ticket={t.Ticket} | " +
                                    $"profit={t.ProfitUsd:F2} | " +
                                    $"brokerSL={t.StopLoss:F5}"
                                );
                            }


                            break;
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
            var tickets =
                new List<long>();

            lock (_mt5PositionSync)
            {
                int total =
                    _client.PositionsTotal();

                for (int i = 0; i < total; i++)
                {
                    ulong ticketU =
                        _client.PositionGetTicket(i);

                    if (ticketU == 0)
                        continue;

                    tickets.Add((long)ticketU);
                }
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
