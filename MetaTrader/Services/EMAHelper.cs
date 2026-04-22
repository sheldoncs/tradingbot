namespace MetaTrader.Services
{
    public class EMAHelper
    {
        private readonly int _period;
        private double _ema;

        public EMAHelper(int period)
        {
            _period = period;
            _ema = 0;
        }

        public double Update(double price)
        {
            if (_ema == 0)
                _ema = price;
            else
            {
                double k = 2.0 / (_period + 1);
                _ema = price * k + _ema * (1 - k);
            }
            return _ema;
        }
    }
}
