namespace MetaTrader.Data.Entities
{
    public sealed class VelocityDecision
    {
        public bool ShouldExit { get; }
        public bool InGrace { get; }
        public string Reason { get; }

        private VelocityDecision(bool exit, bool grace, string reason)
        {
            ShouldExit = exit;
            InGrace = grace;
            Reason = reason;
        }

        public static VelocityDecision Hold(bool inGrace = false)
            => new VelocityDecision(false, inGrace, "Hold");

        public static VelocityDecision Exit(string reason)
            => new VelocityDecision(true, false, reason);
    }
}
