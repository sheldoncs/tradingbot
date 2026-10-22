namespace MetaTrader.Services.Diagnostic.Test
{

    using System;
    using MtApi5;

    public static class Mt5BrokerRiskDiagnostic
    {
        public static void Run(MtApi5Client client)
        {
            string[] symbols =
            {
            "XAUUSD",
            "XAUEUR",
            "EURUSD",
            "GBPUSD",
            "USDJPY",
            "EURJPY",
            "USDCAD",
            "AUDUSD",
            "XRPUSD",
            "BTCUSD",
            "ETHUSD",
            "SOLUSD",
            "BNBUSD",
            "ADAUSD",
            "US100.cash"
        };

            foreach (string symbol in symbols)
            {
                try
                {
                    double tickSize = client.SymbolInfoDouble(
                        symbol,
                        ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_TRADE_TICK_SIZE);

                    double tickValue = client.SymbolInfoDouble(
                        symbol,
                        ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_TRADE_TICK_VALUE);

                    double tickValueProfit = client.SymbolInfoDouble(
                        symbol,
                        ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_TRADE_TICK_VALUE_PROFIT);

                    double contractSize = client.SymbolInfoDouble(
                        symbol,
                        ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_TRADE_CONTRACT_SIZE);

                    double minLot = client.SymbolInfoDouble(
                        symbol,
                        ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_VOLUME_MIN);

                    double volumeStep = client.SymbolInfoDouble(
                        symbol,
                        ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_VOLUME_STEP);

                    double bid = client.SymbolInfoDouble(
                        symbol,
                        ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);

                    double ask = client.SymbolInfoDouble(
                        symbol,
                        ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);

                    double lot = minLot;

                    // Hypothetical stop: 100 ticks away.
                    // No trade is submitted.
                    double stopDistance = tickSize * 100.0;

                    double estimatedBuyLoss =
                        tickSize > 0 && tickValue > 0
                            ? (stopDistance / tickSize)
                                * tickValue * lot
                            : double.NaN;

                    string report =
                        $"\n========== {symbol} ==========\n" +
                        $"TickSize: {tickSize:G17}\n" +
                        $"TickValue: {tickValue:G17}\n" +
                        $"TickValueProfit: {tickValueProfit:G17}\n" +
                        $"ContractSize: {contractSize:G17}\n" +
                        $"MinLot: {minLot:G17}\n" +
                        $"VolumeStep: {volumeStep:G17}\n" +
                        $"Bid: {bid:G17}\n" +
                        $"Ask: {ask:G17}\n" +
                        $"HypotheticalBuyEntry: {ask:G17}\n" +
                        $"HypotheticalBuySL: {(ask - stopDistance):G17}\n" +
                        $"EstimatedBuyLoss: {estimatedBuyLoss:F4}\n";

                    Console.WriteLine(report);
                    System.Diagnostics.Debug.WriteLine(report);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[{symbol}] Diagnostic error: {ex.Message}");
                }
            }
            System.Diagnostics.Debug.WriteLine("Broker risk diagnostic completed.");
        }
    }

}
