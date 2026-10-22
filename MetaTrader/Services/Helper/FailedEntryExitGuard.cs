using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;

namespace MetaTrader.Services.Helper
{
    public static class FailedEntryExitGuard
    {
        public static bool ShouldExit(
            string symbol,
            TradeAction positionSide,
            double bid,
            double ask,
            double ema9,
            double momentum,
            bool momentumRising,
            bool momentumFalling,
            MqlRates liveCandle,
            double atr)
        {
            try
            {
                if (atr <= 0)
                    return false;

                double liveMid =
                    (bid + ask) / 2.0;

                double bodyAtr =
                    Math.Abs(
                        liveMid - liveCandle.open
                    ) / atr;

                bool priceBelowEma9 =
                    liveMid < ema9;

                bool priceAboveEma9 =
                    liveMid > ema9;

                bool bearishLiveCandle =
                    liveMid < liveCandle.open;

                bool bullishLiveCandle =
                    liveMid > liveCandle.open;


                // =====================================================
                // FAILED BUY
                // =====================================================
                //
                // BUY was entered, but:
                // - price loses EMA9
                // - momentum turns down
                // - a meaningful bearish candle develops
                //
                // =====================================================

                if (positionSide == TradeAction.EnterBuy)
                {
                    bool failedBuy =
                        priceBelowEma9 &&
                        momentumFalling &&
                        bearishLiveCandle &&
                        bodyAtr >= 0.60;

                    if (failedBuy)
                    {
                        LogFile.Write(
                            $"[{symbol}] ⛔ FAILED ENTRY EXIT - BUY | " +
                            $"price={liveMid:F5} | " +
                            $"ema9={ema9:F5} | " +
                            $"momentum={momentum:F2} | " +
                            $"rising={momentumRising} | " +
                            $"falling={momentumFalling} | " +
                            $"bodyATR={bodyAtr:F2} | " +
                            $"reason=BUY lost EMA9 with bearish momentum and strong opposite candle"
                        );

                        return true;
                    }
                }


                // =====================================================
                // FAILED SELL
                // =====================================================
                //
                // SELL was entered, but:
                // - price reclaims EMA9
                // - momentum turns up
                // - a meaningful bullish candle develops
                //
                // =====================================================

                if (positionSide == TradeAction.EnterSell)
                {
                    bool failedSell =
                        priceAboveEma9 &&
                        momentumRising &&
                        bullishLiveCandle &&
                        bodyAtr >= 0.60;

                    if (failedSell)
                    {
                        LogFile.Write(
                            $"[{symbol}] ⛔ FAILED ENTRY EXIT - SELL | " +
                            $"price={liveMid:F5} | " +
                            $"ema9={ema9:F5} | " +
                            $"momentum={momentum:F2} | " +
                            $"rising={momentumRising} | " +
                            $"falling={momentumFalling} | " +
                            $"bodyATR={bodyAtr:F2} | " +
                            $"reason=SELL reclaimed EMA9 with bullish momentum and strong opposite candle"
                        );

                        return true;
                    }
                }


                return false;
            }
            catch (Exception ex)
            {
                LogFile.Write(
                    $"[{symbol}] FailedEntryExitGuard error | " +
                    $"message={ex.Message}"
                );

                return false;
            }
        }
    }
}
