using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.Util;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using MtApi5;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;

namespace MetaTrader.Services
{
    public class TradingEngine
    {
        // -------------------------------
        // CONFIGURATION
        // -------------------------------
        private const double ContractSize = 100;   // Gold contract size
        private const double RiskPercent = 1.0;    // 1% per trade
        private const int ChoppinessPeriod = 14;
        private const int CooldownCandles = 2;
        private double RiskPerTradeUsd = 40.0;      // USD risk per trade (used for dynamic lot sizing)
        private static string SYMBOL = "XAUUSD";

        private int cooldownRemaining = 0;
        private double startingDayEquity = 0;
        private bool tradingDisabled = false;
        private const double StopLossPoints = 500.0;       // SL distance in "points"
        private const double TakeProfitPoints = 600.0;    // TP distance in "points"
        private static int MAGIC_NUMBER = 12345;
        public static MtApi5Client client = new MtApi5Client();
        private static ENUM_TIMEFRAMES timeframe = ENUM_TIMEFRAMES.PERIOD_H1; // configurable timeframe
        // Track open trades
        private List<TrackedTrader> openTrades = new();
        private static Func<object, Task> _onMessageReceived;
        private static Timer pollTimer;
        private const int BarsToFetch = 300;
        //private DetectStar star;
        DateTime _lastCandleTime = DateTime.MinValue;

        private async Task<ENUM_TIMEFRAMES> getTimeFrame(string timeframe)
        {

            switch (timeframe)
            {
                case "5m":
                    return await Task.FromResult(ENUM_TIMEFRAMES.PERIOD_M5);
                case "15m":
                    return await Task.FromResult(ENUM_TIMEFRAMES.PERIOD_M15);
                case "30m":
                    return await Task.FromResult(ENUM_TIMEFRAMES.PERIOD_M30);
                case "1h":
                    return await Task.FromResult(ENUM_TIMEFRAMES.PERIOD_H1);
                case "4h":
                    return await Task.FromResult(ENUM_TIMEFRAMES.PERIOD_H4);
                default:
                    return await Task.FromResult(ENUM_TIMEFRAMES.PERIOD_M30);
            }

        }

        private void Client_ConnectionStateChanged(object sender, Mt5ConnectionEventArgs e)
        {
            Console.WriteLine($"MT5 connection state: {e.Status}");

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


                Console.WriteLine("Connected to MT5");
                client.SymbolSelect(SYMBOL, true);
                
            }
            else
            {

                Console.WriteLine($"Connection state: {e.Status}");
                connectionState(e.Status);
            }
        }

        private void connectionState(Mt5ConnectionState status)
        {
            Task.FromResult(streamMessage<string>("string", "connectionState", status.ToString()));
        }
        public async void connect(string symbol, string tframe, Func<object, Task> onMessageReceived)
        {
            symbol = symbol.Replace(".s", "");
            timeframe = await getTimeFrame(tframe);
            _onMessageReceived = onMessageReceived;
            client.BeginConnect("127.0.0.1", 8228);
            SYMBOL = symbol != null ? symbol : SYMBOL;

            if (pollTimer != null)
                pollTimer.Close();

            client.ConnectionStateChanged += Api_ConnectionStateChanged;

            Results<string> results = new Results<string>();
            results.messageType = "string";
            results.message = "connecting";
            results.Data = "Connecting to MT5...";
            results.status = 200;
            await _onMessageReceived(results);

            Console.WriteLine("Connecting to MT5...");
        }

        public void startTrading()
        {
            

            // Start polling timer (every 5 seconds)
            //GenerateSignals();
            pollTimer = new Timer(5000);
            pollTimer.Elapsed += (s, ev) => executeTradeEngine();
            pollTimer.AutoReset = true;
            pollTimer.Enabled = true;

        }

