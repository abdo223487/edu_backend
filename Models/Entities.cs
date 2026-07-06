using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EduApi.Models;

/// <summary>
/// Roles as embedded in the JWT "role" claim.
/// The Flutter client accepts either a single string role, or a list of
/// roles where it prioritizes: AssistantAdmin > Teacher > Student > Assistant.
/// </summary>
public static class Roles
{
    public const string Teacher = "Teacher";
    public const string AssistantAdmin = "AssistantAdmin";
    public const string Assistant = "Assistant";
    public const string Student = "Student";
}

public class Teacher
{
    public int Id { get; set; }
    [Required] public string UserName { get; set; } = default!;
    public string PasswordHash { get; set; } = default!;
    public string Name { get; set; } = default!;
    /// <summary>Roles assigned to this account (Teacher / AssistantAdmin / Assistant).</summary>
    public string Role { get; set; } = Roles.Teacher;
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiry { get; set; }

    /// <summary>
    /// TENANT LAYER: the "owning" tenant this account acts on behalf of.
    /// - null  => this account IS a tenant root (a real Teacher who owns their own data).
    /// - set   => this account is staff (Assistant / AssistantAdmin) working *for* another
    ///            teacher's tenant; all their actions are scoped to TenantOwnerId's data.
    /// Effective tenant id = TenantOwnerId ?? Id. Computed once at login/refresh and
    /// embedded in the JWT as the "tenantId" claim (see TokenService/AuthController).
    /// </summary>
    public int? TenantOwnerId { get; set; }

    /// <summary>
    /// Shown on the student's SelectTeacherPage card. Nullable because staff
    /// accounts (Assistant/AssistantAdmin) never appear on that page — only
    /// tenant-root teachers (TenantOwnerId == null) do.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// Same as above: SelectTeacherPage does `Image.network(teacher['imageUrl'])`
    /// with NO null-check, so a tenant-root teacher without one WILL crash that
    /// screen client-side. Always send a non-null value for root teachers.
    /// </summary>
    public string? ImageUrl { get; set; }
}

public class Group
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public int SchoolYear { get; set; }

    /// <summary>TENANT LAYER: which teacher (tenant) this group belongs to.</summary>
    public int TeacherId { get; set; }

    public ICollection<Student> Students { get; set; } = new List<Student>();
}

public class Student
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public string PhoneNumber { get; set; } = default!;
    public string? ParentPhoneNumber { get; set; }
    public string? UserName { get; set; }
    public string PasswordHash { get; set; } = default!;
    public int GroupId { get; set; }
    [ForeignKey(nameof(GroupId))] public Group? Group { get; set; }
    public int SchoolYear { get; set; }
    public bool IsSuspended { get; set; }
    public bool IsCancelled { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiry { get; set; }

    public ICollection<StudentUnitSubscription> UnitSubscriptions { get; set; } = new List<StudentUnitSubscription>();
    public ICollection<StateHistoryEntry> StateHistory { get; set; } = new List<StateHistoryEntry>();
}

/// <summary>History log for suspend/reactivate/cancel/delete actions on a student.</summary>
public class StateHistoryEntry
{
    public int Id { get; set; }
    public int StudentId { get; set; }
    public string Action { get; set; } = default!; // Suspended / Reactivated / Cancelled / etc.
    public string? Reason { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;
}

public class Unit
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public int SchoolYear { get; set; }
    public int? Month { get; set; }
    public string? ImageUrl { get; set; }

    /// <summary>TENANT LAYER: which teacher (tenant) this unit belongs to.</summary>
    public int TeacherId { get; set; }

    public ICollection<Lesson> Lessons { get; set; } = new List<Lesson>();
}

public class Lesson
{
    public int Id { get; set; }
    public int UnitId { get; set; }
    [ForeignKey(nameof(UnitId))] public Unit? Unit { get; set; }
    /// <summary>Position of the lesson within the unit (used for lookups instead of Id).</summary>
    public int LessonIndex { get; set; }
    public string Name { get; set; } = default!;

    /// <summary>Mandatory cover image for the lesson (enforced in UnitsController.AddLesson).</summary>
    public string? ImageUrl { get; set; }
}

public class StudentUnitSubscription
{
    public int Id { get; set; }
    public int StudentId { get; set; }
    public int UnitId { get; set; }
}

public enum AttendanceMethod
{
    Center,
    Online
}

public class Lecture
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public AttendanceMethod AttendanceMethod { get; set; }
    public string? YoutubeLink { get; set; }
    public string? StorageFileKey { get; set; }
    /// <summary>Groups (as a joined string of ids) that this lecture is visible to.</summary>
    public string GroupIdsCsv { get; set; } = string.Empty;
    [NotMapped] public List<int> GroupIds
    {
        get => GroupIdsCsv.Length == 0 ? new() : GroupIdsCsv.Split(',').Select(int.Parse).ToList();
        set => GroupIdsCsv = string.Join(',', value);
    }
    public int? UnitId { get; set; }
    public int? LessonIndex { get; set; }
    public int? Months { get; set; }
    public int? SchoolYear { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>TENANT LAYER: which teacher (tenant) this lecture belongs to.</summary>
    public int TeacherId { get; set; }
}

