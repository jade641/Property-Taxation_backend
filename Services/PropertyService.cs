using Microsoft.EntityFrameworkCore;
using PropertyTax.API.Data;
using PropertyTax.API.DTOs;
using PropertyTax.API.Models;

namespace PropertyTax.API.Services;

public class PropertyService
{
    private readonly AppDbContext _dbContext;
    private readonly AuditLogService _auditLogService;

    public PropertyService(AppDbContext dbContext, AuditLogService auditLogService)
    {
        _dbContext = dbContext;
        _auditLogService = auditLogService;
    }

    public async Task<IReadOnlyCollection<PropertyDto>> GetAllAsync()
    {
        return await _dbContext.Properties
            .AsNoTracking()
            .Include(property => property.Taxpayer)
            .Include(property => property.BarangayLocation)
            .ThenInclude(barangay => barangay!.CityMunicipality)
            .ThenInclude(cityMunicipality => cityMunicipality!.Province)
            .OrderByDescending(property => property.DateRegisteredUtc)
            .Select(property => MapProperty(property))
            .ToListAsync();
    }

    public async Task<PropertyDto?> GetByIdAsync(int id)
    {
        return await _dbContext.Properties
            .AsNoTracking()
            .Include(property => property.Taxpayer)
            .Include(property => property.BarangayLocation)
            .ThenInclude(barangay => barangay!.CityMunicipality)
            .ThenInclude(cityMunicipality => cityMunicipality!.Province)
            .Where(property => property.Id == id)
            .Select(property => MapProperty(property))
            .FirstOrDefaultAsync();
    }

    public async Task<PropertyDto> CreateAsync(PropertyDto propertyDto)
    {
        if (await _dbContext.Properties.AnyAsync(property => property.Pin == propertyDto.Pin.Trim()))
        {
            throw new InvalidOperationException("A property with the supplied PIN already exists.");
        }

        var taxDeclarationNumber = NormalizeOptional(propertyDto.TaxDeclarationNumber);

        if (taxDeclarationNumber is not null && await _dbContext.Properties.AnyAsync(property => property.TaxDeclarationNumber == taxDeclarationNumber))
        {
            throw new InvalidOperationException("A property with the supplied tax declaration number already exists.");
        }

        var taxpayer = await ResolveTaxpayerAsync(propertyDto);
        var property = new Property
        {
            Taxpayer = taxpayer,
            Pin = propertyDto.Pin.Trim(),
            BarangayId = propertyDto.BarangayId,
            TaxDeclarationNumber = taxDeclarationNumber,
            Barangay = propertyDto.Barangay.Trim(),
            Municipality = propertyDto.Municipality.Trim(),
            Address = propertyDto.Address.Trim(),
            PropertyType = propertyDto.PropertyType.Trim(),
            LotNumber = propertyDto.LotNumber.Trim(),
            AreaSquareMeters = propertyDto.AreaSquareMeters,
            MarketValue = propertyDto.MarketValue,
            AssessmentLevel = propertyDto.AssessmentLevel,
            TaxRate = propertyDto.TaxRate,
            ZoningClassification = NormalizeOptional(propertyDto.ZoningClassification),
            Remarks = NormalizeOptional(propertyDto.Remarks),
            Status = string.IsNullOrWhiteSpace(propertyDto.Status) ? "Registered" : propertyDto.Status.Trim(),
            DateRegisteredUtc = propertyDto.DateRegisteredUtc ?? DateTime.UtcNow,
        };

        _dbContext.Properties.Add(property);
        await _dbContext.SaveChangesAsync();

        await _auditLogService.LogAsync("PropertyCreated", "Property", property.Id.ToString(), $"Registered property {property.Pin}");

        return MapProperty(await LoadPropertyAsync(property.Id));
    }

