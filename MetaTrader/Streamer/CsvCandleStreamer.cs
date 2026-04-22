using MtApi5;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MetaTrader.Streamer
{
    public class CsvCandleStreamer
    {
        public async Task StreamCandlesAsync(string csvPath, Func<MqlRates[], Task> onNewData, int delayMs = 1000)
        {
            var lines = File.ReadAllLines(csvPath); // skip header
            var candles = new List<MqlRates>();
            foreach (var line in lines)
            {
                var parts = line.Split(';', ',', '\t');
                if (parts.Length < 6) continue;

                var candle = new MqlRates
                {
                    mt_time = ((DateTimeOffset)DateTime.Parse(parts[0], CultureInfo.InvariantCulture)).ToUnixTimeSeconds(),
                    open = double.Parse(parts[1], CultureInfo.InvariantCulture),
                    high = double.Parse(parts[2], CultureInfo.InvariantCulture),
                    low = double.Parse(parts[3], CultureInfo.InvariantCulture),
                    close = double.Parse(parts[4], CultureInfo.InvariantCulture),
                    tick_volume = long.Parse(parts[5], CultureInfo.InvariantCulture)
                };

                candles.Add(candle);

                // simulate "live" by sending partial candle history
            }
            await onNewData(candles.ToArray());

            await Task.Delay(delayMs); // wait between updates
        }
    }
}
