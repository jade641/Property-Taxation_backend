using System.Text.Json.Serialization;

namespace PropertyTax.API.Models.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaymentStatus
{
    Paid,
    Unpaid,
    Late
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ComplianceStatus
{
    Compliant,
    Unpaid,
    Late
}
