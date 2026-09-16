using chatgptbot.Util;
using MetaTrader.Data.Entities;
using MetaTrader.Dto;
using MetaTrader.helper;
using MetaTrader.Services.Contract;
using MetaTrader.Util;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MtApi5;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

namespace MetaTrader.Services
{
    public class MetaTraderConnectionService : BackgroundService, IMetaTraderConnectionService
    {
        private readonly ILogger<MetaTraderConnectionService> _logger;
        private readonly TradeServiceEngine _tradeService;
        private readonly ITradingPairProperties _tradingPairProperties;
        private readonly TradeStateStore _stateStore;
        private readonly IHubContext<ChatHub> _chatHub;

        private string tframe;
        private string symbol;
        private readonly MtApi5Client _client;
        private bool processBegun = false;
        private bool mt5Connected = false;

        private TcpListener _listener;
        public MetaTraderConnectionService(
            ITradingPairProperties tradingPairProperties,
            ILogger<MetaTraderConnectionService> logger,
            TradeServiceEngine tradeService, TradeStateStore stateStore, MtApi5Client client,
            IHubContext<ChatHub> chatHub)
        {
            _client = client;
            _tradingPairProperties = tradingPairProperties;
            _logger = logger;
            _stateStore = stateStore;
            _tradeService = tradeService;
            _chatHub = chatHub;
        }
        /*
        protected async override Task ExecuteAsync(CancellationToken stoppingToken)
        {  
            await Task.Delay(1000, stoppingToken);
        }
        */
        // ------------------------------------------------------------
        // EXECUTEASYNC — IIS SAFE LOOP
        // ------------------------------------------------------------
        
         protected async override Task ExecuteAsync(CancellationToken stoppingToken)
         {
             
             _logger.LogInformation("MT5 Gateway listening on port 8220");

             LogFile.Write("MetaTrader bot service starting...");
             _logger.LogInformation("MetaTrader bot service starting...");
             SafeConnectToMt5();
             // Attach events once

             _client.ConnectionStateChanged += Api_ConnectionStateChanged;
             _client.OnTradeTransaction += Client_OnTradeTransaction;
             await _stateStore.SaveAsync(false);
             // MAIN LOOP
             while (!stoppingToken.IsCancellationRequested)
             {
                
                 try
                 {
                     await RunBotAsync(stoppingToken);
                 }
                 catch (Exception ex)
                 {
                     Log($"[FATAL LOOP ERROR] {ex.Message}\n{ex.StackTrace}");
                     _logger.LogError(ex, "Fatal exception in bot loop.");
                     LogFile.Write($"[FATAL LOOP ERROR] {ex.Message}\n{ex.StackTrace}");
                 }

                 await Task.Delay(1000, stoppingToken);
             }

             LogFile.Write("MetaTrader bot service stopping...");
         }
         
        // ------------------------------------------------------------
        // MAIN BOT RUN LOGIC
        // ------------------------------------------------------------

        private async Task RunBotAsync(CancellationToken token)
        {
            
            // Trigger beginTrading ONCE

            await StartTradingAsync(token);

        }
       

        // ------------------------------------------------------------
        // BEGIN TRADING SAFELY
        // ------------------------------------------------------------

