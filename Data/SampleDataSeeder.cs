using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PropertyTax.API.Models;

namespace PropertyTax.API.Data;

public class SampleDataSeeder
{
    private const int DefaultOwnerCount = 60;
    private const int MaxOwnerCount = 5000;
    private const int MaxPropertyCount = 10000;
    private const int MaxAssessmentCount = 30000;
    private const int MaxPaymentCount = 30000;
    private const int MaxDocumentCount = 20000;
    private const int StableBarangaySampleCount = 80;
    private const string SampleEmailDomain = "sample.taxsync.local";
    private const string DefaultSamplePassword = "TaxSync#2026";

    private static readonly byte[] SamplePdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n");

    private static readonly string[] GivenNames =
    [
        "Maria Teresa", "Jose Miguel", "Ana Lourdes", "Carlos Eduardo", "Grace Ann",
        "Ramon Luis", "Catherine Joy", "Antonio Rafael", "Marites", "Roberto",
        "Elena Marie", "Francisco", "Lorna Mae", "Danilo", "Rosalinda",
        "Benjamin", "Michelle", "Eduardo", "Aileen", "Renato",
    ];

    private static readonly string[] Surnames =
    [
        "Santos", "Reyes", "Dela Cruz", "Garcia", "Mendoza",
        "Torres", "Ramos", "Bautista", "Flores", "Villanueva",
        "Castillo", "Fernandez", "Gonzales", "Navarro", "Pascual",
        "Soriano", "Aquino", "Salazar", "Domingo", "Valdez",
    ];

    private static readonly string[] Streets =
    [
        "Rizal", "Bonifacio", "Mabini", "Quezon", "Roxas",
        "Aguinaldo", "Jacinto", "Del Pilar", "Malvar", "Osmena",
    ];

    private static readonly string[] PropertyTypes = ["Residential", "Commercial", "Agricultural", "Industrial"];

    private static readonly string[] PropertyStatuses = ["Registered", "Active", "Assessed", "Pending Review"];

    private static readonly string[] PaymentMethods = ["Cash", "Check", "Bank Deposit"];

    private static readonly string[] Banks =
    [
        "Land Bank of the Philippines",
        "Development Bank of the Philippines",
        "Bank of the Philippine Islands",
        "Metrobank",
    ];

    private static readonly (string Folder, string Slug)[] DocumentTemplates =
    [
        ("Tax Declarations", "tax-declaration"),
        ("Assessment Records", "assessment-record"),
        ("Payment Records", "payment-record"),
        ("Compliance Notes", "compliance-note"),
    ];

    private readonly AppDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<SampleDataSeeder> _logger;

    public SampleDataSeeder(
        AppDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<SampleDataSeeder> logger)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public async Task SeedAsync()
    {
        if (!ShouldSeedSampleData())
        {
            return;
        }

        var targets = GetSeedTargets();
        var sampleUsersByRole = await SeedSampleUsersAsync();
        var barangays = await _dbContext.Barangays
            .Include(barangay => barangay.CityMunicipality)
            .ThenInclude(cityMunicipality => cityMunicipality.Province)
            .OrderBy(barangay => barangay.CityMunicipality.Province.Name)
            .ThenBy(barangay => barangay.CityMunicipality.Name)
            .ThenBy(barangay => barangay.Name)
            .Take(StableBarangaySampleCount)
            .ToListAsync();

        if (barangays.Count == 0)
        {
            _logger.LogWarning("Sample data seeding skipped because no barangay locations are available.");
            return;
        }

        var ownerSeeds = BuildOwnerSeeds(targets.OwnerCount, barangays);
        var taxpayersByEmail = await SeedTaxpayersAsync(ownerSeeds);
        var propertyRecords = await SeedPropertiesAsync(ownerSeeds, taxpayersByEmail, targets.PropertyCount);

        sampleUsersByRole.TryGetValue(SystemRoles.Accountant, out var accountantUser);
        sampleUsersByRole.TryGetValue(SystemRoles.Staff, out var staffUser);

        var assessments = await SeedAssessmentsAsync(propertyRecords, targets.AssessmentCount, accountantUser?.Id);
        var payments = await SeedPaymentsAsync(propertyRecords, assessments, targets.PaymentCount, accountantUser?.Id);
        var documents = await SeedDocumentsAsync(propertyRecords, targets.DocumentCount, staffUser?.Id);

        await RemoveExtraSampleDataAsync(ownerSeeds, propertyRecords, assessments, payments, documents);
        await ReplaceSampleAuditLogsAsync(propertyRecords, sampleUsersByRole);

        _logger.LogInformation(
            "Seeded development sample data: {OwnerCount} owners, {PropertyCount} properties, {AssessmentCount} assessments, {PaymentCount} payments, {DocumentCount} documents.",
            ownerSeeds.Count,
            propertyRecords.Count,
            assessments.Count,
            payments.Count,
            documents.Count);
    }

    private bool ShouldSeedSampleData()
    {
        var enabledSetting = _configuration["SeedSampleData:Enabled"];

        return bool.TryParse(enabledSetting, out var enabled)
            ? enabled
            : _environment.IsDevelopment();
    }

    private SampleSeedTargets GetSeedTargets()
    {
        var ownerCount = GetConfiguredCount("OwnerCount", DefaultOwnerCount, 20, MaxOwnerCount);
        var propertyCount = GetConfiguredCount("PropertyCount", GetLegacyPropertyCount(ownerCount), 0, MaxPropertyCount);
        var assessmentCount = GetConfiguredCount("AssessmentCount", propertyCount * 3, 0, MaxAssessmentCount);
        var paymentCount = GetConfiguredCount("PaymentCount", GetLegacyPaymentCount(propertyCount), 0, MaxPaymentCount);
        var documentCount = GetConfiguredCount("DocumentCount", GetLegacyDocumentCount(propertyCount), 0, MaxDocumentCount);

        return new SampleSeedTargets(ownerCount, propertyCount, assessmentCount, paymentCount, documentCount);
    }

