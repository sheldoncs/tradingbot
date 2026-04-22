using MetaTrader.Data.Entities;
using MetaTrader.Enums;

namespace MetaTrader.Services
{
    public static class ExpansionFreshnessProfiles
    {
        public static readonly ExpansionFreshnessConfig Index = new()
        {
            // US100 expands aggressively, allow slightly more
            MaxSingleCandleAtr = 1.1,
            MaxTwoCandleAtr = 1.8
        };

        public static readonly ExpansionFreshnessConfig FX = new()
        {
            // FX trends cleaner, but reverses quicker
            MaxSingleCandleAtr = 0.9,
            MaxTwoCandleAtr = 1.4
        };

        public static readonly ExpansionFreshnessConfig Gold = new()
        {
            // Gold volatile but often mean-reverts
            MaxSingleCandleAtr = 1.0,
            MaxTwoCandleAtr = 1.6
        };

        public static readonly ExpansionFreshnessConfig Crypto = new()
        {
            // Crypto has violent spikes — be stricter
            MaxSingleCandleAtr = 0.85,
            MaxTwoCandleAtr = 1.3
        };

        public static ExpansionFreshnessConfig For(VelocityProfile profile)
            => profile switch
            {
                VelocityProfile.Index => Index,
                VelocityProfile.FX => FX,
                VelocityProfile.Gold => Gold,
                VelocityProfile.Crypto => Crypto,
                _ => Gold
            };
    }
}