public enum MaterialType
{
    File,
    GoogleDrive
}

public class Material
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public string Type { get; set; } = "file"; // "file" or extension-based, matches client's `m['type']`
    public string Link { get; set; } = default!;
    public int? LectureId { get; set; }
    public int? UnitId { get; set; }
    public int? SchoolYear { get; set; }
    public int? Months { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>TENANT LAYER: which teacher (tenant) this material belongs to.</summary>
    public int TeacherId { get; set; }
}

public class Notebook
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public int SchoolYear { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>TENANT LAYER: which teacher (tenant) this notebook belongs to.</summary>
    public int TeacherId { get; set; }

    public string GroupIdsCsv { get; set; } = string.Empty;
    [NotMapped] public List<int> GroupIds
    {
        get => GroupIdsCsv.Length == 0 ? new() : GroupIdsCsv.Split(',').Select(int.Parse).ToList();
        set => GroupIdsCsv = string.Join(',', value);
    }
    public int Price { get; set; }
    public string UnitIdsCsv { get; set; } = string.Empty;
    [NotMapped] public List<int> UnitIds
    {
        get => UnitIdsCsv.Length == 0 ? new() : UnitIdsCsv.Split(',').Select(int.Parse).ToList();
        set => UnitIdsCsv = string.Join(',', value);
    }
}

public class NotebookPayment
{
    public int Id { get; set; }
    public int NotebookId { get; set; }
    public int StudentId { get; set; }
    public int Price { get; set; }
    public int? DiscountedPrice { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;
}

public class Code
{
    public int Id { get; set; }
    public string Value { get; set; } = default!;
    public int SchoolYear { get; set; }
    public string UnitIdsCsv { get; set; } = string.Empty;
    [NotMapped] public List<int> UnitIds
    {
        get => UnitIdsCsv.Length == 0 ? new() : UnitIdsCsv.Split(',').Select(int.Parse).ToList();
        set => UnitIdsCsv = string.Join(',', value);
    }
    public string LectureIdsCsv { get; set; } = string.Empty;
    [NotMapped] public List<int> LectureIds
    {
        get => LectureIdsCsv.Length == 0 ? new() : LectureIdsCsv.Split(',').Select(int.Parse).ToList();
        set => LectureIdsCsv = string.Join(',', value);
    }
    public bool IsUsed { get; set; }
    public int? UsedByStudentId { get; set; }

    /// <summary>
    /// When the code was redeemed. The Flutter details sheet reads this as
    /// `data['redeemedAt']` to show "تم الاستخدام بنجاح" with a date — was
    /// missing entirely before (only IsUsed/UsedByStudentId existed).
    /// </summary>
    public DateTime? UsedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>TENANT LAYER: which teacher (tenant) this code belongs to.</summary>
    public int TeacherId { get; set; }
}

public class Notification
{
    public int Id { get; set; }
    public string Title { get; set; } = default!;
    public string Message { get; set; } = default!;

    /// <summary>TENANT LAYER: which teacher (tenant) this notification belongs to.</summary>
    public int TeacherId { get; set; }

