using chatgptbot.Util;
using FATCA3.Web.Base;
using MetaTrader.Dto;
using MetaTrader.Enums;
using MetaTrader.helper;
using MetaTrader.Services;
using MetaTrader.Services.Contract;
using MetaTrader.Services.Interfaces;
using MetaTrader.Streamer;
using MetaTrader.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MetaTrader.Controllers
{
    [Route("api/v1/[controller]")]
    [ApiController]
    public class TradeController : BaseController
    {
        private readonly IHubContext<ChatHub> _chatHub;
        private const string LoggerScope = nameof(TradeController);
        //private readonly ITradeService _tradeService;
        private readonly IMetaTraderConnectionService _MetaTraderConnectionService;
        private readonly TradeStateStore _stateStore;
        private readonly INotificationService _notificationService;
        private readonly IConfiguration _configuration;
        private readonly TradeServiceEngine _tradeService;

        // ============================================================
        // GOLD - DUPLICATE START PROTECTION
        // ============================================================

        private static readonly SemaphoreSlim _startLock =
            new SemaphoreSlim(1, 1);

        private static bool _eventsRegistered = false;

        private static bool _tradingStartRequested = false;
        public TradeController(
                                //ITradeService tradeService,
                                TradeServiceEngine tradeService,
                                 IHubContext<ChatHub> chatHub,
                                  IConfiguration configuration,
                                  INotificationService notificationService,
                                   IMetaTraderConnectionService metaTraderConnectionService,
                                   TradeStateStore stateStore,
                                     
        ILogger<TradeController> logger) : base(logger)
        {
            _stateStore = stateStore;
            _chatHub = chatHub;
            _tradeService = tradeService;
            _MetaTraderConnectionService = metaTraderConnectionService;
            _notificationService = notificationService;
            _configuration = configuration;
           
        }

        [HttpGet("connection/provider")]

        public async Task<IActionResult> generateConnectionId()
        {
            Results<String> res = new Results<String>();
            res.Data = Guid.NewGuid().ToString();
            res.status = 200;
            res.message = "Connection ID";
            return Ok(await Task.FromResult(res));
        }

        [HttpPost("startequity")]
        public async Task<IActionResult> startEquity(String connectionId, String pair, CancellationToken cancellationToken)
        {
            Results<double> res = new Results<double>();
            res.Data = await _tradeService.getStartEquity();
            res.message = "startEquity";
            res.messageType = "data";
            res.status = 200;
            return Ok(res);
        }
        //string symbol, string tframe, string connectionId
        [HttpPost("initialize/{connectionId}")]
        public async Task<IActionResult> initializeTradingProperties(string connectionId, 
                                                [FromServices] TradeStateStore stateStore,
                                                     CancellationToken cancellationToken)
        {
            _tradeService._connectionId = connectionId;

            Results<string> res = new Results<string>();
            res.status = 200;
            return Ok(await Task.FromResult(res));
        }

        [HttpPost("auto/trade")]
        public async Task<IActionResult> auto()
        {
            bool isIis =
                   Environment.GetEnvironmentVariable("ASPNETCORE_IIS_PHYSICAL_PATH") != null ||
                   Environment.GetEnvironmentVariable("ASPNETCORE_IIS_SITE_NAME") != null;
            Results<bool> res = new Results<bool>();
            if (isIis)
            {
                await _stateStore.SaveAsync(true);
                res.Data = true;
            }
            else
            {
                await _stateStore.SaveAsync(false);
                res.Data = false;
            }

            res.status = 200;

            return Ok(await Task.FromResult(res));
        }

        [HttpPost("start/trade")]
        public async Task<IActionResult> start(CancellationToken ct)
        {
            bool isIis =
                    Environment.GetEnvironmentVariable("ASPNETCORE_IIS_PHYSICAL_PATH") != null ||
                    Environment.GetEnvironmentVariable("ASPNETCORE_IIS_SITE_NAME") != null;

            Results<string> res = new Results<string>();
            res.status = 200;
            if (isIis)
            {
                _MetaTraderConnectionService.startTrading();
            }
            else
            {
                _tradeService.startTrading(ct);
            }
            //OrderBlockService obService = new OrderBlockService(_notificationService, _configuration, false);
            //obService.startTrading();
            return Ok(await Task.FromResult(res));
        }



        [HttpPost("connect/{connectionId}")]
        public async Task<IActionResult> connect(
            string connectionId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return BadRequest("SignalR connection ID is required.");
            }

            bool isIis =
                Environment.GetEnvironmentVariable(
                    "ASPNETCORE_IIS_PHYSICAL_PATH") != null ||
                Environment.GetEnvironmentVariable(
                    "ASPNETCORE_IIS_SITE_NAME") != null;

            if (!isIis)
            {
                return Conflict(new
                {
                    status = 409,
                    message = "Trading start is restricted to IIS."
                });
            }

            await _startLock.WaitAsync(cancellationToken);

            try
            {
                // Always update the dashboard connection ID.
                // Do not use the connection ID as a trading-start flag.
                _tradeService._connectionId = connectionId;

                ConnectionService.Add(connectionId);

                LogFile.Write(
                    $"GOLD controller connection ID: {connectionId}"
                );

                // Register TradingEventBus handlers only once.
                if (!_eventsRegistered)
                {
                    // Capture the hub context, not this controller.
                    var hub = _chatHub;

                    TradingEventBus.onMarketPhaseLoop +=
                        async (marketPhase) =>
                        {
                            await hub.Clients.Group("dashboards")
                                .SendAsync(
                                    "onMarketPhaseUpdate",
                                    marketPhase
                                );
                        };

                    TradingEventBus.OnState +=
                        async (tradeaction) =>
                        {
                            await hub.Clients.Group("dashboards")
                                .SendAsync(
                                    "onStateUpdate",
                                    tradeaction
                                );
                        };

                    TradingEventBus.onSniper +=
                        async (sniperaction) =>
                        {
                            await hub.Clients.Group("dashboards")
                                .SendAsync(
                                    "onSniperUpdate",
                                    sniperaction
                                );
                        };

                    TradingEventBus.OnDecision +=
                        async (decision) =>
                        {
                            await hub.Clients.Group("dashboards")
                                .SendAsync(
                                    "onDecisionAction",
                                    decision
                                );
                        };

                    TradingEventBus.OnTrade +=
                        async (trade) =>
                        {
                            await hub.Clients.Group("dashboards")
                                .SendAsync(
                                    "onTradeAction",
                                    trade
                                );
                        };

                    TradingEventBus.onM15Snapshot +=
                        async (m15Snapshot) =>
                        {
                            await hub.Clients.Group("dashboards")
                                .SendAsync(
                                    "onM15Snapshot",
                                    m15Snapshot
                                );
                        };

                    TradingEventBus.onPerformanceContext +=
                        async (performanceContext) =>
                        {
                            await hub.Clients.Group("dashboards")
                                .SendAsync(
                                    "onPerformanceContext",
                                    performanceContext
                                );
                        };

                    _eventsRegistered = true;

                    LogFile.Write(
                        "GOLD TradingEventBus handlers registered."
                    );
                }

                // Do not launch the trading engine twice.
                if (_tradingStartRequested)
                {
                    LogFile.Write(
                        "GOLD connect called again. " +
                        "Existing trading start preserved. " +
                        $"Dashboard connection ID: {connectionId}"
                    );

                    return Ok(new
                    {
                        status = 200,
                        started = false,
                        alreadyRequested = true,
                        connectionId,
                        message =
                            "Trading start was already requested. " +
                            "Dashboard connection updated."
                    });
                }

                // Preserve your original callback.
                _tradeService.connect(
                    async (chunk) =>
                    {
                        await _chatHub.Clients.Group("dashboards")
                            .SendAsync("BuySellIndicator", chunk);
                    });

                // This indicates connect() was invoked successfully,
                // not that MT5 has confirmed broker connectivity.
                _tradingStartRequested = true;

                LogFile.Write(
                    "GOLD TradeServiceEngine.connect invoked."
                );

                return Ok(new
                {
                    status = 200,
                    started = true,
                    alreadyRequested = false,
                    connectionId,
                    message =
                        "Trading connection requested successfully. " +
                        "Verify MT5 status through telemetry."
                });
            }
            catch (Exception ex)
            {
                LogFile.Write(
                    $"GOLD connect error: {ex}"
                );

                return StatusCode(500, new
                {
                    status = 500,
                    message = "GOLD trading connection request failed."
                });
            }
            finally
            {
                _startLock.Release();
            }
        }


        //[HttpPost("connect/{connectionId}")]
        //public async Task<IActionResult> connect(String connectionId, CancellationToken cancellationToken)
        //{
        //    Results<string> res = new Results<string>();
        //    res.status = 200;

        //    _tradeService._connectionId = connectionId;
        //    LogFile.Write($"web service controller connection ID: {connectionId}");
        //    bool isIis =
        //          Environment.GetEnvironmentVariable("ASPNETCORE_IIS_PHYSICAL_PATH") != null ||
        //          Environment.GetEnvironmentVariable("ASPNETCORE_IIS_SITE_NAME") != null;

        //    ConnectionService.Add(connectionId);
        //    if (!string.IsNullOrWhiteSpace(connectionId))
        //    {
        //        /*change back to !isIis when live running*/
        //        if (isIis)
        //        {
        //            TradingEventBus.onMarketPhaseLoop += async (marketPhase) =>
        //            {
        //                await _chatHub.Clients.Group("dashboards").SendAsync("onMarketPhaseUpdate", marketPhase);
        //            };

        //            TradingEventBus.OnState += async (tradeaction) =>
        //            {
        //                await _chatHub.Clients.Group("dashboards").SendAsync("onStateUpdate", tradeaction);
        //            };

        //            TradingEventBus.onSniper += async (sniperaction) =>
        //            {
        //                await _chatHub.Clients.Group("dashboards").SendAsync("onSniperUpdate", sniperaction);
        //            };

        //            TradingEventBus.OnDecision += async (decision) =>
        //            {
        //                await _chatHub.Clients.Group("dashboards").SendAsync("onDecisionAction", decision);
        //            };

        //            TradingEventBus.OnTrade += async (trade) =>
        //            {
        //                await _chatHub.Clients.Group("dashboards").SendAsync("onTradeAction", trade);
        //            };

        //            TradingEventBus.onM15Snapshot += async (m15Snapshot) =>
        //            {
        //                await _chatHub.Clients.Group("dashboards").SendAsync("onM15Snapshot", m15Snapshot);
        //            };

        //            TradingEventBus.onPerformanceContext += async (performanceContext) =>
        //            {
        //                await _chatHub.Clients.Group("dashboards").SendAsync("onPerformanceContext", performanceContext);
        //            };

        //        _tradeService.connect(
        //            async (chunk) =>
        //            {
        //                // This is the callback — it's called with every message chunk from the assistant
        //                await _chatHub.Clients.Group("dashboards").SendAsync("BuySellIndicator", chunk);
        //            });
        //        }
        //    }
        //    return Ok(await Task.FromResult(res));
        //}


    }
}
