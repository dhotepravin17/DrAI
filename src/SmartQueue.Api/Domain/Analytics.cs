namespace SmartQueue.Api.Domain;

/// <summary>
/// A product analytics event (PRD §38). Holds internal IDs only, never names, mobile numbers or emergency notes.
/// <see cref="Dimension"/> and <see cref="Value"/> carry the one category and one measure each metric is grouped by.
/// </summary>
public class ProductEvent
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public string? Dimension { get; set; }
    public double? Value { get; set; }
    public required string PropertiesJson { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
