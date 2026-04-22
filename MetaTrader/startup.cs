using chatgptbot.Util;
using MetaTrader.Data.Database;
using MetaTrader.Services;
using MetaTrader.Services.Contract;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingApplication.Configuration;
using ILogger = Serilog.ILogger;

namespace MetaTrader
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {

            using var logger = new LoggerConfiguration()
             .ReadFrom.Configuration(Configuration)
             .CreateLogger();

            ConfigureCorsPolicies(services, Configuration, logger);

            services.AddAutoMapper(AppDomain.CurrentDomain.GetAssemblies());
            ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.AddDebug();
            });

            ILogger<DatabaseConfiguration> mylogger = loggerFactory.CreateLogger<DatabaseConfiguration>();
            DatabaseConfiguration dConfig = new DatabaseConfiguration(Configuration, mylogger);
            var connectionString = dConfig.MySQLConnectString();

            services.AddDbContext<DatabaseContext>(options =>
            {
                options.UseMySql(connectionString,
                 new MySqlServerVersion(new Version(10, 1, 40)),
                 mySqlOptions => mySqlOptions.EnableRetryOnFailure());
            });
            services.AddControllers();
            services.AddSignalR();
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "FATCA", Version = "v1" });
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseSwagger();
                app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "adverse v1"));
            }
            app.UseCors("AllowReactApp"); 
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapHub<ChatHub>("/chatHub");
            });
        }

        private static void ConfigureCorsPolicies(IServiceCollection services, IConfiguration configuration,
           ILogger logger)
        {
            logger.Information("Configuring CORS policies");
            var userEndpointPolicyConfig = new UserEndpointCorsPolicyConfiguration(configuration, logger);

            services.AddCors(userEndpointPolicyConfig.SetupAction);

            logger.Information("CORS policies configured");
        }
    }
}
