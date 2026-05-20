using Microsoft.EntityFrameworkCore;
using PropertyTax.API.Data;
using PropertyTax.API.DTOs;
using PropertyTax.API.Models;
using PropertyTax.API.Models.Enums;

namespace PropertyTax.API.Services;

public class PaymentService
{
    private const decimal MonthlyPenaltyRate = 0.02m;
    private const int MaxPenaltyMonths = 36;
    private static readonly string[] ManualPaymentMethods = ["Cash", "Check", "Bank Deposit"];

    private readonly AppDbContext _dbContext;
    private readonly AuditLogService _auditLogService;
    private readonly DataNotificationService _notificationService;

    public PaymentService(AppDbContext dbContext, AuditLogService auditLogService, DataNotificationService notificationService)
    {
        _dbContext = dbContext;
        _auditLogService = auditLogService;
        _notificationService = notificationService;
    }

    public async Task<PaymentQuoteDto> GetQuoteAsync(int propertyId, int taxYear, string? quarter, DateTime? paymentDateUtc)
    {
        var property = await _dbContext.Properties
            .AsNoTracking()
            .Include(item => item.Taxpayer)
            .FirstOrDefaultAsync(item => item.Id == propertyId)
            ?? throw new InvalidOperationException("The supplied property could not be found.");

        return await BuildPaymentQuoteAsync(property, taxYear, quarter, paymentDateUtc);
    }

    public async Task<PaymentDto> RecordAsync(PaymentDto paymentDto, string? actorUserId)
    {
        var property = await _dbContext.Properties
            .Include(item => item.Taxpayer)
            .FirstOrDefaultAsync(item => item.Id == paymentDto.PropertyId)
            ?? throw new InvalidOperationException("The supplied property could not be found.");

        var paymentDate = EnsureUtcDate(paymentDto.PaymentDateUtc ?? DateTime.UtcNow);
        var quote = await BuildPaymentQuoteAsync(property, paymentDto.TaxYear, paymentDto.Quarter, paymentDate);

        if (quote.OutstandingPrincipal <= 0m)
        {
            throw new InvalidOperationException("The selected property has no outstanding balance for the supplied tax year.");
        }

        if (paymentDto.AmountPaid <= 0m)
        {
            throw new InvalidOperationException("Payment amount must be greater than zero.");
        }

        if (paymentDto.AmountPaid > quote.PayableAmount)
        {
            throw new InvalidOperationException($"Payment exceeds the current payable balance of {quote.PayableAmount:N2}.");
        }

        var paymentMethod = NormalizeManualPaymentMethod(paymentDto.PaymentMethod);
        var referenceNumber = NormalizeOptional(paymentDto.ReferenceNumber);
        var bankName = NormalizeOptional(paymentDto.BankName);
        ValidateManualPaymentDetails(paymentMethod, referenceNumber, bankName);

        var taxpayerId = paymentDto.TaxpayerId ?? property.TaxpayerId;
        var officialReceiptNumber = string.IsNullOrWhiteSpace(paymentDto.OfficialReceiptNumber)
            ? await GenerateOfficialReceiptNumberAsync()
            : paymentDto.OfficialReceiptNumber.Trim();

        if (await _dbContext.Payments.AnyAsync(payment => payment.OfficialReceiptNumber == officialReceiptNumber))
        {
            throw new InvalidOperationException("A payment with the supplied official receipt number already exists.");
        }

        var remainingAfterPayment = Math.Max(quote.PayableAmount - paymentDto.AmountPaid, 0m);
        var status = remainingAfterPayment <= 0m
            ? PaymentStatus.Paid.ToString()
            : string.Equals(quote.Status, PaymentStatus.Late.ToString(), StringComparison.OrdinalIgnoreCase)
                ? PaymentStatus.Late.ToString()
                : PaymentStatus.Unpaid.ToString();

        var payment = new Payment
        {
            PropertyId = property.Id,
            TaxpayerId = taxpayerId,
            TaxYear = paymentDto.TaxYear,
            Quarter = quote.Quarter,
            AmountDue = quote.OutstandingPrincipal,
            AmountPaid = paymentDto.AmountPaid,
            PaymentMethod = paymentMethod,
            ReferenceNumber = referenceNumber,
            BankName = bankName,
            OfficialReceiptNumber = officialReceiptNumber,
            PaymentDateUtc = paymentDate,
            DueDateUtc = quote.DueDateUtc,
            Status = status,
            Penalty = quote.Penalty,
            Notes = string.IsNullOrWhiteSpace(paymentDto.Notes) ? null : paymentDto.Notes.Trim(),
            RecordedByUserId = actorUserId,
            CreatedAtUtc = DateTime.UtcNow,
        };

        _dbContext.Payments.Add(payment);
        await _dbContext.SaveChangesAsync();

        await _auditLogService.LogAsync("PaymentRecorded", "Payment", payment.Id.ToString(), $"Recorded payment {officialReceiptNumber} for property {property.Pin}");

        await _notificationService.CreateForRolesAsync(
            title: "Payment recorded",
            message: $"{property.Taxpayer.FullName} paid {payment.AmountPaid:N2} for property {property.Pin} ({payment.TaxYear} {payment.Quarter}).",
            type: string.Equals(payment.Status, PaymentStatus.Late.ToString(), StringComparison.OrdinalIgnoreCase) ? "overdue" : "info",
            roles: [SystemRoles.Admin, SystemRoles.Accountant, SystemRoles.Auditor, SystemRoles.Staff],
            entityName: "Payment",
            entityId: payment.Id.ToString());

        return await MapPaymentAsync(payment.Id);
    }