    public string GroupIdsCsv { get; set; } = string.Empty;
    [NotMapped] public List<int> GroupIds
    {
        get => GroupIdsCsv.Length == 0 ? new() : GroupIdsCsv.Split(',').Select(int.Parse).ToList();
        set => GroupIdsCsv = string.Join(',', value);
    }
    public int? UnitId { get; set; }
    public string Type { get; set; } = "info";
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Attendance
{
    public int Id { get; set; }
    public int LectureId { get; set; }
    public int StudentId { get; set; }
    public string? EncodedStudentId { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;
}

public enum QuestionType
{
    MCQ,
    TrueFalse,
    ShortAnswer
}

public class Quiz
{
    public int Id { get; set; }
    public string Title { get; set; } = default!;
    public string GroupIdsCsv { get; set; } = string.Empty;
    [NotMapped] public List<int> GroupIds
    {
        get => GroupIdsCsv.Length == 0 ? new() : GroupIdsCsv.Split(',').Select(int.Parse).ToList();
        set => GroupIdsCsv = string.Join(',', value);
    }
    public int UnitId { get; set; }
    public int DurationInMinutes { get; set; }
    public DateTime Deadline { get; set; }
    public int? SchoolYear { get; set; }

    /// <summary>TENANT LAYER: which teacher (tenant) this quiz belongs to.</summary>
    public int TeacherId { get; set; }

    public ICollection<Question> Questions { get; set; } = new List<Question>();
}

public class Question
{
    public int Id { get; set; }
    public int QuizId { get; set; }
    [ForeignKey(nameof(QuizId))] public Quiz? Quiz { get; set; }
    public string Type { get; set; } = "MCQ";
    public string Text { get; set; } = default!;
    public string Answer { get; set; } = default!;
    public int Mark { get; set; }
    public string ChoicesCsv { get; set; } = string.Empty;
    [NotMapped] public List<string> Choices
    {
        get => ChoicesCsv.Length == 0 ? new() : ChoicesCsv.Split('\u001F').ToList();
        set => ChoicesCsv = string.Join('\u001F', value);
    }
    public string? ImageUrl { get; set; }
}

public class QuizResult
{
    public int Id { get; set; }
    public int QuizId { get; set; }
    public int StudentId { get; set; }
    public int Score { get; set; }
    public int TotalMarks { get; set; }
    public DateTime GradedAt { get; set; } = DateTime.UtcNow;
    public ICollection<QuizAnswer> Answers { get; set; } = new List<QuizAnswer>();
}

public class QuizAnswer
{
    public int Id { get; set; }
    public int QuizResultId { get; set; }
    [ForeignKey(nameof(QuizResultId))] public QuizResult? QuizResult { get; set; }
    public int QuestionId { get; set; }
    public string Answer { get; set; } = default!;
    public int? MarkAwarded { get; set; }
}

/// <summary>
/// Manually recorded center (offline) quiz/homework results, entered by a teacher
/// via "Students/quiz-results/center/add" and "Students/homework-results/add".
/// </summary>
public class CenterQuizResult
{
    public int Id { get; set; }
    public int StudentId { get; set; }
    public string Title { get; set; } = default!;
    public int Marks { get; set; }
    public int TotalMarks { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;
}

public class HomeworkResult
{
    public int Id { get; set; }
    public int StudentId { get; set; }
    public string Title { get; set; } = default!;
    public int Marks { get; set; }
    public int TotalMarks { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Homework/assignment ("الواجب") set by a teacher for one or more groups,
/// covering one or more units. Unlike Quiz (single unit, timed), an Assignment
/// has no duration — just a Deadline after which no submission is accepted,
/// and the correct answers are only revealed once that Deadline has fully
/// passed (never immediately after a student submits).
/// </summary>
public class Assignment
{
    public int Id { get; set; }
    public string Title { get; set; } = default!;
    public int? SchoolYear { get; set; }
    public DateTime Deadline { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string GroupIdsCsv { get; set; } = string.Empty;
    [NotMapped] public List<int> GroupIds
    {
        get => GroupIdsCsv.Length == 0 ? new() : GroupIdsCsv.Split(',').Select(int.Parse).ToList();
        set => GroupIdsCsv = string.Join(',', value);
    }

    public string UnitIdsCsv { get; set; } = string.Empty;
    [NotMapped] public List<int> UnitIds
    {
        get => UnitIdsCsv.Length == 0 ? new() : UnitIdsCsv.Split(',').Select(int.Parse).ToList();
        set => UnitIdsCsv = string.Join(',', value);
    }

    /// <summary>TENANT LAYER: which teacher (tenant) this assignment belongs to.</summary>
    public int TeacherId { get; set; }

    public ICollection<AssignmentQuestion> Questions { get; set; } = new List<AssignmentQuestion>();
}

public class AssignmentQuestion
{
    public int Id { get; set; }
    public int AssignmentId { get; set; }
    [ForeignKey(nameof(AssignmentId))] public Assignment? Assignment { get; set; }
    public string Type { get; set; } = "MCQ";
    public string Text { get; set; } = default!;
    public string Answer { get; set; } = default!;
    public int Mark { get; set; }
    public string ChoicesCsv { get; set; } = string.Empty;
    [NotMapped] public List<string> Choices
    {
        get => ChoicesCsv.Length == 0 ? new() : ChoicesCsv.Split('\u001F').ToList();
        set => ChoicesCsv = string.Join('\u001F', value);
    }
    public string? ImageUrl { get; set; }
}

/// <summary>A student's single submission of an Assignment.</summary>
public class AssignmentSubmission
{
    public int Id { get; set; }
    public int AssignmentId { get; set; }
    public int StudentId { get; set; }
    public int Score { get; set; }
    public int TotalMarks { get; set; }
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public ICollection<AssignmentAnswer> Answers { get; set; } = new List<AssignmentAnswer>();
}

public class AssignmentAnswer
{
    public int Id { get; set; }
    public int AssignmentSubmissionId { get; set; }
    [ForeignKey(nameof(AssignmentSubmissionId))] public AssignmentSubmission? AssignmentSubmission { get; set; }
    public int QuestionId { get; set; }
    public string Answer { get; set; } = default!;
    public int? MarkAwarded { get; set; }
}

public class AppVersion
{
    public int Id { get; set; }
    public string Platform { get; set; } = default!;
    public string Version { get; set; } = default!;
    public string DownloadUrl { get; set; } = default!;
}
