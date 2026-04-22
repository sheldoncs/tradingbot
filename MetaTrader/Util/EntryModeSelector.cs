using MetaTrader.Enums;

namespace MetaTrader.Util
{
    public static class EntryModeSelector
    {
        public static EntryMode SelectMode(
            double atrRaw,
            double atrAverage,
            TrendContext trend,
            bool h4Weak,
            bool liquiditySweep,
            bool nearOrderBlock
        )
        {
            // -------------------------------------------------------
            // 1️⃣ Smart Money Mode (highest safety)
            // -------------------------------------------------------
            if (liquiditySweep || nearOrderBlock)
            {
                return EntryMode.SmartMoney;
            }

            // -------------------------------------------------------
            // 2️⃣ Hybrid Mode (best balance)
            // -------------------------------------------------------
            if (atrRaw > atrAverage * 1.2 || h4Weak)
            {
                return EntryMode.Hybrid;
            }

            // -------------------------------------------------------
            // 3️⃣ Standard Mode (trend + momentum)
            // -------------------------------------------------------
            return EntryMode.Standard;
        }
    }

}