    private int GetConfiguredCount(string settingName, int defaultValue, int minValue, int maxValue)
    {
        return int.TryParse(_configuration[$"SeedSampleData:{settingName}"], out var configuredCount)
            ? Math.Clamp(configuredCount, minValue, maxValue)
            : Math.Clamp(defaultValue, minValue, maxValue);
    }

    private async Task<Dictionary<string, ApplicationUser>> SeedSampleUsersAsync()
    {
        var userSeeds = new[]
        {
            new SampleUserSeed("staff@taxsync.gov.ph", "Ma. Elena Davao", SystemRoles.Staff),
            new SampleUserSeed("accountant@taxsync.gov.ph", "Rafael Santos", SystemRoles.Accountant),
            new SampleUserSeed("auditor@taxsync.gov.ph", "Lourdes Navarro", SystemRoles.Auditor),
        };

        var usersByRole = new Dictionary<string, ApplicationUser>(StringComparer.OrdinalIgnoreCase);
        var password = _configuration["SeedSampleData:UserPassword"] ?? DefaultSamplePassword;

        foreach (var seed in userSeeds)
        {
            var user = await _userManager.FindByEmailAsync(seed.Email)
                ?? await _userManager.FindByNameAsync(seed.Email);

            if (user is null)
            {
                user = new ApplicationUser
                {
                    UserName = seed.Email,
                    Email = seed.Email,
                    FullName = seed.FullName,
                    EmailConfirmed = true,
                    IsActive = true,
                    CreatedAtUtc = DateTime.UtcNow,
                };

                var createResult = await _userManager.CreateAsync(user, password);

                if (!createResult.Succeeded)
                {
                    _logger.LogError("Failed to seed sample user {Email}: {Errors}", seed.Email, FormatErrors(createResult));
                    continue;
                }
            }
            else
            {
                var changed = false;

                if (!string.Equals(user.FullName, seed.FullName, StringComparison.Ordinal))
                {
                    user.FullName = seed.FullName;
                    changed = true;
                }

                if (!user.EmailConfirmed)
                {
                    user.EmailConfirmed = true;
                    changed = true;
                }

                if (!user.IsActive)
                {
                    user.IsActive = true;
                    changed = true;
                }

                if (!await _userManager.HasPasswordAsync(user))
                {
                    var addPasswordResult = await _userManager.AddPasswordAsync(user, password);

                    if (!addPasswordResult.Succeeded)
                    {
                        _logger.LogError("Failed to add password for sample user {Email}: {Errors}", seed.Email, FormatErrors(addPasswordResult));
                    }
                }

                if (changed)
                {
                    var updateResult = await _userManager.UpdateAsync(user);

                    if (!updateResult.Succeeded)
                    {
                        _logger.LogError("Failed to update sample user {Email}: {Errors}", seed.Email, FormatErrors(updateResult));
                    }
                }
            }

            if (!await _userManager.IsInRoleAsync(user, seed.Role))
            {
                var roleResult = await _userManager.AddToRoleAsync(user, seed.Role);

                if (!roleResult.Succeeded)
                {
                    _logger.LogError("Failed to assign {Role} role to {Email}: {Errors}", seed.Role, seed.Email, FormatErrors(roleResult));
                }
            }

            usersByRole[seed.Role] = user;
        }

        return usersByRole;
    }

    private static List<SampleOwnerSeed> BuildOwnerSeeds(int ownerCount, IReadOnlyList<Barangay> barangays)
    {
        var ownerSeeds = new List<SampleOwnerSeed>(ownerCount);
        var now = DateTime.UtcNow.Date;

        for (var index = 1; index <= ownerCount; index++)
        {
            var location = barangays[(index * 7) % barangays.Count];
            var city = location.CityMunicipality;
            var province = city.Province;
            var fullName = $"{GivenNames[(index - 1) % GivenNames.Length]} {Surnames[(index * 3) % Surnames.Length]}";
            var street = Streets[(index * 5) % Streets.Length];
            var address = $"{100 + index} {street} St., {location.Name}, {city.Name}, {province.Name}, Region XI";

            ownerSeeds.Add(new SampleOwnerSeed(
                index,
                fullName,
                $"owner{index:000}@{SampleEmailDomain}",
                $"09{17 + (index % 70):00}-{(200 + index * 3) % 1000:000}-{(1000 + index * 137) % 10000:0000}",
                address,
                $"{900 + (index % 50):000}-{100 + ((index * 7) % 900):000}-{100 + ((index * 11) % 900):000}-{index % 10:000}",
                now.AddDays(-ownerCount - index),
                location));
        }

        return ownerSeeds;
    }

    private async Task<Dictionary<string, Taxpayer>> SeedTaxpayersAsync(IReadOnlyCollection<SampleOwnerSeed> ownerSeeds)
    {
        var emails = ownerSeeds.Select(seed => seed.Email).ToList();
        var existingTaxpayers = await _dbContext.Taxpayers
            .Where(taxpayer => taxpayer.Email != null && emails.Contains(taxpayer.Email!))
            .ToListAsync();
        var taxpayersByEmail = existingTaxpayers
            .ToDictionary(taxpayer => taxpayer.Email!, StringComparer.OrdinalIgnoreCase);

        foreach (var seed in ownerSeeds)
        {
            if (!taxpayersByEmail.TryGetValue(seed.Email, out var taxpayer))
            {
                taxpayer = new Taxpayer();
                _dbContext.Taxpayers.Add(taxpayer);
                taxpayersByEmail[seed.Email] = taxpayer;
            }

            taxpayer.FullName = seed.FullName;
            taxpayer.Email = seed.Email;
            taxpayer.PhoneNumber = seed.PhoneNumber;
            taxpayer.Address = seed.Address;
            taxpayer.TaxIdentificationNumber = seed.TaxIdentificationNumber;
            taxpayer.CreatedAtUtc = seed.CreatedAtUtc;
        }

        await _dbContext.SaveChangesAsync();

        return taxpayersByEmail;
    }

