using MetaTrader.Data.Entities;
using MetaTrader.Dto;
using MetaTrader.Enums;
using MetaTrader.helper;
using MetaTrader.Services.Interfaces;
using MetaTrader.Util;
using Microsoft.Extensions.Configuration;
using MtApi5;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;

namespace MetaTrader.Services
{
    public static class OpenTradeService
    {
        private const double fixedLotSize = 0.01;
        private const double maxAllowedLot = 0.05;
        static List<TrackedTrade> trades = new();
        const double StopLossPoints = 500.0;
        public static Task<List<TrackedTrade>> OpenTrade(TradeSide side, 
            double price, LotSizingMode sizingMode, 
            string SYMBOL, 
             TradingPairsDto tradingPairsDto,
              INotificationService _notificationService,
              IConfiguration _configuration)
        {
            
            // compute stop loss and take profit in absolute price units
            decimal slPrice = side == TradeSide.Buy ? (decimal)price - (decimal)tradingPairsDto.stoplosspoints * tradingPairsDto.pipvalue : (decimal)price + (decimal)StopLossPoints * tradingPairsDto.pipvalue;
            decimal tpPrice = side == TradeSide.Buy ? (decimal)price + (decimal)tradingPairsDto.takeprofitpoints * tradingPairsDto.pipvalue : (decimal)price - tradingPairsDto.takeprofitpoints * tradingPairsDto.pipvalue;

            // dynamic lot sizing: RiskPerTradeUsd / (SL distance in USD per standard lot)
            double slDistancePrice = Math.Abs(price - (double)slPrice); // in USD
            if (slDistancePrice <= 0)
            {
                Console.WriteLine("Invalid stop-loss distance. Aborting order.");
                LogFile.Write("Invalid stop-loss distance. Aborting order.");
                return Task.FromResult(trades);
            }

            decimal rawVolume = (decimal)tradingPairsDto.riskpertrade / ((decimal)slDistancePrice * tradingPairsDto.contractsize);
            double baseLot = (double)Math.Max(0.01m, Math.Round(rawVolume, 2));
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
                return Task.FromResult(trades);
            }

            LogFile.Write($"Pre Order values. Side={side}, Volume={finalLot}, Entry={price}, SL={slPrice}, TP={tpPrice}");
            Console.WriteLine($"Pre Order values. Side={side}, Volume={finalLot}, Entry={price}, SL={slPrice}, TP={tpPrice}");

            SignalStore.Set(TokenService.accountNo, new TradeInstruction
            {
                Action = side == TradeSide.Buy ? "BUY": "SELL",
                Lot = finalLot,
                SlPoints = (double)slPrice,
                TpPoints = (double)tpPrice,
                Magic = TokenService.Magic,
                CreatedUtc = DateTime.UtcNow,
                Comment = "SFX_FreshExpansion"
            });


            var tracked = new TrackedTrade
            {
                    Ticket = (ulong)TokenService.Magic,
                    Symbol = SYMBOL,
                    Side = side,
                    OpenTime = DateTime.UtcNow,
                    ContractSize = tradingPairsDto.contractsize,
                    EntryPrice = price,
                    Volume = (double)finalLot,
                    StopLoss = (double)slPrice,
                    IsClosed = false,
                    TakeProfit = (double)tpPrice,
                    PeakOrTrough = price // initialize peak/trough to entry price
            };
            trades.Add(tracked);
            
            var trade = new Trade
            {
                Ticket = (ulong)TokenService.Magic,
                Symbol = SYMBOL,
                Side = side.ToString(),
                OpenTime = DateTime.UtcNow,
                ContractSize = tradingPairsDto.contractsize,
                EntryPrice = price,
                Volume = (double)finalLot,
                StopLoss = (double)slPrice,
                IsClosed = false,
                TakeProfit = (double)tpPrice,
                PeakOrTrough = price // initialize peak/trough to entry price
            };

            string whichSide = side switch
            {
                TradeSide.Buy => "BUY",
                TradeSide.Sell => "SELL",
                _ => "UNKNOWN"
            };

            TradeInstruction tradeInstruction = new TradeInstruction()
            {
                Action = whichSide,
                Lot = 0.01,
                Magic = TokenService.Magic,
                SlPoints = trades.Select(t => t).FirstOrDefault().StopLoss,
                TpPoints = trades.Select(t => t).FirstOrDefault().TakeProfit,
                Comment = $"{whichSide} Trade"
            };
            TokenService.setInstruction(tradeInstruction);

            TradingEventBus.ActiveTrade(trade);

            return Task.FromResult(trades); 
            }        
     }
 }

