namespace EduApi.Services.Interfaces;

public record AttendanceWhatsAppNotification(
    string StudentName,
    string TeacherName,
    DateTime AttendanceLocalTime,
    string LastGradeText,
    string LastHomeworkText,
    string NotebookStatusText
);

public record StudentWelcomeWhatsAppNotification(
    string StudentName,
    string UserName,
    string Password
);

public record DismissalWhatsAppNotification(
    string TeacherName,
    string LessonTitle,
    string GroupName,
    DateTime DismissalLocalTime
);

public interface IWhatsAppService
{
    /// <summary>
    /// Sends the attendance notification to the parent's WhatsApp number.
    /// Implementations must never throw on delivery failure (network error,
    /// invalid number, template not approved, etc.) — attendance recording
    /// must always succeed even if the WhatsApp message doesn't go out.
    /// Callers should still check the bool to log/surface failures if needed.
    /// </summary>
    Task<bool> SendAttendanceNotificationAsync(string parentPhoneNumber, AttendanceWhatsAppNotification data);

    /// <summary>
    /// Sends the "welcome / here are your login details" message to a newly
    /// created student's own WhatsApp number. Same never-throw contract as
    /// SendAttendanceNotificationAsync: student creation must always succeed
    /// even if this message doesn't go out.
    /// </summary>
    Task<bool> SendWelcomeMessageAsync(string studentPhoneNumber, StudentWelcomeWhatsAppNotification data);

    /// <summary>
    /// Sends the "lesson finished / dismissal" broadcast to one parent's
    /// WhatsApp number. Same never-throw contract as the others: recording a
    /// Dismissal must always succeed even if a given message doesn't go out.
    /// The caller loops this over every parent phone number in the group.
    /// </summary>
    Task<bool> SendDismissalNotificationAsync(string parentPhoneNumber, DismissalWhatsAppNotification data);
}