    public async Task<IReadOnlyCollection<PaymentDto>> GetHistoryAsync(int? taxpayerId, int? propertyId)
    {
        var query = _dbContext.Payments
            .AsNoTracking()
            .Include(payment => payment.Property)
            .Include(payment => payment.Taxpayer)
            .AsQueryable();

        if (taxpayerId.HasValue)
        {
            query = query.Where(payment => payment.TaxpayerId == taxpayerId.Value);
        }

        if (propertyId.HasValue)
        {
            query = query.Where(payment => payment.PropertyId == propertyId.Value);
        }

        var payments = await query
            .OrderByDescending(payment => payment.PaymentDateUtc)
            .ThenByDescending(payment => payment.Id)
            .ToListAsync();

        var result = new List<PaymentDto>(payments.Count);

        foreach (var payment in payments)
        {
            result.Add(await MapPaymentAsync(payment.Id, payment));
        }

        return result;
    }

    private async Task<PaymentQuoteDto> BuildPaymentQuoteAsync(Property property, int taxYear, string? quarter, DateTime? paymentDateUtc)
    {
        if (taxYear < 1900 || taxYear > 2200)
        {
            throw new InvalidOperationException("A valid tax year is required.");
        }

        var annualTaxDue = await _dbContext.TaxAssessments
            .AsNoTracking()
            .Where(assessment => assessment.PropertyId == property.Id && assessment.TaxYear == taxYear)
            .SumAsync(assessment => (decimal?)assessment.TaxDue) ?? 0m;

        if (annualTaxDue <= 0m)
        {
            throw new InvalidOperationException("The selected property does not have a tax assessment for the supplied year.");
        }

        var totalPaidToDate = await _dbContext.Payments
            .AsNoTracking()
            .Where(payment => payment.PropertyId == property.Id && payment.TaxYear == taxYear)
            .SumAsync(payment => (decimal?)payment.AmountPaid) ?? 0m;

        var normalizedQuarter = NormalizeQuarter(quarter);
        var paymentDate = EnsureUtcDate(paymentDateUtc ?? DateTime.UtcNow);
        var dueDate = GetDueDateUtc(taxYear, normalizedQuarter);
        var outstandingPrincipal = Math.Max(annualTaxDue - totalPaidToDate, 0m);
        var quarterDue = normalizedQuarter == "Annual"
            ? outstandingPrincipal
            : Math.Max(Math.Round(annualTaxDue * GetQuarterFraction(normalizedQuarter), 2, MidpointRounding.AwayFromZero) - totalPaidToDate, 0m);
        var isLate = quarterDue > 0m && paymentDate.Date > dueDate.Date;
        var penalty = isLate
            ? CalculatePenalty(quarterDue, dueDate, paymentDate)
            : 0m;
        var payableAmount = Math.Round(outstandingPrincipal + penalty, 2, MidpointRounding.AwayFromZero);
        var status = outstandingPrincipal <= 0m
            ? PaymentStatus.Paid.ToString()
            : isLate
                ? PaymentStatus.Late.ToString()
                : PaymentStatus.Unpaid.ToString();

        return new PaymentQuoteDto
        {
            PropertyId = property.Id,
            PropertyPin = property.Pin,
            OwnerName = property.Taxpayer.FullName,
            TaxYear = taxYear,
            Quarter = normalizedQuarter,
            PaymentDateUtc = paymentDate,
            DueDateUtc = dueDate,
            AnnualTaxDue = annualTaxDue,
            TotalPaidToDate = totalPaidToDate,
            OutstandingPrincipal = outstandingPrincipal,
            QuarterDue = quarterDue,
            Penalty = penalty,
            PayableAmount = payableAmount,
            Status = status,
        };
    }

