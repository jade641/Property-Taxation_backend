using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PropertyTax.API.Models;
using System.Text.Json;

namespace PropertyTax.API.Data;

public class DbInitializer
{
    private static readonly string[] LegacySeedAdminPasswords =
    [
        "Admin123!",
    ];

    private readonly AppDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<DbInitializer> _logger;

    public DbInitializer(
        AppDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<DbInitializer> logger)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _roleManager = roleManager;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        var connection = _dbContext.Database.GetDbConnection();
        var connectionTarget = $"{connection.DataSource}/{connection.Database}";

        try
        {
            _logger.LogInformation("Starting database initialization for {ConnectionTarget}.", connectionTarget);

            var pendingMigrations = (await _dbContext.Database.GetPendingMigrationsAsync()).ToArray();

            if (pendingMigrations.Length > 0)
            {
                _logger.LogInformation(
                    "Applying {MigrationCount} pending migrations for {ConnectionTarget}: {PendingMigrations}",
                    pendingMigrations.Length,
                    connectionTarget,
                    string.Join(", ", pendingMigrations));

                await _dbContext.Database.MigrateAsync();
            }
            else
            {
                _logger.LogInformation("No pending migrations found for {ConnectionTarget}.", connectionTarget);
            }

            await EnsureUserSchemaCompatibilityAsync(connectionTarget);
            await EnsureMlAlertSchemaCompatibilityAsync(connectionTarget);

            await SeedRolesAsync();
            await SeedAdminAsync();
            await SeedRegionXiLocationsAsync();

            _logger.LogInformation("Database initialization completed successfully for {ConnectionTarget}.", connectionTarget);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Database initialization failed for {ConnectionTarget}.", connectionTarget);
            throw;
        }
    }

    private async Task EnsureUserSchemaCompatibilityAsync(string connectionTarget)
    {
        const string columnName = "PasswordResetAttempts";

        await _dbContext.Database.OpenConnectionAsync();

        try
        {
            await using var existsCommand = _dbContext.Database.GetDbConnection().CreateCommand();
            existsCommand.CommandText = @"
SELECT COUNT(*)
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Users'
  AND COLUMN_NAME = 'PasswordResetAttempts';";

            var existingColumns = Convert.ToInt64(await existsCommand.ExecuteScalarAsync());

            if (existingColumns > 0)
            {
                return;
            }

            _logger.LogWarning(
                "Missing {ColumnName} column detected on Users for {ConnectionTarget}. Applying a targeted compatibility fix.",
                columnName,
                connectionTarget);

            await _dbContext.Database.ExecuteSqlRawAsync(@"
ALTER TABLE `Users`
ADD COLUMN `PasswordResetAttempts` int NOT NULL DEFAULT 0;");

            _logger.LogInformation(
                "Applied compatibility fix for {ColumnName} on {ConnectionTarget}.",
                columnName,
                connectionTarget);
        }
        finally
        {
            await _dbContext.Database.CloseConnectionAsync();
        }
    }

    private async Task EnsureMlAlertSchemaCompatibilityAsync(string connectionTarget)
    {
        const string tableName = "ml_alerts";

        await _dbContext.Database.OpenConnectionAsync();

        try
        {
            await using var existsCommand = _dbContext.Database.GetDbConnection().CreateCommand();
            existsCommand.CommandText = @"
SELECT COUNT(*)
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'ml_alerts';";

            var existingTables = Convert.ToInt64(await existsCommand.ExecuteScalarAsync());

            if (existingTables > 0)
            {
                return;
            }

            _logger.LogWarning(
                "Missing {TableName} table detected for {ConnectionTarget}. Applying a targeted compatibility fix.",
                tableName,
                connectionTarget);

            await _dbContext.Database.ExecuteSqlRawAsync(@"
CREATE TABLE `ml_alerts` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `PropertyId` int NOT NULL,
  `Title` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
  `Description` longtext CHARACTER SET utf8mb4 NOT NULL,
  `Severity` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
  `Status` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
  `CreatedAt` datetime NOT NULL,
  PRIMARY KEY (`Id`),
  INDEX `IX_ml_alerts_PropertyId` (`PropertyId`),
  CONSTRAINT `FK_ml_alerts_Properties_PropertyId` FOREIGN KEY (`PropertyId`) REFERENCES `Properties` (`Id`) ON DELETE CASCADE
) CHARACTER SET = utf8mb4;");

            _logger.LogInformation(
                "Applied compatibility fix for {TableName} on {ConnectionTarget}.",
                tableName,
                connectionTarget);
        }
        finally
        {
            await _dbContext.Database.CloseConnectionAsync();
        }
    }

    private async Task SeedRolesAsync()
    {
        foreach (var role in SystemRoles.All)
        {
            if (!await _roleManager.RoleExistsAsync(role))
            {
                var result = await _roleManager.CreateAsync(new IdentityRole(role));

                if (!result.Succeeded)
                {
                    _logger.LogError("Failed to seed role {Role}: {Errors}", role, FormatErrors(result));
                }
            }
        }
    }

    private async Task SeedAdminAsync()
    {
        var adminUsername = _configuration["SeedAdmin:Username"] ?? "admin@taxsync.gov.ph";
        var adminEmail = _configuration["SeedAdmin:Email"] ?? adminUsername;
        var adminPassword = _configuration["SeedAdmin:Password"] ?? "Admin123!";
        var adminUser = await _userManager.FindByEmailAsync(adminEmail)
            ?? await _userManager.FindByNameAsync(adminUsername);

        if (adminUser is null)
        {
            adminUser = new ApplicationUser
            {
                UserName = adminUsername,
                Email = adminEmail,
                FullName = _configuration["SeedAdmin:FullName"] ?? "TaxSync Administrator",
                EmailConfirmed = true,
                IsActive = true,
            };

            var createResult = await _userManager.CreateAsync(adminUser, adminPassword);

            if (!createResult.Succeeded)
            {
                _logger.LogError("Failed to seed TaxSync administrator account: {Errors}", FormatErrors(createResult));
                return;
            }

            _logger.LogInformation("Seeded TaxSync administrator account for {Username}", adminUsername);
        }
        else
        {
            var updated = false;

            if (!adminUser.EmailConfirmed)
            {
                adminUser.EmailConfirmed = true;
                updated = true;
            }

            if (!adminUser.IsActive)
            {
                adminUser.IsActive = true;
                updated = true;
            }

            if (string.IsNullOrWhiteSpace(adminUser.FullName))
            {
                adminUser.FullName = _configuration["SeedAdmin:FullName"] ?? "TaxSync Administrator";
                updated = true;
            }

            if (!await _userManager.HasPasswordAsync(adminUser))
            {
                var addPasswordResult = await _userManager.AddPasswordAsync(adminUser, adminPassword);

                if (!addPasswordResult.Succeeded)
                {
                    _logger.LogError("Failed to set password for seeded administrator account: {Errors}", FormatErrors(addPasswordResult));
                }
            }

            if (updated)
            {
                var updateResult = await _userManager.UpdateAsync(adminUser);

                if (!updateResult.Succeeded)
                {
                    _logger.LogError("Failed to update seeded administrator account: {Errors}", FormatErrors(updateResult));
                }
            }

            await AlignSeedAdminPasswordAsync(adminUser, adminPassword);
        }

        if (!await _userManager.IsInRoleAsync(adminUser, SystemRoles.Admin))
        {
            var roleResult = await _userManager.AddToRoleAsync(adminUser, SystemRoles.Admin);

            if (!roleResult.Succeeded)
            {
                _logger.LogError("Failed to assign Admin role to seeded administrator account: {Errors}", FormatErrors(roleResult));
            }
        }
    }

    private async Task AlignSeedAdminPasswordAsync(ApplicationUser adminUser, string configuredPassword)
    {
        if (string.IsNullOrWhiteSpace(configuredPassword))
        {
            _logger.LogWarning("Seed admin password is empty. Skipping password alignment.");
            return;
        }

        if (await _userManager.CheckPasswordAsync(adminUser, configuredPassword))
        {
            return;
        }

        foreach (var legacyPassword in LegacySeedAdminPasswords)
        {
            if (string.Equals(legacyPassword, configuredPassword, StringComparison.Ordinal))
            {
                continue;
            }

            if (!await _userManager.CheckPasswordAsync(adminUser, legacyPassword))
            {
                continue;
            }

            var resetToken = await _userManager.GeneratePasswordResetTokenAsync(adminUser);
            var resetResult = await _userManager.ResetPasswordAsync(adminUser, resetToken, configuredPassword);

            if (!resetResult.Succeeded)
            {
                _logger.LogError("Failed to align seeded administrator password: {Errors}", FormatErrors(resetResult));
                return;
            }

            _logger.LogInformation("Aligned seeded administrator password with the configured development credential.");
            return;
        }
    }

    private async Task SeedRegionXiLocationsAsync()
    {
        var seedFilePath = Path.Combine(_environment.ContentRootPath, "Data", "region-xi-locations.json");

        if (!File.Exists(seedFilePath))
        {
            _logger.LogWarning("Region XI location seed file not found at {SeedFilePath}", seedFilePath);
            return;
        }

        await using var seedStream = File.OpenRead(seedFilePath);
        var seedData = await JsonSerializer.DeserializeAsync<RegionXiSeedData>(seedStream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        if (seedData?.Provinces.Count is null or 0)
        {
            _logger.LogWarning("Region XI location seed file did not contain any provinces.");
            return;
        }

        var provincesByCode = await _dbContext.Provinces.ToDictionaryAsync(province => province.PsgcCode);

        foreach (var provinceSeed in seedData.Provinces)
        {
            if (!provincesByCode.TryGetValue(provinceSeed.Code, out var province))
            {
                province = new Province
                {
                    RegionCode = seedData.RegionCode,
                    PsgcCode = provinceSeed.Code,
                    Name = provinceSeed.Name,
                };

                _dbContext.Provinces.Add(province);
                provincesByCode[provinceSeed.Code] = province;
            }
            else
            {
                province.RegionCode = seedData.RegionCode;
                province.Name = provinceSeed.Name;
            }
        }

        await _dbContext.SaveChangesAsync();

        var citiesByCode = await _dbContext.CitiesMunicipalities.ToDictionaryAsync(city => city.PsgcCode);

        foreach (var provinceSeed in seedData.Provinces)
        {
            var province = provincesByCode[provinceSeed.Code];

            foreach (var citySeed in provinceSeed.CitiesMunicipalities)
            {
                if (!citiesByCode.TryGetValue(citySeed.Code, out var city))
                {
                    city = new CityMunicipality
                    {
                        ProvinceId = province.Id,
                        PsgcCode = citySeed.Code,
                        Name = citySeed.Name,
                        LguType = citySeed.Type,
                    };

                    _dbContext.CitiesMunicipalities.Add(city);
                    citiesByCode[citySeed.Code] = city;
                }
                else
                {
                    city.ProvinceId = province.Id;
                    city.Name = citySeed.Name;
                    city.LguType = citySeed.Type;
                }
            }
        }

        await _dbContext.SaveChangesAsync();

        var existingBarangaysByCode = await _dbContext.Barangays
            .ToDictionaryAsync(barangay => barangay.PsgcCode);

        foreach (var provinceSeed in seedData.Provinces)
        {
            foreach (var citySeed in provinceSeed.CitiesMunicipalities)
            {
                var city = citiesByCode[citySeed.Code];

                foreach (var barangaySeed in citySeed.Barangays)
                {
                    if (!existingBarangaysByCode.TryGetValue(barangaySeed.Code, out var barangay))
                    {
                        _dbContext.Barangays.Add(new Barangay
                        {
                            CityMunicipalityId = city.Id,
                            PsgcCode = barangaySeed.Code,
                            Name = barangaySeed.Name,
                        });

                        continue;
                    }

                    barangay.CityMunicipalityId = city.Id;
                    barangay.Name = barangaySeed.Name;
                }
            }
        }

        await _dbContext.SaveChangesAsync();
    }

    private static string FormatErrors(IdentityResult result)
    {
        return string.Join(", ", result.Errors.Select(error => error.Description));
    }

    private sealed class RegionXiSeedData
    {
        public string RegionCode { get; set; } = "110000000";
        public List<ProvinceSeed> Provinces { get; set; } = new();
    }

    private sealed class ProvinceSeed
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<CityMunicipalitySeed> CitiesMunicipalities { get; set; } = new();
    }

    private sealed class CityMunicipalitySeed
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public List<BarangaySeed> Barangays { get; set; } = new();
    }

    private sealed class BarangaySeed
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
}