        public void executeTradeEngine()
        {
            DateTime now = DateTime.Now;
            client.CopyRates(SYMBOL, ENUM_TIMEFRAMES.PERIOD_H1, 1, BarsToFetch, out MqlRates[] rates1H);
            client.CopyRates(SYMBOL, ENUM_TIMEFRAMES.PERIOD_H4, 1, BarsToFetch, out MqlRates[] rates4H);

            double[] emaFast1H = CalculateEMA(rates1H, 9);
            double[] emaSlow1H = CalculateEMA(rates1H, 21);

            double[] emaFast4H = CalculateEMA(rates4H, 9);
            double[] emaSlow4H = CalculateEMA(rates4H, 21);

            double accountEquity = client.AccountInfoDouble(ENUM_ACCOUNT_INFO_DOUBLE.ACCOUNT_EQUITY);

            double bid = client.SymbolInfoDouble(SYMBOL, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);
            double ask = client.SymbolInfoDouble(SYMBOL, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);

            double midPrice = (bid + ask) / 2;

            Tick(now, rates1H, rates4H, emaFast1H, emaSlow1H, emaFast4H, emaSlow4H, accountEquity, bid, ask, midPrice);
        }
        // ================================
        // ✔ PUBLIC ENTRY POINT
        // ================================
        public void Tick(
            DateTime now,
            MqlRates[] rates1H,
            MqlRates[] rates4H,
            double[] emaFast1H,
            double[] emaSlow1H,
            double[] emaFast4H,
            double[] emaSlow4H,
            double accountEquity,
            double bid,
            double ask,
            double midPrice)
        {
            if (tradingDisabled)
            {
                Console.WriteLine("[ENGINE] Trading disabled by equity guard.");
                return;
            }

            // 1. Equity Safety Guard (FTMO style)
            if (!CheckEquityGuards(accountEquity))
                return;

            // 2. Trading window + evening shutdown
            if (!IsTradingWindow(now) || IsEveningShutdown(now))
            {
                Console.WriteLine("[ENGINE] Outside trading window.");
                return;
            }

            // 3. Manage exits if in trade
            if (openTrades.Count > 0)
            {
                ManageOpenTrades(rates1H, bid, ask, midPrice);
                return;
            }

            // 4. Cooldown
            if (cooldownRemaining > 0)
            {
                if (IsNewCandleClosed(rates1H))
                {
                    cooldownRemaining--;
                    Console.WriteLine($"[ENGINE] Cooling down... {cooldownRemaining} candles left.");
                }
                return;
            }

            // 5. Choppiness
            var chop = CalculateChoppinessIndex(rates1H, ChoppinessPeriod);
            if (chop > 61)   // 61+ = choppy
            {
                Console.WriteLine($"[ENGINE] Market choppy (CI={chop:F2}). No trades.");
                return;
            }

            // 6. Multi-timeframe Momentum Check
            TradeAction action = CheckMultiTfMomentum(
                emaFast1H, emaSlow1H,
                emaFast4H, emaSlow4H,
                rates1H);

            if (action == TradeAction.EnterBuy)
                TryOpenBuy(accountEquity, rates1H, midPrice, bid, ask);

            if (action == TradeAction.EnterSell)
                TryOpenSell(accountEquity, rates1H, midPrice, bid, ask);
        }

        // ================================
        // ✔ OPEN TRADES MANAGEMENT
        // ================================
        private void ManageOpenTrades(MqlRates[] rates1H, double bid, double ask, double midPrice)
        {
            foreach (var t in openTrades.ToList())
            {
                double atr = CalculateAtr(rates1H, 14);

                // 1. Check SL/TP hit by broker
                if (IsTradeClosedByBroker(t))
                {
                    Console.WriteLine($"[EXIT] Trade {t.Ticket} closed by broker (SL/TP).");
                    openTrades.Remove(t);
                    cooldownRemaining = CooldownCandles;
                    continue;
                }

                // 2. Break-even logic
                CheckBreakEven(t, midPrice, atr);

                // 3. Tighten stop when momentum weakens
                if (ShouldTightenStop(rates1H, t))
                    TightenStopLossAtr(t, midPrice, atr);

                // 4. Trailing stop (simple ATR trailing)
                ApplyTrailingStopAtr(t, midPrice, atr, bid, ask);
            }
        }

        // ================================
        // ✔ OPEN BUY / SELL HELPERS
        // ================================
        private void TryOpenBuy(double equity, MqlRates[] rates, double midPrice, double bid, double ask)
        {
            double atr = CalculateAtr(rates, 14);
            double slDist = atr * 1.5;
            double tpDist = slDist * 2.0;
            double volume = CalculatePositionSize(equity, RiskPercent, slDist);

            Console.WriteLine($"[ENTRY] BUY: vol={volume} SL={slDist:F2} TP={tpDist:F2}");

            long ticket = OpenBuy(volume, bid, slDist, tpDist);
            if (ticket > 0)
            {
                openTrades.Add(new TrackedTrader(ticket, TradeSide.Buy, midPrice, slDist, tpDist));
            }
        }

        private void TryOpenSell(double equity, MqlRates[] rates, double midPrice, double bid, double ask)
        {
            double atr = CalculateAtr(rates, 14);
            double slDist = atr * 1.5;
            double tpDist = slDist * 2.0;
            double volume = CalculatePositionSize(equity, RiskPercent, slDist);

            Console.WriteLine($"[ENTRY] SELL: vol={volume} SL={slDist:F2} TP={tpDist:F2}");

            long ticket = OpenSell(volume, ask, slDist, tpDist);
            if (ticket > 0)
            {
                openTrades.Add(new TrackedTrader(ticket, TradeSide.Sell, midPrice, slDist, tpDist));
            }
        }