    private async Task<List<SamplePropertyRecord>> SeedPropertiesAsync(
        IReadOnlyCollection<SampleOwnerSeed> ownerSeeds,
        IReadOnlyDictionary<string, Taxpayer> taxpayersByEmail,
        int propertyCount)
    {
        var propertySeeds = BuildPropertySeeds(ownerSeeds, taxpayersByEmail, propertyCount);

        if (propertySeeds.Count == 0)
        {
            return [];
        }

        var existingProperties = await _dbContext.Properties
            .Where(property => property.TaxDeclarationNumber != null && property.TaxDeclarationNumber.StartsWith("TD-11-SMP-"))
            .ToListAsync();
        var propertiesByPin = existingProperties.ToDictionary(property => property.Pin, StringComparer.OrdinalIgnoreCase);
        var propertiesByTaxDeclarationNumber = existingProperties
            .Where(property => !string.IsNullOrWhiteSpace(property.TaxDeclarationNumber))
            .ToDictionary(property => property.TaxDeclarationNumber!, StringComparer.OrdinalIgnoreCase);

        foreach (var seed in propertySeeds)
        {
            if (!propertiesByPin.TryGetValue(seed.Pin, out var property) &&
                !propertiesByTaxDeclarationNumber.TryGetValue(seed.TaxDeclarationNumber, out property))
            {
                property = new Property
                {
                    Pin = seed.Pin,
                    TaxDeclarationNumber = seed.TaxDeclarationNumber,
                };
                _dbContext.Properties.Add(property);
            }

            property.TaxpayerId = seed.Taxpayer.Id;
            property.BarangayId = seed.Location.Id;
            property.Pin = seed.Pin;
            property.TaxDeclarationNumber = seed.TaxDeclarationNumber;
            property.Barangay = seed.Location.Name;
            property.Municipality = seed.Location.CityMunicipality.Name;
            property.Address = seed.Address;
            property.PropertyType = seed.PropertyType;
            property.LotNumber = seed.LotNumber;
            property.AreaSquareMeters = seed.AreaSquareMeters;
            property.MarketValue = seed.MarketValue;
            property.AssessmentLevel = seed.AssessmentLevel;
            property.TaxRate = seed.TaxRate;
            property.ZoningClassification = seed.ZoningClassification;
            property.Remarks = seed.Remarks;
            property.Status = seed.Status;
            property.DateRegisteredUtc = seed.DateRegisteredUtc;
            property.CreatedAtUtc = seed.CreatedAtUtc;
            property.UpdatedAtUtc = DateTime.UtcNow;

            propertiesByPin[seed.Pin] = property;
            propertiesByTaxDeclarationNumber[seed.TaxDeclarationNumber] = property;
        }

        await _dbContext.SaveChangesAsync();

        return propertySeeds
            .Select(seed => new SamplePropertyRecord(seed, propertiesByPin[seed.Pin]))
            .ToList();
    }

    private static List<SamplePropertySeed> BuildPropertySeeds(
        IReadOnlyCollection<SampleOwnerSeed> ownerSeeds,
        IReadOnlyDictionary<string, Taxpayer> taxpayersByEmail,
        int propertyCount)
    {
        if (propertyCount <= 0)
        {
            return [];
        }

        var orderedOwnerSeeds = ownerSeeds
            .OrderBy(seed => seed.Sequence)
            .ToList();

        if (orderedOwnerSeeds.Count == 0)
        {
            return [];
        }

        var propertySeeds = new List<SamplePropertySeed>(propertyCount);

        for (var sequence = 1; sequence <= propertyCount; sequence++)
        {
            var ownerSeed = orderedOwnerSeeds[(sequence - 1) % orderedOwnerSeeds.Count];
            var propertyType = PropertyTypes[(sequence - 1) % PropertyTypes.Length];
            var assessmentLevel = GetAssessmentLevel(propertyType);
            var taxRate = GetTaxRate(propertyType);
            var area = GetAreaSquareMeters(propertyType, sequence);
            var marketValue = GetMarketValue(propertyType, area, sequence);
            var cityCode = ownerSeed.Location.CityMunicipality.PsgcCode;
            var citySegment = cityCode.Length > 6 ? cityCode[^6..] : cityCode;
            var lotNumber = $"LOT-{citySegment}-{sequence:0000}";
            var address = $"{lotNumber}, {ownerSeed.Location.Name}, {ownerSeed.Location.CityMunicipality.Name}, {ownerSeed.Location.CityMunicipality.Province.Name}, Region XI";

            propertySeeds.Add(new SamplePropertySeed(
                sequence,
                taxpayersByEmail[ownerSeed.Email],
                ownerSeed.Location,
                $"XI-{citySegment}-{BuildPropertyTypeSegment(propertyType)}-{sequence:000000}",
                $"TD-11-SMP-{sequence:000000}",
                address,
                propertyType,
                lotNumber,
                area,
                marketValue,
                assessmentLevel,
                taxRate,
                GetZoningClassification(propertyType),
                $"Sample development record for {propertyType.ToLowerInvariant()} property in {ownerSeed.Location.CityMunicipality.Name}.",
                PropertyStatuses[(sequence - 1) % PropertyStatuses.Length],
                DateTime.UtcNow.Date.AddDays(-sequence * 3),
                DateTime.UtcNow.Date.AddDays(-sequence * 3 - 1)));
        }

        return propertySeeds;
    }

