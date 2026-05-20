using System.Security.Claims;
using PropertyTax.API.Data;
using PropertyTax.API.Models;

namespace PropertyTax.API.Services;

public class AuditLogService
{
    private readonly AppDbContext _dbContext;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuditLogService> _logger;

    public AuditLogService(
        AppDbContext dbContext,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AuditLogService> logger)
    {
        _dbContext = dbContext;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task LogAsync(
        string action,
        string entityName,
        string? entityId,
        string? description,
        bool succeeded = true,
        string? userId = null,
        string? username = null,
        string? role = null)
    {
        try
        {
            var principal = _httpContextAccessor.HttpContext?.User;

            userId ??= principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            username ??= principal?.FindFirstValue(ClaimTypes.Name) ?? principal?.Identity?.Name;
            role ??= principal?.FindFirstValue(ClaimTypes.Role) ?? principal?.FindFirst("role")?.Value;

            var auditLog = new AuditLog
            {
                Action = action,
                EntityName = entityName,
                EntityId = entityId,
                Description = description,
                Succeeded = succeeded,
                PerformedByUserId = userId,
                PerformedByUsername = username,
                UserRole = role,
                IpAddress = GetIpAddress(),
                CreatedAtUtc = DateTime.UtcNow,
            };

            _dbContext.AuditLogs.Add(auditLog);
            await _dbContext.SaveChangesAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to persist audit log entry for {Action} on {EntityName}", action, entityName);
        }
    }

    private string? GetIpAddress()
    {
        var httpContext = _httpContextAccessor.HttpContext;

        if (httpContext is null)
        {
            return null;
        }

        if (httpContext.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
        {
            return forwardedFor.ToString().Split(',').FirstOrDefault()?.Trim();
        }

        return httpContext.Connection.RemoteIpAddress?.ToString();
    }
}