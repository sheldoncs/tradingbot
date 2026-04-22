namespace MetaTrader.Util
{
    public class TradeCoordinator
    {
        public string ArmedSymbol { get; private set; }

        public bool HasArmedSymbol => !string.IsNullOrEmpty(ArmedSymbol);
        public bool CanArm(string symbol)
            => ArmedSymbol == null || ArmedSymbol == symbol;

        public void SetArmed(string symbol)
            => ArmedSymbol = symbol;

        public void Clear()
            => ArmedSymbol = null;
    }

}
