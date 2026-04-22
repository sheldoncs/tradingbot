using MetaTrader.Data.Entities;
using MetaTrader.Util;

namespace MetaTrader.Services
{

    public static class TokenService
    {
        private static int TradeMagic = 90000;
        public static int Magic => TradeMagic;
        
        private static TradeInstruction tradeInstruction;
        public static TradeInstruction instruction => tradeInstruction;
        private static long account;
        public static long accountNo => account;
        public static void setMagic (int m)
        {
            TradeMagic = m;
        }

        public static void setAccount(long m)
        {
            account = m;
        }
        public static void setInstruction(TradeInstruction instruction)
        {
            tradeInstruction = instruction;
        }

    }

}
