namespace EduApi.Services.Interfaces;

public record AttendanceWhatsAppNotification(
    string StudentName,
    string TeacherName,
    DateTime AttendanceLocalTime,
    string LastGradeText,
    string LastHomeworkText,
    string NotebookStatusText
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
}
