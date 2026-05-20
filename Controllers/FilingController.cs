using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropertyTax.API.Data;
using PropertyTax.API.DTOs;
using PropertyTax.API.Models;
using PropertyTax.API.Services;
using System.IO.Compression;

namespace PropertyTax.API.Controllers;

[ApiController]
[Route("api/filing")]
public class FilingController : ControllerBase
{
    private const long DefaultMaxUploadBytes = 10 * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, string> AllowedContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    };

    private readonly AppDbContext _dbContext;
    private readonly IWebHostEnvironment _webHostEnvironment;
    private readonly AuditLogService _auditLogService;
    private readonly IConfiguration _configuration;

    public FilingController(
        AppDbContext dbContext,
        IWebHostEnvironment webHostEnvironment,
        AuditLogService auditLogService,
        IConfiguration configuration)
    {
        _dbContext = dbContext;
        _webHostEnvironment = webHostEnvironment;
        _auditLogService = auditLogService;
        _configuration = configuration;
    }

    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Staff + "," + SystemRoles.Accountant)]
    [Consumes("multipart/form-data")]
    [HttpPost("upload")]
    public async Task<ActionResult<ApiResponse<PropertyDocumentDto>>> Upload([FromForm] FilingUploadDto uploadDto)
    {
        var propertyId = uploadDto.PropertyId;
        var file = uploadDto.File;
        var property = await _dbContext.Properties.FirstOrDefaultAsync(item => item.Id == propertyId);

        if (property is null)
        {
            return NotFound(ApiResponse<PropertyDocumentDto>.Fail("Property not found."));
        }

        if (file.Length == 0)
        {
            return BadRequest(ApiResponse<PropertyDocumentDto>.Fail("No file content was provided."));
        }

        var validationResult = await ValidateUploadAsync(file);

        if (!validationResult.IsValid)
        {
            return BadRequest(ApiResponse<PropertyDocumentDto>.Fail(validationResult.ErrorMessage ?? "Invalid file upload."));
        }

        var folder = string.IsNullOrWhiteSpace(uploadDto.Folder)
            ? "Property Documents"
            : uploadDto.Folder.Trim();

        var uploadRoot = _configuration["FileStorage:UploadRoot"] ?? "uploads";
        var uploadRootPath = Path.GetFullPath(Path.Combine(_webHostEnvironment.ContentRootPath, uploadRoot));
        var propertyFolder = Path.GetFullPath(Path.Combine(uploadRootPath, "properties", propertyId.ToString()));

        if (!propertyFolder.StartsWith(uploadRootPath, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(ApiResponse<PropertyDocumentDto>.Fail("Invalid upload path."));
        }

        Directory.CreateDirectory(propertyFolder);

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var originalName = Path.GetFileName(file.FileName);
        var sanitizedBaseName = SanitizeFileName(Path.GetFileNameWithoutExtension(originalName));
        var fileName = $"{sanitizedBaseName}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}{extension}";
        var filePath = Path.Combine(propertyFolder, fileName);

        await using (var stream = System.IO.File.Create(filePath))
        {
            await file.CopyToAsync(stream);
        }

        var document = new PropertyDocument
        {
            PropertyId = property.Id,
            FileName = fileName,
            OriginalFileName = originalName,
            RelativePath = Path.Combine(uploadRoot, "properties", propertyId.ToString(), fileName).Replace('\\', '/'),
            ContentType = AllowedContentTypes[extension],
            SizeInBytes = file.Length,
            Folder = folder,
            UploadedAtUtc = DateTime.UtcNow,
            UploadedByUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
        };

        _dbContext.PropertyDocuments.Add(document);
        await _dbContext.SaveChangesAsync();

        await _auditLogService.LogAsync("DocumentUploaded", "Filing", document.Id.ToString(), $"Uploaded file {fileName} for property {propertyId}");
        return Ok(ApiResponse<PropertyDocumentDto>.Ok(MapDocument(document, property.Pin), "Document uploaded successfully."));
    }

    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Staff + "," + SystemRoles.Accountant + "," + SystemRoles.Auditor)]
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<PropertyDocumentDto>>>> GetAllDocuments()
    {
        var documents = await _dbContext.PropertyDocuments
            .AsNoTracking()
            .Include(document => document.Property)
            .OrderByDescending(document => document.UploadedAtUtc)
            .ToListAsync();

        return Ok(ApiResponse<IReadOnlyCollection<PropertyDocumentDto>>.Ok(
            documents.Select(document => MapDocument(document, document.Property.Pin)).ToList(),
            "Documents retrieved successfully."));
    }

    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Staff + "," + SystemRoles.Accountant + "," + SystemRoles.Auditor)]
    [HttpGet("{propertyId:int}")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<PropertyDocumentDto>>>> GetPropertyDocuments(int propertyId)
    {
        var propertyExists = await _dbContext.Properties.AnyAsync(property => property.Id == propertyId);

        if (!propertyExists)
        {
            return NotFound(ApiResponse<IReadOnlyCollection<PropertyDocumentDto>>.Fail("Property not found."));
        }

        var documents = await _dbContext.PropertyDocuments
            .AsNoTracking()
            .Include(document => document.Property)
            .Where(document => document.PropertyId == propertyId)
            .OrderByDescending(document => document.UploadedAtUtc)
            .ToListAsync();

        return Ok(ApiResponse<IReadOnlyCollection<PropertyDocumentDto>>.Ok(
            documents.Select(document => MapDocument(document, document.Property.Pin)).ToList(),
            "Property documents retrieved successfully."));
    }

    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Accountant)]
    [HttpDelete("documents/{id:int}")]
    public async Task<ActionResult<ApiResponse<object?>>> DeleteDocument(int id)
    {
        var document = await _dbContext.PropertyDocuments.FirstOrDefaultAsync(item => item.Id == id);

        if (document is null)
        {
            return NotFound(ApiResponse<object?>.Fail("Document not found."));
        }

        var physicalPath = Path.Combine(_webHostEnvironment.ContentRootPath, document.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (System.IO.File.Exists(physicalPath))
        {
            System.IO.File.Delete(physicalPath);
        }

        _dbContext.PropertyDocuments.Remove(document);
        await _dbContext.SaveChangesAsync();

        await _auditLogService.LogAsync("DocumentDeleted", "Filing", id.ToString(), $"Deleted file {document.FileName}");
        return Ok(ApiResponse<object?>.Ok(null, "Document deleted successfully."));
    }

    private static PropertyDocumentDto MapDocument(PropertyDocument document, string propertyPin)
    {
        return new PropertyDocumentDto
        {
            Id = document.Id,
            PropertyId = document.PropertyId,
            PropertyPin = propertyPin,
            FileName = document.FileName,
            OriginalFileName = document.OriginalFileName,
            RelativePath = document.RelativePath,
            ContentType = document.ContentType,
            SizeInBytes = document.SizeInBytes,
            Folder = document.Folder,
            UploadedAtUtc = document.UploadedAtUtc,
        };
    }

    private async Task<(bool IsValid, string? ErrorMessage)> ValidateUploadAsync(IFormFile file)
    {
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

        if (!AllowedContentTypes.TryGetValue(extension, out var expectedContentType))
        {
            return (false, "Only PDF and XLSX files are allowed.");
        }

        if (!string.Equals(file.ContentType, expectedContentType, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "The uploaded file content type is not allowed.");
        }

        var maxUploadBytes = long.TryParse(_configuration["FileStorage:MaxUploadBytes"], out var configuredMaxUploadBytes)
            ? configuredMaxUploadBytes
            : DefaultMaxUploadBytes;

        if (file.Length > maxUploadBytes)
        {
            return (false, $"The uploaded file exceeds the {maxUploadBytes / 1024 / 1024} MB size limit.");
        }

        if (extension == ".pdf")
        {
            return await IsPdfAsync(file) ? (true, null) : (false, "The uploaded PDF file signature is invalid.");
        }

        return await IsXlsxAsync(file) ? (true, null) : (false, "The uploaded XLSX file signature is invalid.");
    }

    private static async Task<bool> IsPdfAsync(IFormFile file)
    {
        var signature = new byte[5];

        await using var stream = file.OpenReadStream();
        var bytesRead = await stream.ReadAsync(signature);

        return bytesRead == signature.Length
            && signature.SequenceEqual(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D });
    }

    private static async Task<bool> IsXlsxAsync(IFormFile file)
    {
        await using var stream = file.OpenReadStream();

        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            return archive.GetEntry("[Content_Types].xml") is not null
                && archive.GetEntry("xl/workbook.xml") is not null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName
            .Where(character => !invalidCharacters.Contains(character))
            .ToArray())
            .Trim();

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "document";
        }

        return sanitized.Length > 80 ? sanitized[..80] : sanitized;
    }
}