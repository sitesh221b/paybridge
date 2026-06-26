using System.Diagnostics;

namespace PayBridge.PaymentApi.Observability;

/// <summary>
/// Custom ActivitySource for spans we create manually
/// (i.e. not auto-instrumented). Currently used for webhook
/// trace-relinking via span links.
/// </summary>
public static class PaymentTracing
{
    public const string ActivitySourceName = "PayBridge.PaymentApi";

    public static readonly ActivitySource Source = new(ActivitySourceName, "0.1.0");
}