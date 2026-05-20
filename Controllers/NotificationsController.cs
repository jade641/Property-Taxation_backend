using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropertyTax.API.DTOs;
using PropertyTax.API.Services;

namespace PropertyTax.API.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly DataNotificationService _notificationService;

    public NotificationsController(DataNotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<DataNotificationDto>>>> Get([FromQuery] int take = 20)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(ApiResponse<IReadOnlyCollection<DataNotificationDto>>.Fail("User context is unavailable."));
        }

        var notifications = await _notificationService.GetForUserAsync(userId, take);
        return Ok(ApiResponse<IReadOnlyCollection<DataNotificationDto>>.Ok(notifications));
    }

    [HttpGet("unread-count")]
    public async Task<ActionResult<ApiResponse<int>>> UnreadCount()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(ApiResponse<int>.Fail("User context is unavailable."));
        }

        var count = await _notificationService.GetUnreadCountAsync(userId);
        return Ok(ApiResponse<int>.Ok(count));
    }

    [HttpPost("{id:int}/read")]
    public async Task<ActionResult<ApiResponse<object?>>> MarkRead([FromRoute] int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(ApiResponse<object?>.Fail("User context is unavailable."));
        }

        var updated = await _notificationService.MarkAsReadAsync(userId, id);

        return updated
            ? Ok(ApiResponse<object?>.Ok(null, "Notification marked as read."))
            : NotFound(ApiResponse<object?>.Fail("Notification not found."));
    }

    [HttpPost("read-all")]
    public async Task<ActionResult<ApiResponse<int>>> MarkAllRead()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(ApiResponse<int>.Fail("User context is unavailable."));
        }

        var updatedCount = await _notificationService.MarkAllAsReadAsync(userId);
        return Ok(ApiResponse<int>.Ok(updatedCount, "Notifications marked as read."));
    }
}