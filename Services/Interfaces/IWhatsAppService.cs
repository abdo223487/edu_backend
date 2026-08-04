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

/// <summary>
/// Shared payload for both "quiz finished" and "assignment finished" parent
/// notifications — same 4 fields, just sent through two different templates
/// (quiz vs assignment) so the two can be worded/approved separately.
/// </summary>
public record ExamResultWhatsAppNotification(
    string StudentName,
    string TeacherName,
    string ExamTitle,
    int Score,
    int TotalMarks
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

    /// <summary>
    /// Sends the "student finished the online quiz with the teacher" message
    /// to the parent's WhatsApp number, right after the student submits
    /// (QuizzesController.Grade). Same never-throw contract as the others:
    /// grading/saving the result must always succeed even if this message
    /// doesn't go out.
    /// </summary>
    Task<bool> SendQuizResultNotificationAsync(string parentPhoneNumber, ExamResultWhatsAppNotification data);

    /// <summary>
    /// Sends the "student finished the assignment with the teacher" message
    /// to the parent's WhatsApp number, right after the student submits
    /// (AssignmentsController.Submit). Same never-throw contract as the others.
    /// </summary>
    Task<bool> SendAssignmentResultNotificationAsync(string parentPhoneNumber, ExamResultWhatsAppNotification data);
}
