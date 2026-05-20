using Microsoft.EntityFrameworkCore;
using PropertyTax.API.Data;
using PropertyTax.API.DTOs;

namespace PropertyTax.API.Services;

public class LocationService
{
    private readonly AppDbContext _dbContext;

    public LocationService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyCollection<ProvinceDto>> GetProvincesAsync()
    {
        return await _dbContext.Provinces
            .AsNoTracking()
            .Where(province => province.RegionCode == "110000000")
            .OrderBy(province => province.Name)
            .Select(province => new ProvinceDto(province.Id, province.PsgcCode, province.Name))
            .ToListAsync();
    }

    public async Task<IReadOnlyCollection<CityMunicipalityDto>> GetCitiesAsync(int provinceId)
    {
        return await _dbContext.CitiesMunicipalities
            .AsNoTracking()
            .Where(city => city.ProvinceId == provinceId)
            .OrderBy(city => city.Name)
            .Select(city => new CityMunicipalityDto(city.Id, city.ProvinceId, city.PsgcCode, city.Name, city.LguType))
            .ToListAsync();
    }

    public async Task<IReadOnlyCollection<BarangayDto>> SearchBarangaysAsync(int cityId, string? search)
    {
        var query = _dbContext.Barangays
            .AsNoTracking()
            .Include(barangay => barangay.CityMunicipality)
            .ThenInclude(city => city.Province)
            .Where(barangay => barangay.CityMunicipalityId == cityId);

        var trimmedSearch = search?.Trim();

        if (!string.IsNullOrWhiteSpace(trimmedSearch))
        {
            query = query.Where(barangay => EF.Functions.Like(barangay.Name, $"%{trimmedSearch}%"));
        }

        var barangays = await query
            .Select(barangay => new BarangayDto(
                barangay.Id,
                barangay.CityMunicipalityId,
                barangay.PsgcCode,
                barangay.Name,
                barangay.CityMunicipality.Name,
                barangay.CityMunicipality.Province.Name))
            .ToListAsync();

        return barangays
            .OrderBy(barangay => barangay.Name, NaturalStringComparer.Instance)
            .ThenBy(barangay => barangay.PsgcCode, StringComparer.Ordinal)
            .ToList();
    }

    private sealed class NaturalStringComparer : IComparer<string>
    {
        public static NaturalStringComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var leftIndex = 0;
            var rightIndex = 0;

            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                var leftIsDigit = char.IsDigit(left[leftIndex]);
                var rightIsDigit = char.IsDigit(right[rightIndex]);

                if (leftIsDigit && rightIsDigit)
                {
                    var leftNumberStart = leftIndex;
                    var rightNumberStart = rightIndex;

                    while (leftIndex < left.Length && char.IsDigit(left[leftIndex]))
                    {
                        leftIndex++;
                    }

                    while (rightIndex < right.Length && char.IsDigit(right[rightIndex]))
                    {
                        rightIndex++;
                    }

                    var leftDigits = TrimLeadingZeroes(left.AsSpan(leftNumberStart, leftIndex - leftNumberStart));
                    var rightDigits = TrimLeadingZeroes(right.AsSpan(rightNumberStart, rightIndex - rightNumberStart));

                    var lengthComparison = leftDigits.Length.CompareTo(rightDigits.Length);

                    if (lengthComparison != 0)
                    {
                        return lengthComparison;
                    }

                    var digitComparison = leftDigits.SequenceCompareTo(rightDigits);

                    if (digitComparison != 0)
                    {
                        return digitComparison;
                    }

                    continue;
                }

                var characterComparison = char.ToUpperInvariant(left[leftIndex]).CompareTo(char.ToUpperInvariant(right[rightIndex]));

                if (characterComparison != 0)
                {
                    return characterComparison;
                }

                leftIndex++;
                rightIndex++;
            }

            return left.Length.CompareTo(right.Length);
        }

        private static ReadOnlySpan<char> TrimLeadingZeroes(ReadOnlySpan<char> digits)
        {
            var index = 0;

            while (index < digits.Length - 1 && digits[index] == '0')
            {
                index++;
            }

            return digits[index..];
        }
    }
}