    private async Task<List<TaxAssessment>> SeedAssessmentsAsync(
        IReadOnlyCollection<SamplePropertyRecord> propertyRecords,
        int assessmentCount,
        string? calculatedByUserId)
    {
        var assessmentSeeds = BuildAssessmentSeeds(propertyRecords, assessmentCount, calculatedByUserId);

        if (assessmentSeeds.Count == 0)
        {
            return [];
        }

        var propertyIds = assessmentSeeds
            .Select(seed => seed.PropertyId)
            .Distinct()
            .ToList();
        var taxYears = assessmentSeeds
            .Select(seed => seed.TaxYear)
            .Distinct()
            .ToList();
        var existingAssessments = await _dbContext.TaxAssessments
            .Where(assessment => propertyIds.Contains(assessment.PropertyId) && taxYears.Contains(assessment.TaxYear))
            .ToListAsync();
        var assessmentsByPropertyYear = existingAssessments
            .GroupBy(assessment => (assessment.PropertyId, assessment.TaxYear))
            .ToDictionary(group => group.Key, group => group.First());
        var seededAssessments = new List<TaxAssessment>(assessmentSeeds.Count);

        foreach (var seed in assessmentSeeds)
        {
            if (!assessmentsByPropertyYear.TryGetValue((seed.PropertyId, seed.TaxYear), out var assessment))
            {
                assessment = new TaxAssessment
                {
                    PropertyId = seed.PropertyId,
                    TaxYear = seed.TaxYear,
                };
                _dbContext.TaxAssessments.Add(assessment);
                assessmentsByPropertyYear[(seed.PropertyId, seed.TaxYear)] = assessment;
            }

            assessment.MarketValue = seed.MarketValue;
            assessment.AssessmentLevel = seed.AssessmentLevel;
            assessment.AssessedValue = seed.AssessedValue;
            assessment.TaxRate = seed.TaxRate;
            assessment.TaxDue = seed.TaxDue;
            assessment.CalculatedByUserId = seed.CalculatedByUserId;
            assessment.CreatedAtUtc = seed.CreatedAtUtc;

            seededAssessments.Add(assessment);
        }

        await _dbContext.SaveChangesAsync();

        return seededAssessments;
    }

    private static List<AssessmentSeed> BuildAssessmentSeeds(
        IReadOnlyCollection<SamplePropertyRecord> propertyRecords,
        int assessmentCount,
        string? calculatedByUserId)
    {
        if (propertyRecords.Count == 0 || assessmentCount <= 0)
        {
            return [];
        }

        var orderedRecords = propertyRecords
            .OrderBy(record => record.Seed.Sequence)
            .ToList();
        var currentYear = DateTime.UtcNow.Year;
        var yearsRequired = Math.Max(1, (int)Math.Ceiling((double)assessmentCount / orderedRecords.Count));
        var earliestTaxYear = currentYear - yearsRequired + 1;
        var taxYears = Enumerable.Range(earliestTaxYear, yearsRequired).ToArray();
        var assessmentSeeds = new List<AssessmentSeed>(assessmentCount);

        foreach (var record in orderedRecords)
        {
            foreach (var taxYear in taxYears)
            {
                if (assessmentSeeds.Count == assessmentCount)
                {
                    return assessmentSeeds;
                }

                var marketValue = GetAssessmentMarketValue(record.Property.MarketValue, currentYear, taxYear, record.Seed.Sequence);
                var assessedValue = Math.Round(marketValue * NormalizeRate(record.Property.AssessmentLevel), 2, MidpointRounding.AwayFromZero);
                var taxDue = Math.Round(assessedValue * NormalizeRate(record.Property.TaxRate), 2, MidpointRounding.AwayFromZero);

                assessmentSeeds.Add(new AssessmentSeed(
                    record.Property.Id,
                    taxYear,
                    marketValue,
                    record.Property.AssessmentLevel,
                    assessedValue,
                    record.Property.TaxRate,
                    taxDue,
                    new DateTime(taxYear, 1, 10, 8, 0, 0, DateTimeKind.Utc).AddDays(record.Seed.Sequence % 20),
                    calculatedByUserId));
            }
        }

        return assessmentSeeds;
    }

    private async Task<List<Payment>> SeedPaymentsAsync(
        IReadOnlyCollection<SamplePropertyRecord> propertyRecords,
        IReadOnlyCollection<TaxAssessment> assessments,
        int paymentCount,
        string? recordedByUserId)
    {
        var paymentSeeds = BuildPaymentSeeds(propertyRecords, assessments, paymentCount, recordedByUserId);

        if (paymentSeeds.Count == 0)
        {
            return [];
        }

        var receiptNumbers = paymentSeeds.Select(seed => seed.OfficialReceiptNumber).ToList();
        var existingPayments = await _dbContext.Payments
            .Where(payment => receiptNumbers.Contains(payment.OfficialReceiptNumber))
            .ToListAsync();
        var paymentsByReceipt = existingPayments.ToDictionary(payment => payment.OfficialReceiptNumber, StringComparer.OrdinalIgnoreCase);

        foreach (var seed in paymentSeeds)
        {
            if (!paymentsByReceipt.TryGetValue(seed.OfficialReceiptNumber, out var payment))
            {
                payment = new Payment { OfficialReceiptNumber = seed.OfficialReceiptNumber };
                _dbContext.Payments.Add(payment);
                paymentsByReceipt[seed.OfficialReceiptNumber] = payment;
            }

            payment.PropertyId = seed.PropertyId;
            payment.TaxpayerId = seed.TaxpayerId;
            payment.TaxYear = seed.TaxYear;
            payment.Quarter = seed.Quarter;
            payment.AmountDue = seed.AmountDue;
            payment.AmountPaid = seed.AmountPaid;
            payment.PaymentMethod = seed.PaymentMethod;
            payment.ReferenceNumber = seed.ReferenceNumber;
            payment.BankName = seed.BankName;
            payment.PaymentDateUtc = seed.PaymentDateUtc;
            payment.DueDateUtc = seed.DueDateUtc;
            payment.Status = seed.Status;
            payment.Penalty = seed.Penalty;
            payment.Notes = seed.Notes;
            payment.RecordedByUserId = seed.RecordedByUserId;
            payment.CreatedAtUtc = seed.CreatedAtUtc;
        }

        await _dbContext.SaveChangesAsync();

        return paymentSeeds.Select(seed => paymentsByReceipt[seed.OfficialReceiptNumber]).ToList();
    }