    public async Task<PropertyDto> RegisterAsync(RegisterPropertyDto propertyDto)
    {
        var barangay = await _dbContext.Barangays
            .Include(item => item.CityMunicipality)
            .ThenInclude(item => item.Province)
            .FirstOrDefaultAsync(item => item.Id == propertyDto.BarangayId)
            ?? throw new InvalidOperationException("The selected barangay could not be found.");

        if (barangay.CityMunicipalityId != propertyDto.CityMunicipalityId || barangay.CityMunicipality.ProvinceId != propertyDto.ProvinceId)
        {
            throw new InvalidOperationException("The selected province, city/municipality, and barangay do not match.");
        }

        var taxDeclarationNumber = NormalizeOptional(propertyDto.TaxDeclarationNumber);

        if (taxDeclarationNumber is not null && await _dbContext.Properties.AnyAsync(property => property.TaxDeclarationNumber == taxDeclarationNumber))
        {
            throw new InvalidOperationException("A property with the supplied tax declaration number already exists.");
        }

        var taxpayer = await ResolveTaxpayerAsync(new PropertyDto
        {
            OwnerName = propertyDto.OwnerName,
            OwnerEmail = propertyDto.OwnerEmail,
            OwnerPhone = propertyDto.OwnerPhone,
            OwnerAddress = string.IsNullOrWhiteSpace(propertyDto.OwnerAddress)
                ? $"{barangay.Name}, {barangay.CityMunicipality.Name}, {barangay.CityMunicipality.Province.Name}"
                : propertyDto.OwnerAddress,
            TaxIdentificationNumber = propertyDto.TaxIdentificationNumber,
        });

        var propertyType = propertyDto.PropertyType.Trim();
        var pin = await GenerateUniquePinAsync(barangay.CityMunicipality, propertyType);
        var lotNumber = propertyDto.LotNumber.Trim();
        var propertyAddress = $"{lotNumber}, {barangay.Name}, {barangay.CityMunicipality.Name}, {barangay.CityMunicipality.Province.Name}, Region XI";
        var property = new Property
        {
            Taxpayer = taxpayer,
            BarangayId = barangay.Id,
            Pin = pin,
            TaxDeclarationNumber = taxDeclarationNumber,
            Barangay = barangay.Name,
            Municipality = barangay.CityMunicipality.Name,
            Address = propertyAddress,
            PropertyType = propertyType,
            LotNumber = lotNumber,
            AreaSquareMeters = propertyDto.AreaSquareMeters,
            MarketValue = propertyDto.MarketValue,
            AssessmentLevel = GetAssessmentLevel(propertyType),
            TaxRate = GetTaxRate(propertyType),
            ZoningClassification = NormalizeOptional(propertyDto.ZoningClassification),
            Remarks = NormalizeOptional(propertyDto.Remarks),
            Status = "Pending Review",
            DateRegisteredUtc = DateTime.UtcNow,
        };

        _dbContext.Properties.Add(property);
        await _dbContext.SaveChangesAsync();

        await _auditLogService.LogAsync("PropertyRegistered", "Property", property.Id.ToString(), $"Registered property {property.Pin} through location form");

        return MapProperty(await LoadPropertyAsync(property.Id));
    }

