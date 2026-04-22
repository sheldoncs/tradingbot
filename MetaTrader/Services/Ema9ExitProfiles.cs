namespace MetaTrader.Services
{
    public enum EntryExitProfile
    {
        Velocity,
        Sniper,
        Continuation,
        VelocityProven
    }

    public sealed class Ema9ExitConfig
    {
        public bool IgnoreSoftBreachWhenProven { get; init; }
        public double SoftBreachAtr { get; init; }
        public double HardBreachAtr { get; init; }
        public int ConfirmSeconds { get; init; }
        public double HardRangeAtr { get; init; }
        public double HardBodyRatio { get; init; }

        // Optional: sniper can allow one candle “grace” below/above EMA9
        public bool AllowGraceIfNearEma21 { get; init; } = true;
        public double NearEma21Atr { get; init; } = 0.25;
    }

    public static class Ema9ExitProfiles
    {
        // Start defaults — tune later per asset class
        public static readonly Ema9ExitConfig Velocity = new()
        {
            IgnoreSoftBreachWhenProven = false,
            SoftBreachAtr = 0.12,
            HardBreachAtr = 0.25,
            ConfirmSeconds = 20,
            HardRangeAtr = 0.90,
            HardBodyRatio = 0.55,
            AllowGraceIfNearEma21 = false
        };

        public static readonly Ema9ExitConfig VelocityProven = new()
        {
            IgnoreSoftBreachWhenProven = true,   // ⭐ THIS IS THE BIG FIX
            SoftBreachAtr = 0.12,                // irrelevant when ignored
            HardBreachAtr = 0.80,                // only hard failures exit
            ConfirmSeconds = 999,
            HardRangeAtr = 1.10,
            HardBodyRatio = 0.60,
            AllowGraceIfNearEma21 = false
        };

        public static readonly Ema9ExitConfig Sniper = new()
        {
            IgnoreSoftBreachWhenProven = false,
            SoftBreachAtr = 0.18,    // slower exit than velocity
            HardBreachAtr = 0.35,
            ConfirmSeconds = 45,     // longer confirmation
            HardRangeAtr = 1.10,
            HardBodyRatio = 0.60,
            AllowGraceIfNearEma21 = true,
            NearEma21Atr = 0.25
        };

        public static readonly Ema9ExitConfig Continuation = new()
        {
            IgnoreSoftBreachWhenProven = false,
            SoftBreachAtr = 0.15,
            HardBreachAtr = 0.30,
            ConfirmSeconds = 30,
            HardRangeAtr = 1.00,
            HardBodyRatio = 0.58,
            AllowGraceIfNearEma21 = true,
            NearEma21Atr = 0.25
        };

        public static Ema9ExitConfig For(EntryExitProfile profile) =>
            profile switch
            {
                EntryExitProfile.Velocity => Velocity,
                EntryExitProfile.Sniper => Sniper,
                EntryExitProfile.VelocityProven => VelocityProven,
                EntryExitProfile.Continuation => Continuation,
                _ => Velocity
            };
    }
}