        private async Task StartTradingAsync(CancellationToken cancellationtoken)
        {
            try
            {
                TradeState state = await _stateStore.LoadAsync();
                LogFile.Write($"Autotrade state...{state.AutoTrade}");

                if (ConnectionService.HasAny() && state.AutoTrade == false)
                {
                    LogFile.Write("Starting trading for the first time...");
                    LogFile.Write("Before beginTrading");
                    await _stateStore.SaveAsync(true);

                    LogFile.Write($"Total Connection IDs: {ConnectionService.GetAll().Count}");

                    TradingEventBus.OnState += async (stateUpdate) =>
                    {
                            await _chatHub.Clients.Group("dashboards").SendAsync("onStateUpdate", stateUpdate);
                    };

                    TradingEventBus.onM15Snapshot += async (m15Snapshot) =>
                    {
                            await _chatHub.Clients.Group("dashboards").SendAsync("onM15Snapshot", m15Snapshot);
                    };

                    TradingEventBus.onSniper += async (sniperaction) =>
                    {
                            await _chatHub.Clients.Group("dashboards").SendAsync("onSniperUpdate", sniperaction);
                    };

                    TradingEventBus.OnTrade += async (trade) =>
                    {
                            await _chatHub.Clients.Group("dashboards").SendAsync("onTradeAction", trade);
                    };

                    TradingEventBus.OnDecision += async (decision) =>
                    {
                            await _chatHub.Clients.Group("dashboards").SendAsync("onDecisionAction", decision);
                    };

                    TradingEventBus.onPerformanceContext += async (performanceContext) =>
                    {
                            await _chatHub.Clients.Group("dashboards").SendAsync("onPerformanceContext", performanceContext);
                    };

                    TradingEventBus.onMarketPhaseLoop += async (marketPhase) =>
                    {
                            await _chatHub.Clients.Group("dashboards").SendAsync("onMarketPhaseUpdate", marketPhase);
                    };

                    await _tradeService.openConnection(
                        async (chunk) =>
                        {
                            try
                            {   
                                       await _chatHub.Clients.Group("dashboards")
                                            .SendAsync("BuySellIndicator", chunk);
                            }
                            catch (Exception ex)
                            {
                                LogFile.Write($"SignalR error: {ex.Message}");
                            }
                        },
                        _client
                    );
                    double accountEquity = _client.AccountInfoDouble(ENUM_ACCOUNT_INFO_DOUBLE.ACCOUNT_EQUITY);
                    EquityProtectionEngine.Initialize(accountEquity); // re-baseline after forced close
                    
                    LogFile.Write("After beginTrading"); // ❌ you will NEVER see this
                }
                else    
                {
                    if (state.AutoTrade == true)
                      _tradeService.checkTradingWindow(false, cancellationtoken);
                }
            }
            catch (Exception ex)
            {
                LogFile.Write($"Trading loop crashed: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        public void startTrading()
        {
            _tradeService.setTradingBegun(true);
            

        }

        // ------------------------------------------------------------
        // SAFE CONNECTION HANDLER
        // ------------------------------------------------------------
        private void SafeConnectToMt5()
        {
            try
            {
                LogFile.Write("Connecting to MT5...");
                _client.BeginConnect("127.0.0.1", 8228);
            }
            catch (Exception ex)
            {
                LogFile.Write($"MT5 connection failed: {ex.Message}");
            }
        }


        // ------------------------------------------------------------
        // EVENTS
        // ------------------------------------------------------------
        private void Api_ConnectionStateChanged(object sender, Mt5ConnectionEventArgs e)
        {
            Log($"MT5 Connection State: {e.Status}");

            if (e.Status == Mt5ConnectionState.Connected)
            {
                mt5Connected = true;
            }
            else
            {
                mt5Connected = false;
            }
        }

        private void Client_OnTradeTransaction(object sender, Mt5TradeTransactionEventArgs e)
        {
            LogFile.Write("Trade update received.");

            try
            {
                LogFile.Write(
            $"TRADE TRANSACTION | {e}");
                _tradeService.CheckRecentlyClosedPositions();
            }
            catch (Exception ex)
            {
                LogFile.Write(
                    $"Error checking closed positions: {ex.Message}");
            }
        }


        // ------------------------------------------------------------
        // LOGGING
        // ------------------------------------------------------------
        private static readonly string LogPath = "C:\\Logs\\MetaTraderBot.log";

        private void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory("C:\\Logs");
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {msg}\n");
            }
            catch
            {
                // Avoid throwing in logging (critical for IIS)
            }
        }
    }

}
