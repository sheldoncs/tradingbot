using MetaTrader.Enums;

namespace MetaTrader.Util
{
    public class FilterResult
    {
        public bool Blocked { get; set; }
        public string Reason { get; set; }
        public TradeAction Action { get; set; } = TradeAction.Continue;

        public static FilterResult Allow() => new FilterResult { Blocked = false };
        public static FilterResult Block(string reason) =>
            new FilterResult { Blocked = true, Reason = reason, Action = TradeAction.Hold };
    }
}
