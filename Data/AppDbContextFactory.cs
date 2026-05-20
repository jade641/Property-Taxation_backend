using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;

namespace PropertyTax.API.Data;

public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Missing DefaultConnection connection string.");

        var connectionStringBuilder = new MySqlConnectionStringBuilder(connectionString)
        {
            ConnectionTimeout = 60,
            DefaultCommandTimeout = 60,
            SslMode = MySqlSslMode.Preferred,
        };

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseMySql(
            connectionStringBuilder.ConnectionString,
            new MySqlServerVersion(new Version(8, 0, 36)),
            mySqlOptions =>
            {
                mySqlOptions.EnableRetryOnFailure(
                    5,
                    TimeSpan.FromSeconds(10),
                    null
                );
            });
        optionsBuilder.EnableDetailedErrors();

        return new AppDbContext(optionsBuilder.Options);
    }
}