    public async Task<PropertyDto?> UpdateAsync(int id, PropertyDto propertyDto)
    {
        var property = await _dbContext.Properties
            .Include(existing => existing.Taxpayer)
            .FirstOrDefaultAsync(existing => existing.Id == id);

        if (property is null)
        {
            return null;
        }

        var duplicatePinExists = await _dbContext.Properties.AnyAsync(existing =>
            existing.Id != id && existing.Pin == propertyDto.Pin.Trim());

        if (duplicatePinExists)
        {
            throw new InvalidOperationException("A different property already uses the supplied PIN.");
        }

        var taxDeclarationNumber = NormalizeOptional(propertyDto.TaxDeclarationNumber);
        var duplicateTaxDeclarationExists = taxDeclarationNumber is not null && await _dbContext.Properties.AnyAsync(existing =>
            existing.Id != id && existing.TaxDeclarationNumber == taxDeclarationNumber);

        if (duplicateTaxDeclarationExists)
        {
            throw new InvalidOperationException("A different property already uses the supplied tax declaration number.");
        }

        var normalizedBarangay = await ResolveBarangayForUpdateAsync(propertyDto, property);

        await ResolveTaxpayerAsync(propertyDto, property.Taxpayer);

        property.Pin = propertyDto.Pin.Trim();
        property.TaxDeclarationNumber = taxDeclarationNumber;
        property.PropertyType = propertyDto.PropertyType.Trim();
        property.LotNumber = propertyDto.LotNumber.Trim();
        property.AreaSquareMeters = propertyDto.AreaSquareMeters;
        property.MarketValue = propertyDto.MarketValue;
        property.AssessmentLevel = propertyDto.AssessmentLevel;
        property.TaxRate = propertyDto.TaxRate;
        property.ZoningClassification = NormalizeOptional(propertyDto.ZoningClassification);
        property.Remarks = NormalizeOptional(propertyDto.Remarks);
        property.Status = string.IsNullOrWhiteSpace(propertyDto.Status) ? property.Status : propertyDto.Status.Trim();
        property.DateRegisteredUtc = propertyDto.DateRegisteredUtc ?? property.DateRegisteredUtc;
        property.UpdatedAtUtc = DateTime.UtcNow;

        if (normalizedBarangay is not null)
        {
            property.BarangayId = normalizedBarangay.Id;
            property.Barangay = normalizedBarangay.Name;
            property.Municipality = normalizedBarangay.CityMunicipality.Name;
            property.Address = $"{property.LotNumber}, {normalizedBarangay.Name}, {normalizedBarangay.CityMunicipality.Name}, {normalizedBarangay.CityMunicipality.Province.Name}, Region XI";
        }
        else
        {
            property.BarangayId = propertyDto.BarangayId ?? property.BarangayId;
            property.Barangay = propertyDto.Barangay.Trim();
            property.Municipality = string.IsNullOrWhiteSpace(propertyDto.Municipality)
                ? property.Municipality
                : propertyDto.Municipality.Trim();
            property.Address = propertyDto.Address.Trim();
        }

        await _dbContext.SaveChangesAsync();
        await _auditLogService.LogAsync("PropertyUpdated", "Property", property.Id.ToString(), $"Updated property {property.Pin}");

        return MapProperty(await LoadPropertyAsync(property.Id));
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var property = await _dbContext.Properties.FirstOrDefaultAsync(existing => existing.Id == id);

        if (property is null)
        {
            return false;
        }

        _dbContext.Properties.Remove(property);
        await _dbContext.SaveChangesAsync();
        await _auditLogService.LogAsync("PropertyDeleted", "Property", id.ToString(), $"Deleted property {property.Pin}");

        return true;
    }

    private async Task<Property> LoadPropertyAsync(int propertyId)
    {
        return await _dbContext.Properties
            .Include(property => property.Taxpayer)
            .Include(property => property.BarangayLocation)
            .ThenInclude(barangay => barangay!.CityMunicipality)
            .ThenInclude(cityMunicipality => cityMunicipality!.Province)
            .FirstAsync(property => property.Id == propertyId);
    }