        // ================================
        // ✔ MULTI-TF MOMENTUM (your existing logic)
        // ================================
        public TradeAction CheckMultiTfMomentum(
            double[] emaFast1H, double[] emaSlow1H,
            double[] emaFast4H, double[] emaSlow4H,
            MqlRates[] rates1H)
        {
            int i1 = emaFast1H.Length - 1;
            int i0 = emaFast1H.Length - 2;

            double h1M = emaFast1H[i1] - emaSlow1H[i1];
            double h1Prev = emaFast1H[i0] - emaSlow1H[i0];
            double h1Change = h1M - h1Prev;

            double h4M = emaFast4H[i1] - emaSlow4H[i1];
            double h4Prev = emaFast4H[i0] - emaSlow4H[i0];
            double h4Change = h4M - h4Prev;

            bool h1Bull = h1M > 0;
            bool h1Bear = h1M < 0;
            bool h4Bull = h4M > 0;
            bool h4Bear = h4M < 0;

            if ((h1Bull && h1Change < 0) || (h4Bull && h4Change < 0)) return TradeAction.Hold;
            if ((h1Bear && h1Change > 0) || (h4Bear && h4Change > 0)) return TradeAction.Hold;

            if (h1Bull && h4Bull) return TradeAction.EnterBuy;
            if (h1Bear && h4Bear) return TradeAction.EnterSell;

            return TradeAction.Hold;
        }

        // ================================
        // ✔ ATR
        // ================================
        private double CalculateAtr(MqlRates[] rates, int period)
        {
            if (rates.Length <= period) return 0;

            double sumTr = 0;
            for (int i = rates.Length - period; i < rates.Length; i++)
            {
                double high = rates[i].high;
                double low = rates[i].low;
                double prevClose = rates[i - 1].close;

                double tr = Math.Max(high - low,
                           Math.Max(Math.Abs(high - prevClose),
                                    Math.Abs(low - prevClose)));
                sumTr += tr;
            }
            return sumTr / period;
        }

        // ================================
        // ✔ CHOPPINESS INDEX
        // ================================


        private double CalculateChoppinessIndex(MqlRates[] rates, int period)
        {
            double sumATR = 0;
            double highest = double.MinValue;
            double lowest = double.MaxValue;

            for (int i = rates.Length - period; i < rates.Length; i++)
            {
                double high = rates[i].high;
                double low = rates[i].low;
                double prevClose = rates[i - 1].close;

                double tr = Math.Max(high - low,
                           Math.Max(Math.Abs(high - prevClose),
                                    Math.Abs(low - prevClose)));
                sumATR += tr;

                if (high > highest) highest = high;
                if (low < lowest) lowest = low;
            }

            double range = highest - lowest;
            if (range == 0) return 100;

            return 100 * Math.Log10(sumATR / range) / Math.Log10(period);
        }

        // ================================
        // ✔ FTMO EQUITY GUARD
        // ================================
        private bool CheckEquityGuards(double equity)
        {
            if (startingDayEquity == 0) startingDayEquity = equity;

            double dailyLoss = equity - startingDayEquity;
            if (dailyLoss <= -200) // Example: -$200 soft daily limit
            {
                Console.WriteLine("[GUARD] Daily loss limit hit.");
                tradingDisabled = true;
                return false;
            }

            if (equity <= 9500)  // Example: -5% total
            {
                Console.WriteLine("[GUARD] Hard equity guard hit.");
                tradingDisabled = true;
                return false;
            }

            return true;
        }

        // ================================
        // ✔ POSITION SIZE
        // ================================
        private double CalculatePositionSize(double equity, double riskPercent, double slPriceDistance)
        {
            double riskUsd = equity * (riskPercent / 100.0);
            double rawVol = riskUsd / (slPriceDistance * ContractSize);

            double vol = Math.Floor(rawVol * 100.0) / 100.0;
            return Math.Max(vol, 0.01);
        }

        // ================================
        // ✔ BREAK-EVEN
        // ================================
        private void CheckBreakEven(TrackedTrader t, double mid, double atr)
        {
            double oneR = t.InitialSlDistance;

            if (t.BreakEvenDone) return;

            if (t.Side == TradeSide.Buy && mid - t.EntryPrice >= oneR)
            {
                t.StopLoss = t.EntryPrice + atr * 0.2;
                ModifyTrade(t);
                t.BreakEvenDone = true;
            }
            else if (t.Side == TradeSide.Sell && t.EntryPrice - mid >= oneR)
            {
                t.StopLoss = t.EntryPrice - atr * 0.2;
                ModifyTrade(t);
                t.BreakEvenDone = true;
            }
        }

