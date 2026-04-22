using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TradingApplication.Configuration
{
    public class DatabaseConfiguration
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<DatabaseConfiguration> _logger;

        public DatabaseConfiguration(IConfiguration configuration, ILogger<DatabaseConfiguration> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public DatabaseConfiguration(IConfiguration configuration)
        {
            _configuration = configuration;
        }


        public string MySQLConnectString()
        {
            var host = _configuration.GetValue<string>("MYSQL_DBHOST") ?? "localhost";
            var port = _configuration.GetValue<string>("MYSQL_DBPORT") ?? "3306";
            var password = _configuration.GetValue<string>("MYSQL_PASSWORD") ?? _configuration.GetConnectionString("MYSQL_PASSWORD");
            var userid = _configuration.GetValue<string>("MYSQL_USER") ?? _configuration.GetConnectionString("MYSQL_USER");
            var usersDataBase = _configuration.GetValue<string>("MYSQL_DATABASE") ?? _configuration.GetConnectionString("MYSQL_DATABASE");
            var connString = $"server={host};port={port};Database={usersDataBase};userid={userid};password={password};";
            
            return connString;
        }

        public string OracleConnectionString()
        {
            //"Data Source=hermes.cavehill.uwi.edu/HRPRD11;User ID=admin;Password=proxyfield;Unicode=True"
            var dataSource = _configuration.GetValue<string>("ORACLE_DATASOURCE");
            var userId = _configuration.GetValue<string>("ORACLE_USERID");
            var password = _configuration.GetValue<string>("ORACLE_PASSWORD");
            var port = _configuration.GetValue<string>("ORACLE_DBPORT");
            var connectData = _configuration.GetValue<string>("ORACLE_ENVIRONMENT");
            //var connString = $"Data Source={dataSource};ID={userId};Password={password};Unicode=True";
            var connString = $"User Id={userId};Password={password};Data Source={dataSource}:{port}/{connectData};Validate Connection = true;";
            return connString;
        }
    }
}
