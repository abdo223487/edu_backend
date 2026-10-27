namespace EduApi.Services.Interfaces;

/// <summary>
/// Sends push notifications to devices registered through DevicesController.
/// Every method is best-effort and NEVER throws: a push failure must not fail
/// the business operation that triggered it (creating an announcement, etc.).
/// When FCM is disabled/unconfigured every method is a silent no-op.
/// </summary>
public interface IPushService
{
    bool Enabled { get; }

    /// <summary>Push to every registered device of the given students.</summary>
    Task SendToStudentsAsync(IEnumerable<int> studentIds, string title, string body,
        Dictionary<string, string>? data = null, CancellationToken ct = default);

    /// <summary>Push to every registered device of the given staff accounts (Teachers.Id).</summary>
    Task SendToStaffAsync(IEnumerable<int> staffIds, string title, string body,
        Dictionary<string, string>? data = null, CancellationToken ct = default);
}