    private async Task<Barangay?> ResolveBarangayForUpdateAsync(PropertyDto propertyDto, Property property)
    {
        if (!propertyDto.BarangayId.HasValue)
        {
            if (property.BarangayId.HasValue && !string.Equals(propertyDto.Barangay.Trim(), property.Barangay, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Changing the barangay requires selecting a valid province, city/municipality, and barangay.");
            }

            return null;
        }

        var barangay = await _dbContext.Barangays
            .Include(item => item.CityMunicipality)
            .ThenInclude(item => item.Province)
            .FirstOrDefaultAsync(item => item.Id == propertyDto.BarangayId.Value)
            ?? throw new InvalidOperationException("The selected barangay could not be found.");

        if (propertyDto.CityMunicipalityId.HasValue && barangay.CityMunicipalityId != propertyDto.CityMunicipalityId.Value)
        {
            throw new InvalidOperationException("The selected city/municipality does not match the selected barangay.");
        }

        if (propertyDto.ProvinceId.HasValue && barangay.CityMunicipality.ProvinceId != propertyDto.ProvinceId.Value)
        {
            throw new InvalidOperationException("The selected province does not match the selected barangay.");
        }

        return barangay;
    }

    private async Task<Taxpayer> ResolveTaxpayerAsync(PropertyDto propertyDto, Taxpayer? existingTaxpayer = null)
    {
        Taxpayer taxpayer;

        if (existingTaxpayer is not null)
        {
            taxpayer = existingTaxpayer;
        }
        else if (propertyDto.TaxpayerId.HasValue)
        {
            taxpayer = await _dbContext.Taxpayers.FirstOrDefaultAsync(item => item.Id == propertyDto.TaxpayerId.Value)
                ?? throw new InvalidOperationException("The supplied taxpayer could not be found.");
        }
        else if (!string.IsNullOrWhiteSpace(propertyDto.OwnerEmail))
        {
            taxpayer = await _dbContext.Taxpayers.FirstOrDefaultAsync(item => item.Email == propertyDto.OwnerEmail.Trim())
                ?? new Taxpayer();
        }
        else
        {
            taxpayer = await _dbContext.Taxpayers.FirstOrDefaultAsync(item =>
                item.FullName == propertyDto.OwnerName.Trim() && item.PhoneNumber == propertyDto.OwnerPhone)
                ?? new Taxpayer();
        }

        taxpayer.FullName = propertyDto.OwnerName.Trim();
        taxpayer.Email = string.IsNullOrWhiteSpace(propertyDto.OwnerEmail) ? null : propertyDto.OwnerEmail.Trim();
        taxpayer.PhoneNumber = string.IsNullOrWhiteSpace(propertyDto.OwnerPhone) ? null : propertyDto.OwnerPhone.Trim();
        taxpayer.Address = string.IsNullOrWhiteSpace(propertyDto.OwnerAddress) ? null : propertyDto.OwnerAddress.Trim();
        taxpayer.TaxIdentificationNumber = string.IsNullOrWhiteSpace(propertyDto.TaxIdentificationNumber)
            ? null
            : propertyDto.TaxIdentificationNumber.Trim();

        if (taxpayer.Id == 0)
        {
            _dbContext.Taxpayers.Add(taxpayer);
        }

        return taxpayer;
    }

    private static PropertyDto MapProperty(Property property)
    {
        return new PropertyDto
        {
            Id = property.Id,
            TaxpayerId = property.TaxpayerId,
            ProvinceId = property.BarangayLocation?.CityMunicipality?.ProvinceId,
            CityMunicipalityId = property.BarangayLocation?.CityMunicipalityId,
            OwnerName = property.Taxpayer.FullName,
            OwnerEmail = property.Taxpayer.Email,
            OwnerPhone = property.Taxpayer.PhoneNumber,
            OwnerAddress = property.Taxpayer.Address,
            TaxIdentificationNumber = property.Taxpayer.TaxIdentificationNumber,
            BarangayId = property.BarangayId,
            Pin = property.Pin,
            TaxDeclarationNumber = property.TaxDeclarationNumber,
            Barangay = property.Barangay,
            Municipality = property.Municipality,
            Address = property.Address,
            PropertyType = property.PropertyType,
            LotNumber = property.LotNumber,
            AreaSquareMeters = property.AreaSquareMeters,
            MarketValue = property.MarketValue,
            AssessmentLevel = property.AssessmentLevel,
            TaxRate = property.TaxRate,
            ZoningClassification = property.ZoningClassification,
            Remarks = property.Remarks,
            Status = property.Status,
            DateRegisteredUtc = property.DateRegisteredUtc,
        };
    }

    private async Task<string> GenerateUniquePinAsync(CityMunicipality cityMunicipality, string propertyType)
    {
        var codeSegment = cityMunicipality.PsgcCode.Length > 6
            ? cityMunicipality.PsgcCode[^6..]
            : cityMunicipality.PsgcCode;
        var typeSegment = BuildPropertyTypeSegment(propertyType);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = $"11-{codeSegment}-{typeSegment}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{attempt:00}";

            if (!await _dbContext.Properties.AnyAsync(property => property.Pin == candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unable to generate a unique property identification number.");
    }

    private static string BuildPropertyTypeSegment(string propertyType)
    {
        var segment = new string(propertyType
            .Where(char.IsLetterOrDigit)
            .Take(3)
            .ToArray())
            .ToUpperInvariant();

        return segment.PadRight(3, 'X');
    }

    private static decimal GetAssessmentLevel(string propertyType)
    {
        return propertyType.Trim().ToLowerInvariant() switch
        {
            "commercial" => 50m,
            "agricultural" => 40m,
            "industrial" => 80m,
            "special" => 0m,
            _ => 20m,
        };
    }

    private static decimal GetTaxRate(string propertyType)
    {
        return propertyType.Trim().ToLowerInvariant() switch
        {
            "commercial" or "industrial" => 2m,
            "special" => 0m,
            _ => 1m,
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}