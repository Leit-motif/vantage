namespace Vantage.Tests.TestSupport;

/// <summary>A clock the tests own, so recency assertions do not depend on when they ran.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
