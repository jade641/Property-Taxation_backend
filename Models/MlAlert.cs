using System;

namespace PropertyTax.API.Models;

public class MlAlert
{
    public int Id { get; set; }
    public int? PropertyId { get; set; }
    public string? ReferenceId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Severity { get; set; } = "Low"; // Low, Medium, High
    public string Status { get; set; } = "Open"; // Open, Resolved, Dismissed
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // navigation
    public Property? Property { get; set; }
}
