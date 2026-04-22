using MetaTrader.Data.Entities;
using MetaTrader.Dto;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MetaTrader.Data.Database
{
    public interface ITradingDbContext : IDisposable
    {

        TData AddEntry<TData>(TData data, Boolean saveType);
        Task<List<TelegramProperties>> getTelegramProperties();
        Task<TradingPairsDto> GetTradingPairsAsync(string pair);
        Task<List<KryptoLabelsDto>> GetKryptoLabelsAsync();
        Task<ApiToken> getApiTokenRecord(string token);


    }
}
