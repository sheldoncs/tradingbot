namespace MetaTrader.Enums
{
    public enum DecisionReason
    {
        // Regime
        Compression,
        Exhaustion,
        TransitionNotConfirmed,
        NotFirstContinuation,

        // Momentum
        MomentumWeak,
        LateMomentum,
        RangeFatigue,
        LateExhaustion,

        // Structure
        SwingHighBlocked,
        SwingLowBlocked,
        AcceptanceMissing,
        EntryBlocked,

        // Liquidity
        LiquiditySweep,
        LiquidityGrabNoAcceptance,

        // Risk / Safety
        NewsBlackout,
        ReentryBlocked,
        CooldownActive,
        H4WeakNoOverride,

        // Overrides
        EarlyMomentumOverride,
        MomentumOverride,

        // Entries
        SniperEntry,
        AggressiveFallbackBuy,
        AggressiveFallbackSell,

        // State
        EntryLocked,
        NoSignal,

        // Traps
        ResistanceTrap,
        SupportTrap,

        DoNothing,
        TradeNoLongerReady
    }

}
