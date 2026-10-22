using MetaTrader.Data.Database;
using MetaTrader.Services;
using MetaTrader.Services.Contract;
using MetaTrader.Services.Helper;
using MetaTrader.Services.Interfaces;
using MetaTrader.Util;
using MetaTrader.Util.Trading.Execution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MtApi5;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MetaTrader
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // 🔥 FIRST-CHANCE EXCEPTION HOOK (GLOBAL)
            AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
            {
                if (e.Exception is NullReferenceException)
                {
                    LogFile.Write($"First chance NullReferenceException {e.Exception.Message}");
                }
            };
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                }).ConfigureServices((hostContext, services) =>
                {
                    services.AddMemoryCache();
                    services.AddSingleton<TradeStateStore>();
                    services.AddSingleton<TradeCoordinator>();
                    services.AddSingleton<INotificationService, NotificationService>();
                    services.AddScoped<IMetaTraderConnectionService, MetaTraderConnectionService>();
                    services.AddSingleton<IBotBroadcaster, BotBroadcaster>();
                    services.AddHostedService<MetaTraderConnectionService>();
                    services.AddScoped<ITradingDbContext, DatabaseContext>();
                    services.AddSingleton<MtApi5Client>();
                    services.AddSingleton<TradeServiceEngine>();
                    services.AddSingleton<M15ConfirmationService>();
                    services.AddSingleton<PositionManager>();

                    services.AddSingleton<TimeframeDiversityExecutor>(sp =>
                    {
                        // DI gives you the SAME MtApi5Client instance
                        var client = sp.GetRequiredService<MtApi5Client>();

                        return new TimeframeDiversityExecutor(
                            new TimeframeDiversityConfig()
                        );
                    });


                });

    }
}
