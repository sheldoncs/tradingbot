using AutoMapper;
using MetaTrader.Data.Entities;
using MetaTrader.Dto;

namespace MetaTrader.Profiles
{
    public class TradingPairProfile : Profile
    {
        public TradingPairProfile() {
            CreateMap<TradingPairsDto, TradingPairs>().ReverseMap();
            CreateMap<KryptoLabelsDto, KryptoLabels>().ReverseMap();
        }
    }
}