        // ================================
        // ✔ TIGHTEN STOP LOSS (ATR-based)
        // ================================
        private void TightenStopLossAtr(TrackedTrader t, double mid, double atr)
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

        // ================================
        // ✔ TRAILING STOP
        // ================================
        private void ApplyTrailingStopAtr(TrackedTrader t, double mid, double atr, double bid, double ask)
        {
            double trail = atr * 1.2;

            if (t.Side == TradeSide.Buy)
            {
                double newSL = mid - trail;
                if (newSL > t.StopLoss)
                {
                    t.StopLoss = newSL;
                    ModifyTrade(t);
                }
            }
            else
            {
                double newSL = mid + trail;
                if (newSL < t.StopLoss)
                {
                    t.StopLoss = newSL;
                    ModifyTrade(t);
                }
            }
        }

        // ================================
        // ✔ TIGHTENSTOP TRIGGER
        // ================================
        private bool ShouldTightenStop(MqlRates[] rates, TrackedTrader t)
        {
            // Simple candle weakening logic
            int i = rates.Length - 1;
            bool lastBull = rates[i].close > rates[i].open;
            bool lastBear = rates[i].close < rates[i].open;

            if (t.Side == TradeSide.Buy && lastBear) return true;
            if (t.Side == TradeSide.Sell && lastBull) return true;

            return false;
        }

        // ================================
        // ✔ TRADING WINDOW / EVENING SHUTDOWN
        // ================================
        private bool IsTradingWindow(DateTime now)
        {
            if (now.DayOfWeek == DayOfWeek.Saturday ||
                now.DayOfWeek == DayOfWeek.Sunday)
                return false;

            // Example: only trade 2am–6pm
            TimeSpan t = now.TimeOfDay;
            return t >= TimeSpan.FromHours(2) && t <= TimeSpan.FromHours(18);
        }

        private bool IsEveningShutdown(DateTime now)
        {
            TimeSpan t = now.TimeOfDay;
            return t >= TimeSpan.FromHours(18);
        }

        // ================================
        // ✔ TRADE MODIFICATION & DETECTION
        // ================================
        private void ModifyTrade(TrackedTrader t)
        {
            Console.WriteLine($"[MODIFY] SL updated → {t.StopLoss:F2}");
        }

        private bool IsTradeClosedByBroker(TrackedTrader t)
        {
            // Placeholder — implement MT5 check
            return false;
        }

        private long OpenBuy(double vol, double price, double slDist, double tpDist)
        {
            Console.WriteLine($"[MT5] BUY order sent.");
            return DateTime.Now.Ticks; // Fake ticket
        }

        private long OpenSell(double vol, double price, double slDist, double tpDist)
        {
            ENUM_ORDER_TYPE_FILLING filling = ENUM_ORDER_TYPE_FILLING.ORDER_FILLING_IOC;

            // compute stop loss and take profit in absolute price units
            double slPrice = price - StopLossPoints * 0.1;
            double tpPrice = price + TakeProfitPoints * 0.1;
            double slDistancePrice = Math.Abs(price - slPrice); // in USD

            double rawVolume = RiskPerTradeUsd / (slDistancePrice * ContractSize);

            // Minimum lot for gold is 0.01
            double volume = Math.Max(0.01, Math.Round(rawVolume, 2));

            var request = new MqlTradeRequest
            {
                Action = ENUM_TRADE_REQUEST_ACTIONS.TRADE_ACTION_DEAL,
                Symbol = "XAUUSD",
                Volume = volume,
                Type = ENUM_ORDER_TYPE.ORDER_TYPE_SELL,
                Price = price,
                Sl = slPrice,
                Tp = tpPrice,
                Deviation = 100,
                Type_filling = filling,
                Magic = (ulong)MAGIC_NUMBER,
                Comment = "EMA closed-candle"
            };

            Console.WriteLine($"[MT5] SELL order sent.");
            return DateTime.Now.Ticks; // Fake ticket
        }

        async Task streamMessage<T>(string messageType, string message, T data)
        {
            Results<T> results = new Results<T>();
            results.messageType = messageType;
            results.message = message;
            results.Data = data;
            results.status = 200;
            await _onMessageReceived(results);
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

        private bool IsNewCandleClosed(MqlRates[] rates)
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
    }
}
