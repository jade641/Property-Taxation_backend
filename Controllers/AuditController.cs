using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropertyTax.API.Data;
using PropertyTax.API.DTOs;
using PropertyTax.API.Models;

namespace PropertyTax.API.Controllers;

[ApiController]
[Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Auditor)]
[Route("api/audit")]
public class AuditController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public AuditController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("logs")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<object>>>> GetLogs([FromQuery] string? search, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc)
    {
        var query = _dbContext.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalizedSearch = search.Trim();
            query = query.Where(log =>
                log.Action.Contains(normalizedSearch) ||
                (log.EntityName != null && log.EntityName.Contains(normalizedSearch)) ||
                (log.PerformedByUsername != null && log.PerformedByUsername.Contains(normalizedSearch)) ||
                (log.Description != null && log.Description.Contains(normalizedSearch)));
        }

        if (fromUtc.HasValue)
        {
            query = query.Where(log => log.CreatedAtUtc >= fromUtc.Value);
        }

        if (toUtc.HasValue)
        {
            query = query.Where(log => log.CreatedAtUtc <= toUtc.Value);
        }

        var logs = await query
            .OrderByDescending(log => log.CreatedAtUtc)
            .Select(log => new
            {
                log.Id,
                log.Action,
                UserId = log.PerformedByUserId,
                Timestamp = log.CreatedAtUtc,
                log.EntityName,
                log.EntityId,
                log.PerformedByUserId,
                log.PerformedByUsername,
                log.UserRole,
                log.IpAddress,
                log.Description,
                log.Succeeded,
                log.CreatedAtUtc,
            })
            .ToListAsync<object>();

        return Ok(ApiResponse<IReadOnlyCollection<object>>.Ok(logs, "Audit logs retrieved successfully."));
    }
}