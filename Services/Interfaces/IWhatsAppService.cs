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
}
