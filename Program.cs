using System.Data.Common;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using PropertyTax.API.Data;
using PropertyTax.API.DTOs;
using PropertyTax.API.Models;
using PropertyTax.API.Services;

const uint DatabaseConnectionTimeoutSeconds = 60;
const string DatabaseUnreachableMessage = "Database is not reachable from the application host.";

var builder = WebApplication.CreateBuilder(args);
var listenPort = ResolveListenPort(builder.Configuration);

if (listenPort.HasValue && string.IsNullOrWhiteSpace(builder.Configuration["ASPNETCORE_URLS"]))
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(listenPort.Value);
    });
}

var configuredConnectionString = ResolveConnectionString(builder.Configuration);
var connectionString = CreateMySqlConnectionString(configuredConnectionString);
var databaseServerVersion = ResolveDatabaseServerVersion(builder.Configuration);

var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Missing JWT key.");

if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
{
    throw new InvalidOperationException("JWT signing key must be at least 256 bits long.");
}

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "PropertyTax.API";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "PropertyTax.Client";
var frontendBaseUrl = ResolveFrontendBaseUrl(builder.Configuration);
var corsOrigins = ResolveCorsOrigins(builder.Configuration, frontendBaseUrl);
var allowAnyOrigin = builder.Configuration.GetValue(
    "Cors:AllowAnyOrigin",
    builder.Environment.IsDevelopment() || builder.Environment.IsStaging());
var maxUploadBytes = long.TryParse(builder.Configuration["FileStorage:MaxUploadBytes"], out var configuredMaxUploadBytes)
    ? configuredMaxUploadBytes
    : 10 * 1024 * 1024;
var uploadRootPath = ResolveUploadRootPath(builder.Environment.ContentRootPath, builder.Configuration["FileStorage:UploadRoot"]);
var dataProtectionKeyPath = ResolveDataProtectionKeyPath(
    builder.Environment.ContentRootPath,
    builder.Configuration["DataProtection:KeyPath"],
    uploadRootPath);
var requireDatabaseOnStartup = builder.Configuration.GetValue(
    "Database:RequireConnectionOnStartup",
    builder.Environment.IsDevelopment());
var runInitializationOnStartup = builder.Configuration.GetValue(
    "Database:RunInitializationOnStartup",
    builder.Environment.IsDevelopment());
var startupConnectionTimeoutSeconds = int.TryParse(
    builder.Configuration["Database:StartupConnectionTimeoutSeconds"],
    out var configuredStartupTimeoutSeconds)
    ? Math.Clamp(configuredStartupTimeoutSeconds, 5, 300)
    : 60;

Directory.CreateDirectory(uploadRootPath);
Directory.CreateDirectory(dataProtectionKeyPath);

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseMySql(
        connectionString,
        databaseServerVersion,
        mySqlOptions =>
        {
            mySqlOptions.EnableRetryOnFailure(
                5,
                TimeSpan.FromSeconds(10),
                null
            );
        });

    if (builder.Environment.IsDevelopment())
    {
        options.EnableDetailedErrors();
    }
});

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredUniqueChars = 1;
        options.User.RequireUniqueEmail = true;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.ClaimsIdentity.UserIdClaimType = System.Security.Claims.ClaimTypes.NameIdentifier;
        options.ClaimsIdentity.UserNameClaimType = System.Security.Claims.ClaimTypes.Name;
        options.ClaimsIdentity.RoleClaimType = System.Security.Claims.ClaimTypes.Role;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services.Configure<DataProtectionTokenProviderOptions>(options =>
{
    options.TokenLifespan = TimeSpan.FromMinutes(20);
});

builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("EmailSettings"));
builder.Services.AddScoped<IPasswordHasher<ApplicationUser>, BCryptPasswordHasher>();
builder.Services.AddScoped<IEmailService, EmailService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            NameClaimType = System.Security.Claims.ClaimTypes.Name,
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
            ClockSkew = TimeSpan.Zero,
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