    private async Task<PaymentDto> MapPaymentAsync(int paymentId, Payment? payment = null)
    {
        payment ??= await _dbContext.Payments
            .AsNoTracking()
            .Include(item => item.Property)
            .Include(item => item.Taxpayer)
            .FirstAsync(item => item.Id == paymentId);

        var totalDue = await _dbContext.TaxAssessments
            .Where(assessment => assessment.PropertyId == payment.PropertyId && assessment.TaxYear == payment.TaxYear)
            .SumAsync(assessment => (decimal?)assessment.TaxDue) ?? 0m;

        var totalPaid = await _dbContext.Payments
            .Where(item => item.PropertyId == payment.PropertyId && item.TaxYear == payment.TaxYear)
            .SumAsync(item => (decimal?)item.AmountPaid) ?? 0m;

        return new PaymentDto
        {
            Id = payment.Id,
            PropertyId = payment.PropertyId,
            TaxpayerId = payment.TaxpayerId,
            PropertyPin = payment.Property.Pin,
            OwnerName = payment.Taxpayer.FullName,
            Barangay = payment.Property.Barangay,
            TaxYear = payment.TaxYear,
            Quarter = payment.Quarter,
            AmountDue = payment.AmountDue,
            AmountPaid = payment.AmountPaid,
            PaymentMethod = payment.PaymentMethod,
            ReferenceNumber = payment.ReferenceNumber,
            BankName = payment.BankName,
            PaymentDateUtc = payment.PaymentDateUtc,
            DueDateUtc = payment.DueDateUtc,
            Status = payment.Status,
            Penalty = payment.Penalty,
            OfficialReceiptNumber = payment.OfficialReceiptNumber,
            Notes = payment.Notes,
            RemainingBalance = totalDue > 0m ? Math.Max(totalDue - totalPaid, 0m) : Math.Max(payment.AmountDue - totalPaid, 0m),
        };
    }

    private static string NormalizeManualPaymentMethod(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "Cash" : value.Trim();

        foreach (var method in ManualPaymentMethods)
        {
            if (string.Equals(method, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return method;
            }
        }

        throw new InvalidOperationException("Payment method must be Cash, Check, or Bank Deposit for manual LGU recording.");
    }

    private static void ValidateManualPaymentDetails(string paymentMethod, string? referenceNumber, string? bankName)
    {
        if (string.Equals(paymentMethod, "Cash", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (referenceNumber is null)
        {
            throw new InvalidOperationException(
                string.Equals(paymentMethod, "Check", StringComparison.OrdinalIgnoreCase)
                    ? "Check number is required for check payments."
                    : "Deposit slip or reference number is required for bank deposits.");
        }

        if (bankName is null)
        {
            throw new InvalidOperationException("Bank name is required for this payment method.");
        }
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task<string> GenerateOfficialReceiptNumberAsync()
    {
        var dayStart = DateTime.UtcNow.Date;
        var dayEnd = dayStart.AddDays(1);

        var sequence = await _dbContext.Payments.CountAsync(payment =>
            payment.CreatedAtUtc >= dayStart && payment.CreatedAtUtc < dayEnd);

        return $"OR-{DateTime.UtcNow:yyyyMMdd}-{sequence + 1:0000}";
    }

    private static string NormalizeQuarter(string? quarter)
    {
        var normalizedQuarter = string.IsNullOrWhiteSpace(quarter)
            ? "Annual"
            : quarter.Trim().ToUpperInvariant();

        return normalizedQuarter switch
        {
            "Q1" => "Q1",
            "Q2" => "Q2",
            "Q3" => "Q3",
            "Q4" => "Q4",
            "ANNUAL" => "Annual",
            _ => throw new InvalidOperationException("Quarter must be Annual, Q1, Q2, Q3, or Q4."),
        };
    }

    private static decimal GetQuarterFraction(string quarter)
    {
        return quarter switch
        {
            "Q1" => 0.25m,
            "Q2" => 0.50m,
            "Q3" => 0.75m,
            "Q4" => 1.00m,
            _ => 1.00m,
        };
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

    private static DateTime EnsureUtcDate(DateTime value)
    {
        var utcValue = value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();

        return new DateTime(utcValue.Year, utcValue.Month, utcValue.Day, utcValue.Hour, utcValue.Minute, utcValue.Second, DateTimeKind.Utc);
    }

    private static decimal CalculatePenalty(decimal overdueAmount, DateTime dueDateUtc, DateTime paymentDateUtc)
    {
        if (overdueAmount <= 0m || paymentDateUtc.Date <= dueDateUtc.Date)
        {
            return 0m;
        }

        var monthsLate = GetMonthsLate(dueDateUtc, paymentDateUtc);
        var cappedMonths = Math.Min(monthsLate, MaxPenaltyMonths);
        var penalty = overdueAmount * MonthlyPenaltyRate * cappedMonths;

        return Math.Round(penalty, 2, MidpointRounding.AwayFromZero);
    }

    private static int GetMonthsLate(DateTime dueDateUtc, DateTime paymentDateUtc)
    {
        var months = ((paymentDateUtc.Year - dueDateUtc.Year) * 12) + paymentDateUtc.Month - dueDateUtc.Month;

        if (paymentDateUtc.Day > dueDateUtc.Day || months == 0)
        {
            months++;
        }

        return Math.Max(months, 0);
    }
}