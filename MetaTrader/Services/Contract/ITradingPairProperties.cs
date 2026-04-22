using MetaTrader.Data.Entities;
using MetaTrader.Dto;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MetaTrader.Services.Contract
{
    public interface ITradingPairProperties
    {
        Task<TradingPairsDto> GetTradingPairPropertiesAsync(string pair);
        Task<KryptoLabelsDto> GetKryptoPairsAsync(string pair);
        Task<bool> updateApiToken(EaSignalDto dto);
        Task<bool> AddSniperDiagnostics(Diagnostic diagnostic);
    }
}