if (corsOrigins.Length > 0 || allowAnyOrigin)
{
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("Frontend", policy =>
        {
            if (allowAnyOrigin)
            {
                policy.AllowAnyOrigin()
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            }
            else
            {
                policy.SetIsOriginAllowed(origin => IsAllowedFrontendOrigin(origin, corsOrigins))
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            }
        });
    });
}

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadBytes;
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddDataProtection()
    .SetApplicationName("PropertyTax.API")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath));

builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var errors = context.ModelState
                .Values
                .SelectMany(value => value.Errors)
                .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage) ? "Invalid request payload." : error.ErrorMessage)
                .ToArray();

            return new BadRequestObjectResult(ApiResponse<object?>.Fail("Validation failed.", errors));
        };
    });

builder.Services.AddHttpContextAccessor();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "PropertyTax.API",
        Version = "v1",
        Description = "Property Tax Web API",
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Enter a valid Bearer token.",
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer",
                },
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddScoped<DbInitializer>();
builder.Services.AddScoped<SampleDataSeeder>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<PropertyService>();
builder.Services.AddScoped<LocationService>();
builder.Services.AddScoped<TaxService>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<DataNotificationService>();
builder.Services.AddScoped<IMlPredictionService, MlPredictionService>();

var app = builder.Build();

if (args.Any(arg => string.Equals(arg, "--reset-admin", StringComparison.OrdinalIgnoreCase)))
{
    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("ResetAdmin");

    await ResetAdminAsync(scope.ServiceProvider, builder.Configuration, logger);
    return;
}

if (requireDatabaseOnStartup || runInitializationOnStartup)
{
    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(startupConnectionTimeoutSeconds));
    var dbTest = await TestDatabaseConnectionAsync(connectionString, timeoutCts.Token);

    if (!dbTest.Success)
    {
        var details = string.IsNullOrWhiteSpace(dbTest.FailureReason)
            ? dbTest.Message
            : $"{dbTest.Message} {dbTest.FailureReason}";

        logger.LogError("Database startup check failed: {Details}", details);
        throw new InvalidOperationException(details);
    }

    if (runInitializationOnStartup)
    {
        var initializer = scope.ServiceProvider.GetRequiredService<DbInitializer>();
        await initializer.InitializeAsync();
    }
}

if (corsOrigins.Length == 0 && !allowAnyOrigin)
{
    app.Logger.LogWarning(
    "No CORS origins are configured. Browser clients will remain blocked until FrontendBaseUrl, FRONTEND_BASE_URL, Cors:AllowedOrigins, or CORS_ALLOWED_ORIGINS is set.");
}

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("GlobalExceptionHandler");

        if (exception is not null)
        {
            logger.LogError(exception, "Unhandled exception while processing {Method} {Path}", context.Request.Method, context.Request.Path);
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";

        var response = ApiResponse<object?>.Fail("An unexpected server error occurred.");

        await context.Response.WriteAsJsonAsync(response);
    });
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment() && !listenPort.HasValue)
{
    app.UseHttpsRedirection();
}

app.UseRouting();

