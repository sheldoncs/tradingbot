using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Serilog;
using System;


namespace TradingApplication.Configuration
{
    public class UserEndpointCorsPolicyConfiguration
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger _logger;

        public Action<CorsOptions> SetupAction;

        public UserEndpointCorsPolicyConfiguration(IConfiguration configuration, ILogger logger)
        {
            _configuration = configuration;
            _logger = logger;
            CorsOptions opt = new CorsOptions();
            _logger.Information("Setup cors policy");
            SetupAction = new Action<CorsOptions>(options =>
                                options.AddPolicy("AllowReactApp",
                        builder => builder.
                            AllowAnyMethod().AllowCredentials().AllowAnyHeader().WithOrigins(
                                                                         "http://localhost:3000",
                                                                         "http://localhost:4000",
                                                                         "http://localhost:8000",
                                                                         "http://localhost:7000",
                                                                         "http://localhost:3001",
                                                                         "http://localhost:3004",
                                                                         "http://localhost:44337",
                                                                         "https://cavehillmaps.cavehill.uwi.edu",
                                                                         "http://owl4:8080"
                                                                           )
                               )
            );

        }
    }
}
