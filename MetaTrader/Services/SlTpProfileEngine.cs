namespace MetaTrader.Services
{
    using System;

    namespace MetaTrader.Services
    {
        public enum AssetClass
        {
            Gold,
            Fx,
            Crypto,
            Index,
            Unknown
        }

        // 🔥 NEW
        public enum TradeType
        {
            Breakout,
            Continuation,
            None
        }

        public class SlTpProfile
        {
            public bool UseAtrBased { get; set; }

            public int FixedStopLossPoints { get; set; }
            public int FixedTakeProfitPoints { get; set; }

            public double StopLossAtrMultiplier { get; set; }
            public double TakeProfitAtrMultiplier { get; set; }

            public int MinStopLossPoints { get; set; }
            public int MinTakeProfitPoints { get; set; }

            public int MaxStopLossPoints { get; set; }
            public int MaxTakeProfitPoints { get; set; }
        }

        public class SlTpResult
        {
            public int StopLossPoints { get; set; }
            public int TakeProfitPoints { get; set; }

            public double StopLossPriceDistance { get; set; }
            public double TakeProfitPriceDistance { get; set; }

            public override string ToString()
            {
                return $"SL={StopLossPoints} pts, TP={TakeProfitPoints} pts";
            }
        }

        public static class SlTpProfileEngine
        {
            // -----------------------------
            // DEFAULT PROFILES
            // -----------------------------
            private static readonly SlTpProfile GoldProfile = new SlTpProfile
            {
                UseAtrBased = true,
                StopLossAtrMultiplier = 1.2,
                TakeProfitAtrMultiplier = 2.0,
                MinStopLossPoints = 250,
                MinTakeProfitPoints = 400,
                MaxStopLossPoints = 1200,
                MaxTakeProfitPoints = 2500,
                FixedStopLossPoints = 500,
                FixedTakeProfitPoints = 800
            };

            private static readonly SlTpProfile FxProfile = new SlTpProfile
            {
                UseAtrBased = true,
                StopLossAtrMultiplier = 1.0,
                TakeProfitAtrMultiplier = 1.8,
                MinStopLossPoints = 80,
                MinTakeProfitPoints = 140,
                MaxStopLossPoints = 500,
                MaxTakeProfitPoints = 1000,
                FixedStopLossPoints = 150,
                FixedTakeProfitPoints = 300
            };

            private static readonly SlTpProfile CryptoProfile = new SlTpProfile
            {
                UseAtrBased = true,
                StopLossAtrMultiplier = 1.5,
                TakeProfitAtrMultiplier = 2.5,
                MinStopLossPoints = 300,
                MinTakeProfitPoints = 600,
                MaxStopLossPoints = 3000,
                MaxTakeProfitPoints = 6000,
                FixedStopLossPoints = 800,
                FixedTakeProfitPoints = 1600
            };

            private static readonly SlTpProfile IndexProfile = new SlTpProfile
            {
                UseAtrBased = true,
                StopLossAtrMultiplier = 1.2,
                TakeProfitAtrMultiplier = 2.0,
                MinStopLossPoints = 150,
                MinTakeProfitPoints = 300,
                MaxStopLossPoints = 2000,
                MaxTakeProfitPoints = 4000,
                FixedStopLossPoints = 400,
                FixedTakeProfitPoints = 900
            };

            // -----------------------------
            // PUBLIC API
            // -----------------------------
            public static SlTpResult GetSlTp(
                string symbol,
                double atrPrice,
                double pointSize,
                TradeType tradeType) // 🔥 NEW PARAM
            {
                var assetClass = DetectAssetClass(symbol);
                var profile = GetProfile(assetClass);

                if (pointSize <= 0)
                    throw new ArgumentException("pointSize must be > 0");

                if (profile.UseAtrBased && atrPrice <= 0)
                    throw new ArgumentException("atrPrice must be > 0 when ATR-based SL/TP is enabled");

                int slPoints;
                int tpPoints;

                // 🔥 ADJUST MULTIPLIERS BASED ON TRADE TYPE
                double slMultiplier = profile.StopLossAtrMultiplier;
                double tpMultiplier = profile.TakeProfitAtrMultiplier;

                if (tradeType == TradeType.Continuation)
                {   
                    double continuationFactor =
                    IsGold(symbol) ? 1.4 :
                    IsFx(symbol) ? 1.2 :
                    IsIndex(symbol) ? 1.5 :
                    IsCrypto(symbol) ? 1.7 :
                    1.3;

                    slMultiplier *= continuationFactor;
                    tpMultiplier *= 1.2;
                }

                if (tradeType == TradeType.Breakout)
                {
                    double earlyFactor =
                        IsGold(symbol) ? 1.2 :
                        IsFx(symbol) ? 1.1 :
                        IsIndex(symbol) ? 1.25 :
                        IsCrypto(symbol) ? 1.3 :
                        1.15;

                    slMultiplier *= earlyFactor;

                    // let winners run a bit more
                    tpMultiplier *= 1.1;
                }

                if (profile.UseAtrBased)
                {
                    double slPriceDistance = atrPrice * slMultiplier;
                    double tpPriceDistance = atrPrice * tpMultiplier;

                    slPoints = (int)Math.Round(slPriceDistance / pointSize);
                    tpPoints = (int)Math.Round(tpPriceDistance / pointSize);
                }
                else
                {
                    slPoints = profile.FixedStopLossPoints;
                    tpPoints = profile.FixedTakeProfitPoints;
                }

                slPoints = ApplyFloorAndCap(
                    slPoints,
                    profile.MinStopLossPoints,
                    profile.MaxStopLossPoints);

                tpPoints = ApplyFloorAndCap(
                    tpPoints,
                    profile.MinTakeProfitPoints,
                    profile.MaxTakeProfitPoints);

                return new SlTpResult
                {
                    StopLossPoints = slPoints,
                    TakeProfitPoints = tpPoints,
                    StopLossPriceDistance = slPoints * pointSize,
                    TakeProfitPriceDistance = tpPoints * pointSize
                };
            }

            public static AssetClass DetectAssetClass(string symbol)
            {
                if (string.IsNullOrWhiteSpace(symbol))
                    return AssetClass.Unknown;

                symbol = symbol.ToUpperInvariant();

                if (IsGold(symbol)) return AssetClass.Gold;
                if (IsCrypto(symbol)) return AssetClass.Crypto;
                if (IsIndex(symbol)) return AssetClass.Index;
                if (IsFx(symbol)) return AssetClass.Fx;

                return AssetClass.Unknown;
            }

            public static SlTpProfile GetProfile(AssetClass assetClass)
            {
                return assetClass switch
                {
                    AssetClass.Gold => GoldProfile,
                    AssetClass.Fx => FxProfile,
                    AssetClass.Crypto => CryptoProfile,
                    AssetClass.Index => IndexProfile,
                    _ => FxProfile
                };
            }

            private static int ApplyFloorAndCap(int value, int min, int max)
            {
                if (min > 0 && value < min) value = min;
                if (max > 0 && value > max) value = max;
                return value;
            }

            private static bool IsGold(string symbol)
            {
                return symbol.Contains("XAU");
            }

            private static bool IsCrypto(string symbol)
            {
                return symbol.Contains("BTC") ||
                       symbol.Contains("ETH") ||
                       symbol.Contains("SOL") ||
                       symbol.Contains("XRP") ||
                       symbol.Contains("BNB") ||
                       symbol.Contains("ADA") ||
                       symbol.Contains("DOGE") ||
                       symbol.Contains("LTC");
            }

            private static bool IsIndex(string symbol)
            {
                return symbol.Contains("US100") ||
                       symbol.Contains("NAS") ||
                       symbol.Contains("SPX") ||
                       symbol.Contains("US30") ||
                       symbol.Contains("DJI") ||
                       symbol.Contains("GER40") ||
                       symbol.Contains("UK100") ||
                       symbol.Contains("JP225") ||
                       symbol.Contains("INDEX") ||
                       symbol.Contains(".CASH");
            }

            private static bool IsFx(string symbol)
            {
                if (symbol.Length < 6)
                    return false;

                string[] ccy = { "USD", "EUR", "GBP", "JPY", "AUD", "NZD", "CAD", "CHF" };

                foreach (var baseCcy in ccy)
                {
                    foreach (var quoteCcy in ccy)
                    {
                        if (baseCcy == quoteCcy)
                            continue;

                        if (symbol.StartsWith(baseCcy) && symbol.Contains(quoteCcy))
                            return true;
                    }
                }

                return false;
            }
        }
      }
    }
