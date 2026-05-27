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

        var connectionString = ResolveConnectionString(configuration);

        var connectionStringBuilder = new MySqlConnectionStringBuilder(connectionString)
        {
            ConnectionTimeout = 60,
            DefaultCommandTimeout = 60,
            SslMode = MySqlSslMode.Preferred,
        };

        var configuredVersion = configuration["Database:ServerVersion"];
        var databaseEngine = configuration["Database:Engine"];
        ServerVersion serverVersion;

        if (string.IsNullOrWhiteSpace(configuredVersion))
        {
            serverVersion = new MySqlServerVersion(new Version(8, 0, 36));
        }
        else if (!Version.TryParse(configuredVersion, out var parsedVersion))
        {
            throw new InvalidOperationException("Database:ServerVersion must be a valid semantic version such as 8.0.36 or 10.11.15.");
        }
        else
        {
            serverVersion = string.Equals(databaseEngine, "MariaDb", StringComparison.OrdinalIgnoreCase)
                || string.Equals(databaseEngine, "MariaDB", StringComparison.OrdinalIgnoreCase)
                ? new MariaDbServerVersion(parsedVersion)
                : new MySqlServerVersion(parsedVersion);
        }

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseMySql(
            connectionStringBuilder.ConnectionString,
            serverVersion,
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

    private static string ResolveConnectionString(IConfiguration configuration)
    {
        var candidates = new[]
        {
            configuration.GetConnectionString("DefaultConnection"),
            configuration["ConnectionStrings:DefaultConnection"],
            configuration["DefaultConnection"],
            configuration["MYSQL_URL"],
            configuration["MYSQL_INTERNAL_URL"],
            configuration["DATABASE_URL"],
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        var host = FirstNonEmpty(
            configuration["MYSQLHOST"],
            configuration["MYSQL_HOST"],
            configuration["DB_HOST"]);
        var database = FirstNonEmpty(
            configuration["MYSQLDATABASE"],
            configuration["MYSQL_DATABASE"],
            configuration["DB_NAME"]);
        var user = FirstNonEmpty(
            configuration["MYSQLUSER"],
            configuration["MYSQL_USER"],
            configuration["DB_USER"]);
        var password = FirstNonEmpty(
            configuration["MYSQLPASSWORD"],
            configuration["MYSQL_PASSWORD"],
            configuration["DB_PASSWORD"]);
        var portValue = FirstNonEmpty(
            configuration["MYSQLPORT"],
            configuration["MYSQL_PORT"],
            configuration["DB_PORT"]);

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("Missing DefaultConnection connection string.");
        }

        var connectionStringBuilder = new MySqlConnectionStringBuilder
        {
            Server = host,
            Database = database ?? string.Empty,
            UserID = user ?? string.Empty,
            Password = password ?? string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(portValue))
        {
            if (!uint.TryParse(portValue, out var port) || port is < 1)
            {
                throw new InvalidOperationException("MYSQLPORT/MYSQL_PORT/DB_PORT must be a valid TCP port number.");
            }

            connectionStringBuilder.Port = port;
        }

        return connectionStringBuilder.ConnectionString;
    }

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}