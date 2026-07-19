namespace PurrfectSeat.Domain;

public readonly record struct PerformanceId(Guid Value)
{
    public static PerformanceId New() => new(Guid.NewGuid());
}

public readonly record struct HoldId(Guid Value)
{
    public static HoldId New() => new(Guid.NewGuid());
}

public readonly record struct CustomerId(Guid Value);

public readonly record struct SeatId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct PaymentId(Guid Value)
{
    public static PaymentId New() => new(Guid.NewGuid());
}

public readonly record struct PerformanceVersion(int Value)
{
    public static PerformanceVersion Initial => new(0);

    public PerformanceVersion Next() => new(checked(Value + 1));
}

public readonly record struct Money(decimal Amount, string Currency)
{
    public static Money Zero(string currency) => new(0m, currency);

    public static Money operator +(Money left, Money right)
    {
        if (!StringComparer.Ordinal.Equals(left.Currency, right.Currency))
        {
            throw new DomainRuleViolation("money_currency_mismatch", "Money values must use the same currency.");
        }

        return new(left.Amount + right.Amount, left.Currency);
    }

    public override string ToString() => $"{Currency} {Amount:F2}";
}

public sealed class DomainRuleViolation(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