if (corsOrigins.Length > 0 || allowAnyOrigin)
{
    app.UseCors("Frontend");
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/db-test", async (CancellationToken cancellationToken) =>
{
    var dbTest = await TestDatabaseConnectionAsync(connectionString, cancellationToken);
    var response = new
    {
        success = dbTest.Success,
        message = dbTest.Message,
        serverVersion = dbTest.ServerVersion,
    };

    return dbTest.Success
        ? Results.Ok(response)
        : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/", () => Results.Ok(new
{
    service = "PropertyTax.API",
    status = "ok",
    health = "/health",
    apiBase = "/api",
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapControllers();
app.Run();

static async Task ResetAdminAsync(IServiceProvider services, IConfiguration configuration, ILogger logger)
{
    var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
    var adminUsername = configuration["SeedAdmin:Username"] ?? "admin@taxsync.gov.ph";
    var adminEmail = configuration["SeedAdmin:Email"] ?? adminUsername;
    var adminPassword = configuration["SeedAdmin:Password"] ?? "Admin#123";
    var adminFullName = configuration["SeedAdmin:FullName"] ?? "TaxSync Administrator";

    var adminUser = await userManager.FindByEmailAsync(adminEmail)
        ?? await userManager.FindByNameAsync(adminUsername);

    if (adminUser is null)
    {
        adminUser = new ApplicationUser
        {
            UserName = adminUsername,
            Email = adminEmail,
            FullName = adminFullName,
            EmailConfirmed = true,
            IsActive = true,
        };

        var createResult = await userManager.CreateAsync(adminUser, adminPassword);

        if (!createResult.Succeeded)
        {
            logger.LogError("Failed to create admin user: {Errors}", FormatIdentityErrors(createResult));
            return;
        }

        logger.LogInformation("Created admin user {Username}.", adminUsername);
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
            adminUser.FullName = adminFullName;
            updated = true;
        }

        if (updated)
        {
            var updateResult = await userManager.UpdateAsync(adminUser);

            if (!updateResult.Succeeded)
            {
                logger.LogError("Failed to update admin user: {Errors}", FormatIdentityErrors(updateResult));
                return;
            }
        }

        var resetToken = await userManager.GeneratePasswordResetTokenAsync(adminUser);
        var resetResult = await userManager.ResetPasswordAsync(adminUser, resetToken, adminPassword);

        if (!resetResult.Succeeded)
        {
            logger.LogError("Failed to reset admin password: {Errors}", FormatIdentityErrors(resetResult));
            return;
        }

        logger.LogInformation("Reset admin password for {Username}.", adminUsername);
    }

    if (!await roleManager.RoleExistsAsync(SystemRoles.Admin))
    {
        var roleResult = await roleManager.CreateAsync(new IdentityRole(SystemRoles.Admin));

        if (!roleResult.Succeeded)
        {
            logger.LogError("Failed to create Admin role: {Errors}", FormatIdentityErrors(roleResult));
            return;
        }
    }

    if (!await userManager.IsInRoleAsync(adminUser, SystemRoles.Admin))
    {
        var roleResult = await userManager.AddToRoleAsync(adminUser, SystemRoles.Admin);

        if (!roleResult.Succeeded)
        {
            logger.LogError("Failed to assign Admin role: {Errors}", FormatIdentityErrors(roleResult));
        }
    }
}

static string FormatIdentityErrors(IdentityResult result)
{
    return string.Join(", ", result.Errors.Select(error => error.Description));
}

static string? ResolveFrontendBaseUrl(IConfiguration configuration)
{
    var candidates = new[]
    {
        configuration["FrontendBaseUrl"],
        configuration["FRONTEND_BASE_URL"],
        configuration["PUBLIC_FRONTEND_URL"],
    };

    foreach (var candidate in candidates)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate.Trim().TrimEnd('/');
        }
    }

    return null;
}