    private static List<PaymentSeed> BuildPaymentSeeds(
        IReadOnlyCollection<SamplePropertyRecord> propertyRecords,
        IReadOnlyCollection<TaxAssessment> assessments,
        int paymentCount,
        string? recordedByUserId)
    {
        if (paymentCount <= 0 || assessments.Count == 0)
        {
            return [];
        }

        var recordsByPropertyId = propertyRecords.ToDictionary(record => record.Property.Id);
        var orderedAssessments = assessments
            .OrderBy(item => item.TaxYear)
            .ThenBy(item => recordsByPropertyId.TryGetValue(item.PropertyId, out var record) ? record.Seed.Sequence : item.PropertyId)
            .ToList();
        var paymentSeeds = new List<PaymentSeed>(paymentCount);
        var basePaymentsPerAssessment = paymentCount / orderedAssessments.Count;
        var assessmentsWithExtraPayment = paymentCount % orderedAssessments.Count;

        for (var assessmentIndex = 0; assessmentIndex < orderedAssessments.Count; assessmentIndex++)
        {
            var assessment = orderedAssessments[assessmentIndex];

            if (!recordsByPropertyId.TryGetValue(assessment.PropertyId, out var record) || assessment.TaxDue <= 0m)
            {
                continue;
            }

            var installmentCount = basePaymentsPerAssessment + (assessmentIndex < assessmentsWithExtraPayment ? 1 : 0);

            if (installmentCount == 0)
            {
                continue;
            }

            var installmentAmounts = SplitAmount(assessment.TaxDue, installmentCount);

            for (var installmentIndex = 0; installmentIndex < installmentCount; installmentIndex++)
            {
                var paymentSequence = installmentIndex + 1;
                var quarter = GetInstallmentQuarter(paymentSequence, installmentCount);
                var dueDateUtc = GetDueDateUtc(assessment.TaxYear, quarter);
                var paymentDateUtc = BuildInstallmentPaymentDate(assessment.TaxYear, paymentSequence, installmentCount, record.Seed.Sequence);

                paymentSeeds.Add(CreatePaymentSeed(
                    record,
                    assessment,
                    paymentSequence,
                    quarter,
                    installmentAmounts[installmentIndex],
                    installmentAmounts[installmentIndex],
                    "Paid",
                    0m,
                    dueDateUtc,
                    paymentDateUtc,
                    recordedByUserId,
                    installmentCount == 1
                        ? "Sample annual real property tax payment."
                        : $"Sample installment payment {paymentSequence} of {installmentCount}."));
            }
        }

        return paymentSeeds;
    }

    private async Task<List<PropertyDocument>> SeedDocumentsAsync(
        IReadOnlyCollection<SamplePropertyRecord> propertyRecords,
        int documentCount,
        string? uploadedByUserId)
    {
        var documentSeeds = BuildDocumentSeeds(propertyRecords, documentCount, uploadedByUserId);

        if (documentSeeds.Count == 0)
        {
            return [];
        }

        var fileNames = documentSeeds.Select(seed => seed.FileName).ToList();
        var existingDocuments = await _dbContext.PropertyDocuments
            .Where(document => fileNames.Contains(document.FileName))
            .ToListAsync();
        var documentsByFileName = existingDocuments.ToDictionary(document => document.FileName, StringComparer.OrdinalIgnoreCase);

        foreach (var seed in documentSeeds)
        {
            if (!documentsByFileName.TryGetValue(seed.FileName, out var document))
            {
                document = new PropertyDocument { FileName = seed.FileName };
                _dbContext.PropertyDocuments.Add(document);
                documentsByFileName[seed.FileName] = document;
            }

            document.PropertyId = seed.PropertyId;
            document.OriginalFileName = seed.OriginalFileName;
            document.RelativePath = seed.RelativePath;
            document.ContentType = seed.ContentType;
            document.SizeInBytes = seed.SizeInBytes;
            document.Folder = seed.Folder;
            document.UploadedAtUtc = seed.UploadedAtUtc;
            document.UploadedByUserId = seed.UploadedByUserId;

            EnsureSampleDocumentFile(seed.RelativePath);
        }

        await _dbContext.SaveChangesAsync();

        return documentSeeds.Select(seed => documentsByFileName[seed.FileName]).ToList();
    }

    private static List<DocumentSeed> BuildDocumentSeeds(
        IReadOnlyCollection<SamplePropertyRecord> propertyRecords,
        int documentCount,
        string? uploadedByUserId)
    {
        if (propertyRecords.Count == 0 || documentCount <= 0)
        {
            return [];
        }

        var orderedRecords = propertyRecords
            .OrderBy(record => record.Seed.Sequence)
            .ToList();
        var baseDocumentsPerProperty = documentCount / orderedRecords.Count;
        var propertiesWithExtraDocument = documentCount % orderedRecords.Count;
        var documentSeeds = new List<DocumentSeed>(documentCount);

        for (var propertyIndex = 0; propertyIndex < orderedRecords.Count; propertyIndex++)
        {
            var record = orderedRecords[propertyIndex];
            var baseUploadDate = record.Property.DateRegisteredUtc.AddDays(5);
            var documentSlots = baseDocumentsPerProperty + (propertyIndex < propertiesWithExtraDocument ? 1 : 0);

            for (var documentIndex = 0; documentIndex < documentSlots; documentIndex++)
            {
                var documentSequence = documentIndex + 1;
                var template = DocumentTemplates[(documentSequence - 1) % DocumentTemplates.Length];

                documentSeeds.Add(CreateDocumentSeed(
                    record,
                    template.Folder,
                    template.Slug,
                    baseUploadDate.AddDays(documentIndex),
                    uploadedByUserId,
                    documentSequence));
            }
        }

        return documentSeeds;
    }

    private async Task ReplaceSampleAuditLogsAsync(
        IReadOnlyCollection<SamplePropertyRecord> propertyRecords,
        IReadOnlyDictionary<string, ApplicationUser> sampleUsersByRole)
    {
        var sampleAuditLogs = await _dbContext.AuditLogs
            .Where(log => log.Description != null && log.Description.StartsWith("Sample seed:"))
            .ToListAsync();

        if (sampleAuditLogs.Count > 0)
        {
            _dbContext.AuditLogs.RemoveRange(sampleAuditLogs);
            await _dbContext.SaveChangesAsync();
        }

        await SeedAuditLogsAsync(propertyRecords, sampleUsersByRole);
    }

