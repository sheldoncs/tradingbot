using MetaTrader.Enums;
using static MetaTrader.Services.HybridMomentumEngine;

namespace MetaTrader.Util
{
    public static class TradePermissionEngine
    {
        public static bool CanEvaluateTrade(
            string symbol,
            SymbolContext ctx,
            MarketState regime,
            MomentumResult momentum,
            bool pullbackSufficient,
            bool priceAccepted,
            bool atrCooldown
        )
        {
            // ---------------------------------------
            // MODE: XAUUSD ONLY
            // ---------------------------------------
            if (TradingConfig.Mode == TradingMode.XauUsdOnly)
            {
                if (symbol != "XAUUSD.s")
                    return false;

                return InternalPermission(
                    regime,
                    momentum,
                    pullbackSufficient,
                    priceAccepted,
                    atrCooldown
                );
            }

            // ---------------------------------------
            // MODE: MULTI-SYMBOL (ARMED)
            // ---------------------------------------
            if (TradingConfig.Mode == TradingMode.MultiSymbolArmed)
            {
                if (!ctx.IsArmed)
                    return false;

                return InternalPermission(
                    regime,
                    momentum,
                    pullbackSufficient,
                    priceAccepted,
                    atrCooldown
                );
            }

            return false;
        }

        // ---------------------------------------
        // SHARED GATING LOGIC
        // ---------------------------------------
        private static bool InternalPermission(
            MarketState regime,
            MomentumResult momentum,
            bool pullbackSufficient,
            bool priceAccepted,
            bool atrCooldown
        )
        {
            if (regime != MarketState.Impulse)
                return false;

            if (!pullbackSufficient)
                return false;

            if (!priceAccepted)
                return false;

            if (atrCooldown)
                return false;

            if (!momentum.Strong && !momentum.Impulse)
                return false;

            return true;
        }
    }

}
