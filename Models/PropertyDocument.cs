namespace PropertyTax.API.Models;

public class PropertyDocument
{
    public int Id { get; set; }
    public int PropertyId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeInBytes { get; set; }
    public string Folder { get; set; } = "Property Documents";
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
    public string? UploadedByUserId { get; set; }

    public Property Property { get; set; } = null!;
}