    private async Task RemoveExtraSampleDataAsync(
        IReadOnlyCollection<SampleOwnerSeed> ownerSeeds,
        IReadOnlyCollection<SamplePropertyRecord> propertyRecords,
        IReadOnlyCollection<TaxAssessment> assessments,
        IReadOnlyCollection<Payment> payments,
        IReadOnlyCollection<PropertyDocument> documents)
    {
        var targetOwnerEmails = ownerSeeds
            .Select(seed => seed.Email)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetPropertyPins = propertyRecords
            .Select(record => record.Property.Pin)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetAssessmentKeys = assessments
            .Select(assessment => (assessment.PropertyId, assessment.TaxYear))
            .ToHashSet();
        var targetReceiptNumbers = payments
            .Select(payment => payment.OfficialReceiptNumber)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetDocumentFileNames = documents
            .Select(document => document.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sampleDocuments = await _dbContext.PropertyDocuments
            .Where(document => document.FileName.StartsWith("seed-"))
            .ToListAsync();
        var extraDocuments = sampleDocuments
            .Where(document => !targetDocumentFileNames.Contains(document.FileName))
            .ToList();

        foreach (var document in extraDocuments)
        {
            DeleteSampleDocumentFile(document.RelativePath);
        }

        if (extraDocuments.Count > 0)
        {
            _dbContext.PropertyDocuments.RemoveRange(extraDocuments);
            await _dbContext.SaveChangesAsync();
        }

        var samplePayments = await _dbContext.Payments
            .Where(payment => payment.OfficialReceiptNumber.StartsWith("OR-SEED-"))
            .ToListAsync();
        var extraPayments = samplePayments
            .Where(payment => !targetReceiptNumbers.Contains(payment.OfficialReceiptNumber))
            .ToList();

        if (extraPayments.Count > 0)
        {
            _dbContext.Payments.RemoveRange(extraPayments);
            await _dbContext.SaveChangesAsync();
        }

        var sampleAssessments = await _dbContext.TaxAssessments
            .Where(assessment => assessment.Property.TaxDeclarationNumber != null && assessment.Property.TaxDeclarationNumber.StartsWith("TD-11-SMP-"))
            .ToListAsync();
        var extraAssessments = sampleAssessments
            .Where(assessment => !targetAssessmentKeys.Contains((assessment.PropertyId, assessment.TaxYear)))
            .ToList();

        if (extraAssessments.Count > 0)
        {
            _dbContext.TaxAssessments.RemoveRange(extraAssessments);
            await _dbContext.SaveChangesAsync();
        }

        var sampleProperties = await _dbContext.Properties
            .Where(property => property.TaxDeclarationNumber != null && property.TaxDeclarationNumber.StartsWith("TD-11-SMP-"))
            .ToListAsync();
        var extraProperties = sampleProperties
            .Where(property => !targetPropertyPins.Contains(property.Pin))
            .ToList();

        if (extraProperties.Count > 0)
        {
            _dbContext.Properties.RemoveRange(extraProperties);
            await _dbContext.SaveChangesAsync();
        }

        var sampleTaxpayers = await _dbContext.Taxpayers
            .Where(taxpayer => taxpayer.Email != null && taxpayer.Email.EndsWith($"@{SampleEmailDomain}"))
            .ToListAsync();
        var extraTaxpayers = sampleTaxpayers
            .Where(taxpayer => !targetOwnerEmails.Contains(taxpayer.Email!))
            .ToList();

        if (extraTaxpayers.Count > 0)
        {
            _dbContext.Taxpayers.RemoveRange(extraTaxpayers);
            await _dbContext.SaveChangesAsync();
        }
    }

    private async Task SeedAuditLogsAsync(
        IReadOnlyCollection<SamplePropertyRecord> propertyRecords,
        IReadOnlyDictionary<string, ApplicationUser> sampleUsersByRole)
    {
        var propertyEntityIds = propertyRecords
            .Select(record => record.Property.Id.ToString())
            .ToList();

        var existingSampleLogEntityIds = await _dbContext.AuditLogs
            .Where(log =>
                log.EntityId != null &&
                propertyEntityIds.Contains(log.EntityId) &&
                log.Description != null &&
                log.Description.StartsWith("Sample seed:"))
            .Select(log => log.EntityId!)
            .Distinct()
            .ToListAsync();

        var existingSampleLogEntityIdSet = existingSampleLogEntityIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        sampleUsersByRole.TryGetValue(SystemRoles.Staff, out var staffUser);
        sampleUsersByRole.TryGetValue(SystemRoles.Accountant, out var accountantUser);
        sampleUsersByRole.TryGetValue(SystemRoles.Auditor, out var auditorUser);
        var actions = new[]
        {
            (Action: "PropertyRegistered", Entity: "Property", User: staffUser, Role: SystemRoles.Staff),
            (Action: "TaxCalculated", Entity: "TaxAssessment", User: accountantUser, Role: SystemRoles.Accountant),
            (Action: "PaymentRecorded", Entity: "Payment", User: accountantUser, Role: SystemRoles.Accountant),
            (Action: "DocumentUploaded", Entity: "Filing", User: staffUser, Role: SystemRoles.Staff),
            (Action: "ComplianceReviewed", Entity: "Compliance", User: auditorUser, Role: SystemRoles.Auditor),
        };

        foreach (var record in propertyRecords)
        {
            if (existingSampleLogEntityIdSet.Contains(record.Property.Id.ToString()))
            {
                continue;
            }

            var action = actions[record.Seed.Sequence % actions.Length];
            _dbContext.AuditLogs.Add(new AuditLog
            {
                Action = action.Action,
                EntityName = action.Entity,
                EntityId = record.Property.Id.ToString(),
                PerformedByUserId = action.User?.Id,
                PerformedByUsername = action.User?.Email,
                UserRole = action.Role,
                IpAddress = "127.0.0.1",
                Description = $"Sample seed: {action.Action} for property {record.Property.Pin}.",
                Succeeded = true,
                CreatedAtUtc = record.Property.CreatedAtUtc.AddHours(record.Seed.Sequence % 8),
            });
        }

        await _dbContext.SaveChangesAsync();
    }

    private void EnsureSampleDocumentFile(string relativePath)
    {
        var physicalPath = Path.Combine(_environment.ContentRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(physicalPath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(physicalPath))
        {
            File.WriteAllBytes(physicalPath, SamplePdfBytes);
        }
    }

    private void DeleteSampleDocumentFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        try
        {
            var physicalPath = Path.Combine(_environment.ContentRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(physicalPath))
            {
                File.Delete(physicalPath);
            }
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Unable to delete sample document file {RelativePath}", relativePath);
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(exception, "Unable to delete sample document file {RelativePath}", relativePath);
        }
    }

    private static PaymentSeed CreatePaymentSeed(
        SamplePropertyRecord record,
        TaxAssessment assessment,
        int sequence,
        string quarter,
        decimal amountDue,
        decimal amountPaid,
        string status,
        decimal penalty,
        DateTime dueDateUtc,
        DateTime paymentDateUtc,
        string? recordedByUserId,
        string notes)
    {
        var paymentMethod = PaymentMethods[(record.Seed.Sequence + assessment.TaxYear + sequence) % PaymentMethods.Length];
        var bankName = paymentMethod == "Cash"
            ? null
            : Banks[(record.Seed.Sequence + sequence) % Banks.Length];
        var referenceNumber = paymentMethod switch
        {
            "Check" => $"CHK-{assessment.TaxYear}-{record.Seed.Sequence:000000}-{sequence:00}",
            "Bank Deposit" => $"DEP-{assessment.TaxYear}-{record.Seed.Sequence:000000}-{sequence:00}",
            _ => null,
        };

        return new PaymentSeed(
            record.Property.Id,
            record.Property.TaxpayerId,
            assessment.TaxYear,
            quarter,
            amountDue,
            amountPaid,
            paymentMethod,
            referenceNumber,
            bankName,
            $"OR-SEED-{assessment.TaxYear}-{record.Seed.Sequence:000000}-{sequence:00}",
            paymentDateUtc,
            dueDateUtc,
            status,
            penalty,
            notes,
            paymentDateUtc.AddMinutes(15),
            recordedByUserId);
    }

    private static DocumentSeed CreateDocumentSeed(
        SamplePropertyRecord record,
        string folder,
        string slug,
        DateTime uploadedAtUtc,
        string? uploadedByUserId,
        int documentSequence)
    {
        var suffix = documentSequence > 1 ? $"-{documentSequence:00}" : string.Empty;
        var fileName = $"seed-{slug}-{record.Seed.Sequence:000000}{suffix}.pdf";

        return new DocumentSeed(
            record.Property.Id,
            fileName,
            $"{slug}-{record.Property.TaxDeclarationNumber}{suffix}.pdf",
            $"uploads/properties/{record.Property.Id}/{fileName}",
            "application/pdf",
            SamplePdfBytes.LongLength,
            folder,
            uploadedAtUtc,
            uploadedByUserId);
    }

    private static DateTime BuildInstallmentPaymentDate(int taxYear, int installmentSequence, int installmentCount, int propertySequence)
    {
        return GetInstallmentQuarter(installmentSequence, installmentCount) switch
        {
            "Q1" => BuildPastPaymentDate(taxYear, 3, 10, propertySequence, DateTime.UtcNow.Year),
            "Q2" => BuildPastPaymentDate(taxYear, 6, 10, propertySequence, DateTime.UtcNow.Year),
            "Q3" => BuildPastPaymentDate(taxYear, 9, 10, propertySequence, DateTime.UtcNow.Year),
            "Q4" => BuildPastPaymentDate(taxYear, 12, 10, propertySequence, DateTime.UtcNow.Year),
            _ => BuildPastPaymentDate(taxYear, 1, 12, propertySequence, DateTime.UtcNow.Year),
        };
    }

    private static string GetInstallmentQuarter(int installmentSequence, int installmentCount)
    {
        if (installmentCount <= 1)
        {
            return "Annual";
        }

        return installmentSequence switch
        {
            1 => "Q1",
            2 => "Q2",
            3 => "Q3",
            4 => "Q4",
            _ => "Annual",
        };
    }

    private static List<decimal> SplitAmount(decimal totalAmount, int partCount)
    {
        var totalCents = decimal.ToInt64(Math.Round(totalAmount * 100m, 0, MidpointRounding.AwayFromZero));
        var baseCents = totalCents / partCount;
        var remainderCents = totalCents % partCount;
        var parts = new List<decimal>(partCount);

        for (var index = 0; index < partCount; index++)
        {
            var cents = baseCents + (index < remainderCents ? 1 : 0);
            parts.Add(cents / 100m);
        }

        return parts;
    }

    private static DateTime BuildPastPaymentDate(int taxYear, int month, int day, int sequence, int currentYear)
    {
        var daysInMonth = DateTime.DaysInMonth(taxYear, month);
        var paymentDate = new DateTime(taxYear, month, Math.Min(daysInMonth, day + sequence % 6), 10, 0, 0, DateTimeKind.Utc);
        var now = DateTime.UtcNow;

        if (taxYear >= currentYear && paymentDate > now)
        {
            paymentDate = now.Date.AddDays(-Math.Max(1, sequence % 21)).AddHours(10);
        }

        return paymentDate;
    }

    private static DateTime GetDueDateUtc(int taxYear, string quarter)
    {
        return quarter switch
        {
            "Q1" => new DateTime(taxYear, 3, 31, 0, 0, 0, DateTimeKind.Utc),
            "Q2" => new DateTime(taxYear, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            "Q3" => new DateTime(taxYear, 9, 30, 0, 0, 0, DateTimeKind.Utc),
            "Q4" => new DateTime(taxYear, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            _ => new DateTime(taxYear, 1, 31, 0, 0, 0, DateTimeKind.Utc),
        };
    }

    private static int GetLegacyPropertyCount(int ownerCount)
    {
        var propertyCount = 0;

        for (var ownerSequence = 1; ownerSequence <= ownerCount; ownerSequence++)
        {
            propertyCount += GetLegacyPropertyCountForOwner(ownerSequence);
        }

        return propertyCount;
    }

    private static int GetLegacyPaymentCount(int propertyCount)
    {
        var currentYear = DateTime.UtcNow.Year;
        var paymentCount = 0;

        for (var propertySequence = 1; propertySequence <= propertyCount; propertySequence++)
        {
            for (var taxYear = currentYear - 2; taxYear <= currentYear; taxYear++)
            {
                var mode = (propertySequence + taxYear) % 6;

                paymentCount += mode switch
                {
                    0 => 0,
                    1 => 1,
                    2 => 2,
                    _ => 1,
                };
            }
        }

        return paymentCount;
    }

    private static int GetLegacyDocumentCount(int propertyCount)
    {
        return propertyCount + propertyCount / 4;
    }

    private static int GetLegacyPropertyCountForOwner(int ownerSequence)
    {
        if (ownerSequence % 5 == 0)
        {
            return 3;
        }

        return ownerSequence % 2 == 0 ? 2 : 1;
    }

    private static decimal GetAreaSquareMeters(string propertyType, int sequence)
    {
        return propertyType switch
        {
            "Commercial" => 220m + sequence * 8m,
            "Agricultural" => 5_000m + sequence * 125m,
            "Industrial" => 1_800m + sequence * 42m,
            _ => 120m + sequence * 5m,
        };
    }

    private static decimal GetMarketValue(string propertyType, decimal areaSquareMeters, int sequence)
    {
        var value = propertyType switch
        {
            "Commercial" => 2_500_000m + areaSquareMeters * 3_200m,
            "Agricultural" => 650_000m + areaSquareMeters * 95m,
            "Industrial" => 8_000_000m + areaSquareMeters * 2_100m,
            _ => 750_000m + areaSquareMeters * 4_800m,
        };

        return Math.Round(value + sequence * 12_500m, 2, MidpointRounding.AwayFromZero);
    }

    private static decimal GetAssessmentMarketValue(decimal currentMarketValue, int currentYear, int taxYear, int sequence)
    {
        var depreciationFactor = 1m - Math.Max(0, currentYear - taxYear) * 0.03m;
        return Math.Round(currentMarketValue * depreciationFactor + sequence * 1_250m, 2, MidpointRounding.AwayFromZero);
    }

    private static decimal GetAssessmentLevel(string propertyType)
    {
        return propertyType switch
        {
            "Commercial" => 50m,
            "Agricultural" => 40m,
            "Industrial" => 80m,
            _ => 20m,
        };
    }

    private static decimal GetTaxRate(string propertyType)
    {
        return propertyType is "Commercial" or "Industrial" ? 2m : 1m;
    }

    private static string GetZoningClassification(string propertyType)
    {
        return propertyType switch
        {
            "Commercial" => "Central Business District",
            "Agricultural" => "Agricultural Production Zone",
            "Industrial" => "Light Industrial Zone",
            _ => "Residential Zone",
        };
    }

    private static string BuildPropertyTypeSegment(string propertyType)
    {
        return propertyType[..Math.Min(propertyType.Length, 3)].ToUpperInvariant();
    }

    private static decimal NormalizeRate(decimal value)
    {
        return value > 1m ? value / 100m : value;
    }

    private static string FormatErrors(IdentityResult result)
    {
        return string.Join(", ", result.Errors.Select(error => error.Description));
    }

    private sealed record SampleUserSeed(string Email, string FullName, string Role);

    private sealed record SampleOwnerSeed(
        int Sequence,
        string FullName,
        string Email,
        string PhoneNumber,
        string Address,
        string TaxIdentificationNumber,
        DateTime CreatedAtUtc,
        Barangay Location);

    private sealed record SamplePropertySeed(
        int Sequence,
        Taxpayer Taxpayer,
        Barangay Location,
        string Pin,
        string TaxDeclarationNumber,
        string Address,
        string PropertyType,
        string LotNumber,
        decimal AreaSquareMeters,
        decimal MarketValue,
        decimal AssessmentLevel,
        decimal TaxRate,
        string ZoningClassification,
        string Remarks,
        string Status,
        DateTime DateRegisteredUtc,
        DateTime CreatedAtUtc);

    private sealed record SamplePropertyRecord(SamplePropertySeed Seed, Property Property);

    private sealed record AssessmentSeed(
        int PropertyId,
        int TaxYear,
        decimal MarketValue,
        decimal AssessmentLevel,
        decimal AssessedValue,
        decimal TaxRate,
        decimal TaxDue,
        DateTime CreatedAtUtc,
        string? CalculatedByUserId);

    private sealed record PaymentSeed(
        int PropertyId,
        int TaxpayerId,
        int TaxYear,
        string Quarter,
        decimal AmountDue,
        decimal AmountPaid,
        string PaymentMethod,
        string? ReferenceNumber,
        string? BankName,
        string OfficialReceiptNumber,
        DateTime PaymentDateUtc,
        DateTime DueDateUtc,
        string Status,
        decimal Penalty,
        string Notes,
        DateTime CreatedAtUtc,
        string? RecordedByUserId);

    private sealed record DocumentSeed(
        int PropertyId,
        string FileName,
        string OriginalFileName,
        string RelativePath,
        string ContentType,
        long SizeInBytes,
        string Folder,
        DateTime UploadedAtUtc,
        string? UploadedByUserId);

    private sealed record SampleSeedTargets(
        int OwnerCount,
        int PropertyCount,
        int AssessmentCount,
        int PaymentCount,
        int DocumentCount);
}