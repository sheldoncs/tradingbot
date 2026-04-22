using AutoMapper;
using MetaTrader.Data.Database;
using MetaTrader.Data.Entities;
using MetaTrader.Dto;
using MetaTrader.Util;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TradingApplication.Exceptions;

namespace MetaTrader.Data.Database
{
    public partial class DatabaseContext : DbContext, ITradingDbContext
    {

      
        public DbSet<TelegramProperties> Telegram { get; set; }
        public DbSet<TradingPairs> Tradingpairs { get; set; }
        public DbSet<ApiToken> Apitoken { get; set; }
        public DbSet<Diagnostic> Diagnostic { get; set; }
        public DbSet<KryptoLabels> kryptolabels { get; set; }
        private readonly IMapper _mapper;
        private readonly IConfiguration _configuration;
        private readonly ILogger<DatabaseContext> _logger;
        private const string LoggerScope = nameof(DatabaseContext);

        private readonly IDictionary<string, string> tokens = new Dictionary<string, string>();
        public IDictionary<string, string> Tokens => tokens;

        public DatabaseContext(ILogger<DatabaseContext> logger, DbContextOptions<DatabaseContext> opt, IMapper mapper, IConfiguration configuration) : base(opt)
        {
            _mapper = mapper;
            _configuration = configuration;
            _logger = logger;
        }
        public TData AddEntry<TData>(TData data, Boolean saveType)
        {
            if (saveType)
            {
                this.Add(data);
            }
            else
            {
                this.Update(data);
            }

            base.SaveChanges();

            return data;
        }
        
        public async Task<ApiToken> getApiTokenRecord(string token)
        {

            var t = Apitoken.Select(s=>s).Where(f => f.token.Equals(token)).FirstOrDefault();

            return await Task.FromResult(t);

        }
        public async Task<TradingPairsDto> GetTradingPairsAsync (string pair)
        {
            TradingPairs tradingPair = Tradingpairs.Select(s => s).Where(f => f.pair.Equals(pair)).FirstOrDefault();
            TradingPairsDto dto = _mapper.Map<TradingPairs, TradingPairsDto>(tradingPair);
            return await Task.FromResult(dto);
        }


        public async Task<List<TelegramProperties>> getTelegramProperties()
        {
            List <TelegramProperties> properties = Telegram.Select(s => s).ToList();
            return await Task.FromResult(properties);
        }

        public async Task<List<KryptoLabelsDto>> GetKryptoLabelsAsync()
        {
            try
            {
                List<KryptoLabels> labels = kryptolabels.Select(s => s).ToList();
                List<KryptoLabelsDto> list = _mapper.Map<List<KryptoLabels>, List<KryptoLabelsDto>>(labels);
                return await Task.FromResult(list);
            } catch(Exception ex)
            {
                LogFile.Write($"GetKryptoLabelsAsync crashed: {ex.Message}\n{ex.StackTrace}");
                throw new KryptoException(ex.Message);
            }
        }
    }
}
