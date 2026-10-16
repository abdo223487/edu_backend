namespace EduApi.Models;

/// <summary>
/// باقة (plan) created by the SuperAdmin: name + price + which dashboard
/// features are included. Everything NOT in <see cref="FeatureKeys"/> is
/// blocked for teachers on this plan.
///
/// FeatureKeys   : comma-separated catalog keys (Common/TeacherFeatures.cs) -> these drive the REAL buttons.
/// ExtraIncluded : free text lines the SuperAdmin writes (shown as green boxes), "\n"-separated.
/// ExtraExcluded : free text lines the SuperAdmin writes (shown as red boxes),  "\n"-separated.
/// The two extra lists are display-only (e.g. "دعم فني 24 ساعة"); they never change any button.
/// Plans are GLOBAL (not tenant-scoped): no TeacherId, no query filter.
/// </summary>
public class FeaturePlan
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public decimal Price { get; set; }
    public string? FeatureKeys { get; set; }
    public string? ExtraIncluded { get; set; }
    public string? ExtraExcluded { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
