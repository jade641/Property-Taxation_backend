using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PropertyTax.API.Data;
using PropertyTax.API.DTOs;
using PropertyTax.API.Models;

namespace PropertyTax.API.Services;

public class DataNotificationService
{
    private readonly AppDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;

    public DataNotificationService(AppDbContext dbContext, UserManager<ApplicationUser> userManager)
    {
        _dbContext = dbContext;
        _userManager = userManager;
    }

    public async Task CreateForRolesAsync(
        string title,
        string message,
        string type,
        IEnumerable<string> roles,
        string? entityName,
        string? entityId)
    {
        var normalizedRoles = roles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedRoles.Length == 0)
        {
            return;
        }

        var usersInRoles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var role in normalizedRoles)
        {
            var users = await _userManager.GetUsersInRoleAsync(role);

            foreach (var user in users)
            {
                usersInRoles.Add(user.Id);
            }
        }

        if (usersInRoles.Count == 0)
        {
            return;
        }

        var createdAtUtc = DateTime.UtcNow;
        var notifications = usersInRoles.Select(userId => new DataNotification
        {
            UserId = userId,
            Title = title,
            Message = message,
            Type = NormalizeType(type),
            EntityName = string.IsNullOrWhiteSpace(entityName) ? null : entityName.Trim(),
            EntityId = string.IsNullOrWhiteSpace(entityId) ? null : entityId.Trim(),
            IsRead = false,
            CreatedAtUtc = createdAtUtc,
        });

        await _dbContext.DataNotifications.AddRangeAsync(notifications);
        await _dbContext.SaveChangesAsync();
    }

    public async Task<IReadOnlyCollection<DataNotificationDto>> GetForUserAsync(string userId, int take)
    {
        var safeTake = Math.Clamp(take, 1, 100);

        var notifications = await _dbContext.DataNotifications
            .AsNoTracking()
            .Where(item => item.UserId == userId)
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.Id)
            .Take(safeTake)
            .ToListAsync();

        return notifications.Select(Map).ToArray();
    }

    public Task<int> GetUnreadCountAsync(string userId)
    {
        return _dbContext.DataNotifications
            .AsNoTracking()
            .CountAsync(item => item.UserId == userId && !item.IsRead);
    }

    public async Task<bool> MarkAsReadAsync(string userId, int notificationId)
    {
        var notification = await _dbContext.DataNotifications
            .FirstOrDefaultAsync(item => item.Id == notificationId && item.UserId == userId);

        if (notification is null)
        {
            return false;
        }

        if (!notification.IsRead)
        {
            notification.IsRead = true;
            notification.ReadAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
        }

        return true;
    }

    public async Task<int> MarkAllAsReadAsync(string userId)
    {
        var unreadNotifications = await _dbContext.DataNotifications
            .Where(item => item.UserId == userId && !item.IsRead)
            .ToListAsync();

        if (unreadNotifications.Count == 0)
        {
            return 0;
        }

        var now = DateTime.UtcNow;

        foreach (var notification in unreadNotifications)
        {
            notification.IsRead = true;
            notification.ReadAtUtc = now;
        }

        await _dbContext.SaveChangesAsync();
        return unreadNotifications.Count;
    }

    private static DataNotificationDto Map(DataNotification notification)
    {
        return new DataNotificationDto
        {
            Id = notification.Id,
            Title = notification.Title,
            Message = notification.Message,
            Type = notification.Type,
            EntityName = notification.EntityName,
            EntityId = notification.EntityId,
            IsRead = notification.IsRead,
            CreatedAtUtc = notification.CreatedAtUtc,
            ReadAtUtc = notification.ReadAtUtc,
        };
    }

    private static string NormalizeType(string? value)
    {
        var type = string.IsNullOrWhiteSpace(value) ? "info" : value.Trim().ToLowerInvariant();

        return type switch
        {
            "info" => "info",
            "deadline" => "deadline",
            "overdue" => "overdue",
            _ => "info",
        };
    }
}