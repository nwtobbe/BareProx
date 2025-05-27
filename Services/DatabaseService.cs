using Microsoft.Extensions.Configuration;

namespace BareProx.Services
{
    public class DatabaseService
    {
        private readonly IConfiguration _configuration;

        public DatabaseService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public string GetConnectionString()
        {
            string dbServer = _configuration["Database:DbServer"];
            string dbName = _configuration["Database:DbName"];
            string dbUser = _configuration["Database:DbUser"];
            string dbPassword = _configuration["Database:DbPassword"];

            return $"Server={dbServer};Database={dbName};User Id={dbUser};Password={dbPassword};";
        }
    }
    public class DatabaseConfig
    {
        public string DbServer { get; set; }
        public string DbName { get; set; }
        public string DbUser { get; set; }
        public string DbPassword { get; set; }
    }
}