static string[] ResolveCorsOrigins(IConfiguration configuration, string? fallbackOrigin)
{
    var configuredOrigins = configuration.GetSection("Cors:AllowedOrigins")
        .Get<string[]>()?
        .Where(origin => !string.IsNullOrWhiteSpace(origin))
        .Select(origin => origin.TrimEnd('/'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    if (configuredOrigins is { Length: > 0 })
    {
        return configuredOrigins;
    }

    var inlineConfiguredOrigins = ParseDelimitedValues(
            configuration["Cors:AllowedOrigins"],
            configuration["CORS_ALLOWED_ORIGINS"])
        .Select(origin => origin.TrimEnd('/'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    if (inlineConfiguredOrigins.Length > 0)
    {
        return inlineConfiguredOrigins;
    }

    if (!string.IsNullOrWhiteSpace(fallbackOrigin))
    {
        return [fallbackOrigin];
    }

    return [];
}

static bool IsAllowedFrontendOrigin(string origin, string[] configuredOrigins)
{
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsedOrigin))
    {
        return false;
    }

    var normalizedOrigin = origin.TrimEnd('/');

    if (configuredOrigins.Any(configured => string.Equals(configured, normalizedOrigin, StringComparison.OrdinalIgnoreCase)))
    {
        return true;
    }

    if (!string.Equals(parsedOrigin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(parsedOrigin.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var host = parsedOrigin.Host;

    return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("::1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("vercel.app", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".vercel.app", StringComparison.OrdinalIgnoreCase);
}

static string ResolveUploadRootPath(string contentRootPath, string? configuredUploadRoot)
{
    if (!string.IsNullOrWhiteSpace(configuredUploadRoot))
    {
        return ResolvePath(contentRootPath, configuredUploadRoot);
    }

    if (Directory.Exists("/var/data"))
    {
        return "/var/data/uploads";
    }

    var uploadRoot = "uploads";

    return Path.GetFullPath(Path.Combine(contentRootPath, uploadRoot));
}

static string ResolveDataProtectionKeyPath(string contentRootPath, string? configuredKeyPath, string uploadRootPath)
{
    if (!string.IsNullOrWhiteSpace(configuredKeyPath))
    {
        return ResolvePath(contentRootPath, configuredKeyPath);
    }

    if (Directory.Exists("/var/data"))
    {
        return "/var/data/data-protection-keys";
    }

    return Path.GetFullPath(Path.Combine(uploadRootPath, ".keys"));
}

static string ResolvePath(string contentRootPath, string configuredPath)
{
    return Path.IsPathRooted(configuredPath)
        ? Path.GetFullPath(configuredPath)
        : Path.GetFullPath(Path.Combine(contentRootPath, configuredPath));
}

static IEnumerable<string> ParseDelimitedValues(params string?[] candidates)
{
    foreach (var candidate in candidates)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            continue;
        }

        foreach (var value in candidate.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }
}

static int? ResolveListenPort(IConfiguration configuration)
{
    var configuredPort = configuration["PORT"];

    if (string.IsNullOrWhiteSpace(configuredPort))
    {
        return null;
    }

    if (!int.TryParse(configuredPort, out var port) || port is < 1 or > 65535)
    {
        throw new InvalidOperationException("The PORT environment variable must be a valid TCP port number.");
    }

    return port;
}

static string ResolveConnectionString(IConfiguration configuration)
{
    var candidates = new[]
    {
        configuration.GetConnectionString("DefaultConnection"),
        configuration["ConnectionStrings:DefaultConnection"],
        configuration["DefaultConnection"],
        configuration["DATABASE_URL"],
    };

    foreach (var candidate in candidates)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }
    }

    throw new InvalidOperationException(
        "Missing connection string. Set ConnectionStrings:DefaultConnection or ConnectionStrings__DefaultConnection, or provide DATABASE_URL in MySQL connection-string or mysql:// form.");
}

static ServerVersion ResolveDatabaseServerVersion(IConfiguration configuration)
{
    var configuredVersion = configuration["Database:ServerVersion"];

    if (string.IsNullOrWhiteSpace(configuredVersion))
    {
        return new MySqlServerVersion(new Version(8, 0, 36));
    }

                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      if (!Version.TryParse(configuredVersion, out var parsedVersion))
    {
        throw new InvalidOperationException("Database:ServerVersion must be a valid semantic version such as 8.0.36 or 10.11.15.");
    }

    var databaseEngine = configuration["Database:Engine"];

    return string.Equals(databaseEngine, "MariaDb", StringComparison.OrdinalIgnoreCase)
        || string.Equals(databaseEngine, "MariaDB", StringComparison.OrdinalIgnoreCase)
        ? new MariaDbServerVersion(parsedVersion)
        : new MySqlServerVersion(parsedVersion);
}

static string CreateMySqlConnectionString(string configuredConnectionString)
{
    var normalizedConnectionString = NormalizeMySqlConnectionString(configuredConnectionString);
    var connectionStringBuilder = new MySqlConnectionStringBuilder(normalizedConnectionString);

    if (!ContainsConnectionOption(normalizedConnectionString, "ConnectionTimeout", "Connection Timeout", "Connect Timeout"))
    {
        connectionStringBuilder.ConnectionTimeout = DatabaseConnectionTimeoutSeconds;
    }

    if (!ContainsConnectionOption(normalizedConnectionString, "DefaultCommandTimeout", "Default Command Timeout"))
    {
        connectionStringBuilder.DefaultCommandTimeout = DatabaseConnectionTimeoutSeconds;
    }

    if (!ContainsConnectionOption(normalizedConnectionString, "SslMode", "Ssl Mode"))
    {
        connectionStringBuilder.SslMode = MySqlSslMode.Preferred;
    }

    return connectionStringBuilder.ConnectionString;
}

static string NormalizeMySqlConnectionString(string configuredConnectionString)
{
    if (!Uri.TryCreate(configuredConnectionString, UriKind.Absolute, out var databaseUrl)
        || (!string.Equals(databaseUrl.Scheme, "mysql", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(databaseUrl.Scheme, "mariadb", StringComparison.OrdinalIgnoreCase)))
    {
        return configuredConnectionString;
    }

    if (string.IsNullOrWhiteSpace(databaseUrl.Host))
    {
        throw new InvalidOperationException("DATABASE_URL must include a database host.");
    }

    var credentials = databaseUrl.UserInfo.Split(':', 2, StringSplitOptions.None);
    var connectionStringBuilder = new MySqlConnectionStringBuilder
    {
        Server = databaseUrl.Host,
        Port = databaseUrl.Port > 0 ? checked((uint)databaseUrl.Port) : 3306u,
        UserID = credentials.Length > 0 ? Uri.UnescapeDataString(credentials[0]) : string.Empty,
        Password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : string.Empty,
    };

    var databaseName = databaseUrl.AbsolutePath.Trim('/');

    if (!string.IsNullOrWhiteSpace(databaseName))
    {
        connectionStringBuilder.Database = Uri.UnescapeDataString(databaseName);
    }

    foreach (var queryParameter in QueryHelpers.ParseQuery(databaseUrl.Query))
    {
        var value = queryParameter.Value.ToString();

        if (string.IsNullOrWhiteSpace(value))
        {
            continue;
        }

        try
        {
            connectionStringBuilder[queryParameter.Key] = value;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"DATABASE_URL includes unsupported MySQL option '{queryParameter.Key}'.",
                exception);
        }
    }

    return connectionStringBuilder.ConnectionString;
}

static bool ContainsConnectionOption(string connectionString, params string[] optionNames)
{
    if (Uri.TryCreate(connectionString, UriKind.Absolute, out var databaseUrl)
        && (string.Equals(databaseUrl.Scheme, "mysql", StringComparison.OrdinalIgnoreCase)
            || string.Equals(databaseUrl.Scheme, "mariadb", StringComparison.OrdinalIgnoreCase)))
    {
        var queryParameterNames = QueryHelpers.ParseQuery(databaseUrl.Query)
            .Keys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return optionNames.Any(queryParameterNames.Contains);
    }

    var connectionStringBuilder = new DbConnectionStringBuilder
    {
        ConnectionString = connectionString,
    };

    return optionNames.Any(connectionStringBuilder.ContainsKey);
}

static async Task<DatabaseConnectionTestResult> TestDatabaseConnectionAsync(
    string connectionString,
    CancellationToken cancellationToken)
{
    try
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        return new DatabaseConnectionTestResult(
            Success: true,
            Message: "Database connection successful.",
            ServerVersion: connection.ServerVersion,
            FailureReason: null);
    }
    catch (Exception exception)
    {
        var message = IsReachabilityFailure(exception)
            ? DatabaseUnreachableMessage
            : "Database connection failed.";

        return new DatabaseConnectionTestResult(
            Success: false,
            Message: message,
            ServerVersion: null,
            FailureReason: exception.Message);
    }
}

static bool IsReachabilityFailure(Exception exception)
{
    return exception is TimeoutException
        || exception is SocketException
        || exception.Message.Contains("Unable to connect to any of the specified MySQL hosts", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
        || (exception.InnerException is not null && IsReachabilityFailure(exception.InnerException));
}

internal sealed record DatabaseConnectionTestResult(
    bool Success,
    string Message,
    string? ServerVersion,
    string? FailureReason);
