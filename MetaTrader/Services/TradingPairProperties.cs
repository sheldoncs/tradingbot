using MetaTrader.Data.Database;
using MetaTrader.Data.Entities;
using MetaTrader.Dto;
using MetaTrader.Services.Contract;
using MetaTrader.Util;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApplication.Exceptions;

namespace MetaTrader.Services
{
    public class TradingPairProperties : ITradingPairProperties
    {
        private readonly IServiceProvider _provider;
        public TradingPairProperties(IServiceProvider provider)
        {
            _provider = provider;
        }

        public async Task<bool> AddSniperDiagnostics(Diagnostic diagnostic)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            db.AddEntry<Diagnostic>(diagnostic, true);
            return await Task.FromResult(true);

        }
        public async Task<TradingPairsDto> GetTradingPairPropertiesAsync(string pair)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

            TradingPairsDto dto = await db.GetTradingPairsAsync(pair);
            
            return await Task.FromResult(dto);
        }
        public async Task<bool> updateApiToken(EaSignalDto dto)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            ApiToken tk = await db.getApiTokenRecord(dto.Token);
            if (tk != null)
            {
                tk.account = dto.Account;
                tk.isactive = true;
                tk.createdat = DateTime.Now;
                db.AddEntry<ApiToken>(tk, false);
                return true;
            }
            return false;
        } 
        public async Task<KryptoLabelsDto> GetKryptoPairsAsync(string pair)
        {
            try
            {
                using var scope = _provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

                List<KryptoLabelsDto> dtoList = await db.GetKryptoLabelsAsync();

                KryptoLabelsDto label = dtoList.Select(s => s).Where(w => w.label.Equals(pair)).FirstOrDefault();
                return await Task.FromResult(label);
            } catch (Exception ex)
            {
                LogFile.Write($"beginTrading crashed: {ex.Message}\n{ex.StackTrace}");
                throw new KryptoException(ex.Message);
            }
        }

        
    }
}
