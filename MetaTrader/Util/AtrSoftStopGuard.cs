using MetaTrader.Data.Entities;
using System;

namespace MetaTrader.Util
{
    public static class AtrSoftStopGuard
    {
        public const int MinSecondsInTrade = 60; // 1 minute

        public static bool IsEligible(TrackedTrade t)
        {
            return (DateTime.UtcNow - t.OpenTime).TotalSeconds >= 60;
        }
    }

}
