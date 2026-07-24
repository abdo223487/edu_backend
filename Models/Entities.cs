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

    /// <summary>
    /// Global, app-wide super-administrator. NOT a tenant (TenantOwnerId is
    /// always null for a SuperAdmin account, and unlike a tenant-root Teacher
    /// it never owns Groups/Units/etc. of its own). A SuperAdmin acts ON
    /// BEHALF OF whichever teacher/tenant it targets, by sending that
    /// teacher's id in the "X-TenantId" header on every request that needs
    /// tenant-scoped data (same header students already use to pick their
    /// active teacher) -- see HttpTenantContext.
    ///
    /// BOOTSTRAP: there is no separate "create a SuperAdmin" endpoint. Instead,
    /// POST api/Teachers with UserName == "admin" AND Password == "admin"
    /// (exact, case-insensitive on the username) always creates/promotes to a
    /// SuperAdmin account, regardless of who's calling or what "role" was
    /// requested in the body -- see TeachersController.Create.
    /// </summary>
    public const string SuperAdmin = "SuperAdmin";
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

    /// <summary>
    /// SUPERADMIN CONTROL: when true, this teacher (tenant root OR staff, since
    /// staff act on behalf of TenantOwnerId's tenant) is blocked from doing
    /// anything -- login fails, and any still-valid JWT/X-TenantId request for
    /// this tenant is rejected by TenantSuspensionMiddleware -- WITHOUT deleting
    /// any of their data. Toggled via SuperAdminSetTeacherSuspension. Only ever
    /// meaningful on a tenant-root account (TenantOwnerId == null); the
    /// suspension check always resolves to the ROOT teacher's flag, so
    /// suspending a root teacher also blocks all of their staff automatically.
    /// </summary>
    public bool IsSuspended { get; set; }

    /// <summary>When IsSuspended was last set to true; null while active.</summary>
    public DateTime? SuspendedAt { get; set; }

    /// <summary>Optional note from the SuperAdmin explaining the suspension.</summary>
    public string? SuspensionReason { get; set; }
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

    /// <summary>
    /// PER-TENANT FIX: suspend/cancel used to live here as a single flag on the
    /// Student itself, which meant a student suspended/cancelled by ONE teacher
    /// (e.g. their original/legacy group) came back suspended/cancelled for
    /// EVERY OTHER teacher they're also linked to, even though teachers are
    /// otherwise fully isolated tenants. That status now lives per-relationship
    /// on StudentGroupMembership.IsSuspended/IsCancelled instead -- see there.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiry { get; set; }

    public ICollection<StudentUnitSubscription> UnitSubscriptions { get; set; } = new List<StudentUnitSubscription>();
    public ICollection<StateHistoryEntry> StateHistory { get; set; } = new List<StateHistoryEntry>();

    /// <summary>
    /// MULTI-TENANT MEMBERSHIP: a student can now belong to a Group at MORE THAN
    /// ONE teacher (tenant) at the same time -- e.g. subscribed to Teacher A for
    /// Math and Teacher B for Physics. GroupId/Group above are kept as the
    /// student's ORIGINAL/legacy group (set at creation) for backward
    /// compatibility with old data and simple lookups; for anything tenant-aware
    /// (which group does this student belong to under teacher X) use this
    /// collection instead, filtered by Group.TeacherId == current tenant.
    /// </summary>
    public ICollection<StudentGroupMembership> GroupMemberships { get; set; } = new List<StudentGroupMembership>();
}

/// <summary>
/// TENANT LAYER: join table letting a Student belong to a Group under more than
/// one teacher (tenant) at once. One row per (Student, Group). A student can only
/// ever have ONE membership per teacher in practice (enforced in code, not DB,
/// since Group.TeacherId isn't directly on this table) -- a teacher assigns the
/// student to exactly one of their own Groups.
/// </summary>
public class StudentGroupMembership
{
    public int Id { get; set; }
    public int StudentId { get; set; }
    [ForeignKey(nameof(StudentId))] public Student? Student { get; set; }
    public int GroupId { get; set; }
    [ForeignKey(nameof(GroupId))] public Group? Group { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// PER-TENANT STATUS: suspend/cancel is a decision made by ONE teacher about
    /// their own relationship with this student, so it lives on the membership
    /// row (scoped to Group -> TeacherId) instead of on Student itself. A
    /// student suspended/cancelled by Teacher A stays perfectly normal/active
    /// for Teacher B -- teachers are separate tenants and one's decision about
    /// a shared student must never affect the other.
    /// </summary>
    public bool IsSuspended { get; set; }
    public bool IsCancelled { get; set; }
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

/// <summary>
/// A standalone container for online-only lectures — NOT tied to a Unit.
/// Created with just a Name, SchoolYear, and ImageUrl. A student can never
/// browse into it or its lectures on their own; the only way in is
/// redeeming a Code that carries this OnlineLesson's Id (see
/// StudentOnlineLessonUnlock / StudentsController.RedeemCode), same
/// "code-gated" model as a standalone (no-unit) Lecture, but scoped to a
/// whole group of lectures at once instead of one at a time.
/// </summary>
public class OnlineLesson
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public int SchoolYear { get; set; }
    public string? ImageUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>TENANT LAYER: which teacher (tenant) this online lesson belongs to.</summary>
    public int TeacherId { get; set; }
}

/// <summary>
/// Grants a student access to EVERY lecture inside one OnlineLesson
/// container, created when a Code carrying that OnlineLessonId is redeemed
/// (see StudentsController.RedeemCode). Mirrors StudentUnitSubscription,
/// but for OnlineLesson containers instead of Units.
/// </summary>
public class StudentOnlineLessonUnlock
{
    public int Id { get; set; }
    // MULTI-TENANT SECURITY: same reasoning as StudentUnitSubscription.TeacherId.
    public int TeacherId { get; set; }
    public int StudentId { get; set; }
    public int OnlineLessonId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class StudentUnitSubscription
{
    public int Id { get; set; }
    // MULTI-TENANT SECURITY: a Unit belongs to exactly one Teacher, so a
    // subscription row is always scoped to that same tenant. Without this
    // (and the matching global query filter in AppDbContext), any endpoint
    // querying this table by StudentId alone -- instead of always joining
    // through the tenant-filtered Units table first -- would silently leak
    // subscriptions across teachers. Same pattern as the QuizResult fix.
    public int TeacherId { get; set; }
    public int StudentId { get; set; }
    public int UnitId { get; set; }
}

/// <summary>
/// Grants a student access to one standalone (no-unit) Lecture, created when a
/// Code carrying that LectureId is redeemed (see StudentsController.RedeemCode).
/// Mirrors StudentUnitSubscription, but for standalone lectures instead of units —
/// without this, a noUnitOnly lecture from LecturesController.ByGroup had no gate
/// at all and was visible to every student in the group regardless of any code.
/// </summary>
public class StudentLectureUnlock
{
    public int Id { get; set; }
    // MULTI-TENANT SECURITY: same reasoning as StudentUnitSubscription.TeacherId.
    public int TeacherId { get; set; }
    public int StudentId { get; set; }
    public int LectureId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
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
    /// <summary>
    /// Public R2 URL of a thumbnail frame auto-extracted (via ffmpeg) from an
    /// uploaded recorded video at upload time. Null for YouTube lectures —
    /// those use YouTube's own thumbnail API (img.youtube.com/vi/{id}/0.jpg)
    /// client-side instead, same as before.
    /// </summary>
    public string? ThumbnailUrl { get; set; }
    /// <summary>Groups (as a joined string of ids) that this lecture is visible to.</summary>
    public string GroupIdsCsv { get; set; } = string.Empty;
    [NotMapped] public List<int> GroupIds
    {
        get => GroupIdsCsv.Length == 0 ? new() : GroupIdsCsv.Split(',').Select(int.Parse).ToList();
        set => GroupIdsCsv = string.Join(',', value);
    }
    public int? UnitId { get; set; }
    /// <summary>
    /// Set instead of UnitId for a lecture created inside an OnlineLesson
    /// container (see OnlineLessonsController). Always AttendanceMethod =
    /// Online, never has GroupIds -- the only way a student reaches it is by
    /// redeeming a Code for its OnlineLessonId (StudentOnlineLessonUnlock).
    /// Mutually exclusive with UnitId.
    /// </summary>
    public int? OnlineLessonId { get; set; }
    public int? LessonIndex { get; set; }
    public int? Months { get; set; }
    public int? SchoolYear { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>TENANT LAYER: which teacher (tenant) this lecture belongs to.</summary>
    public int TeacherId { get; set; }
}

/// <summary>
/// Real join row backing Lecture.GroupIds (kept in sync automatically from
/// GroupIdsCsv by AppDbContext.SaveChangesAsync). Queries filter on this
/// table (indexed, exact match) instead of doing a substring Contains() on
/// the CSV string, which was both unindexable and capable of false-positive
/// matches (e.g. group 1 matching inside "10,21").
/// </summary>
public class LectureGroupLink
{
    public int Id { get; set; }
    public int LectureId { get; set; }
    public int GroupId { get; set; }
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
    // MULTI-TENANT SECURITY: same reasoning as StudentUnitSubscription.TeacherId
    // -- a Notebook belongs to exactly one Teacher, so a payment row does too.
    public int TeacherId { get; set; }
    public int NotebookId { get; set; }
    public int StudentId { get; set; }
    public int Price { get; set; }
    public int? DiscountedPrice { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;
    // BUGFIX: PayNotebookRequest already carried LectureId from the client
    // (the Flutter payment flow asks "which lecture is this payment for"),
    // but nothing on this entity ever stored it, so the payments list could
    // never say which lecture a payment belonged to — the Flutter payments
    // screen showed the generic "محاضرة" fallback and no unit name for
    // every single row. Nullable because ApplyDiscount() creates a
    // NotebookPayment row too, and a discount isn't tied to any one lecture.
    public int? LectureId { get; set; }
    public Lecture? Lecture { get; set; }
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
    /// <summary>
    /// OnlineLesson container ids this code unlocks (in addition to
    /// UnitIds/LectureIds). Redeeming grants a StudentOnlineLessonUnlock for
    /// each one, which in turn unlocks every lecture inside that container.
    /// </summary>
    public string OnlineLessonIdsCsv { get; set; } = string.Empty;
    [NotMapped] public List<int> OnlineLessonIds
    {
        get => OnlineLessonIdsCsv.Length == 0 ? new() : OnlineLessonIdsCsv.Split(',').Select(int.Parse).ToList();
        set => OnlineLessonIdsCsv = string.Join(',', value);
    }
    public bool IsUsed { get; set; }
    public int? UsedByStudentId { get; set; }

    /// <summary>
    /// When true, this row is a TEMPLATE, not a real redeemable code: the
    /// teacher created it with a TriggerLectureId instead of handing out its
    /// Value. It is never itself redeemable (RedeemCode rejects it) and never
    /// becomes IsUsed. Every time a student attends TriggerLectureId, a
    /// fresh, unique, already-assigned Code row is cloned from this template
    /// (see AttendanceController.IssueTriggeredCodesAsync) — SourceCodeTemplateId
    /// on that clone points back here.
    /// </summary>
    public bool IsTemplate { get; set; } = false;

    /// <summary>
    /// Set only on a template (IsTemplate = true): the Center lecture whose
    /// attendance auto-issues a personal code clone to whoever attends it.
    /// </summary>
    public int? TriggerLectureId { get; set; }

    /// <summary>
    /// Set only on an auto-issued clone (IsTemplate = false, IsUsed = true
    /// from creation): the template Code.Id it was cloned from. Used to
    /// avoid issuing a second code to the same student from the same
    /// template, and to let a teacher trace clones back to their template.
    /// </summary>
    public int? SourceCodeTemplateId { get; set; }

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

public class NotificationGroupLink
{
    public int Id { get; set; }
    public int NotificationId { get; set; }
    public int GroupId { get; set; }
}

public class Attendance
{
    public int Id { get; set; }
    // MULTI-TENANT SECURITY: same reasoning as StudentUnitSubscription.TeacherId
    // -- a Lecture belongs to exactly one Teacher, so an attendance row does too.
    public int TeacherId { get; set; }
    public int LectureId { get; set; }
    public int StudentId { get; set; }
    public string? EncodedStudentId { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// "الانصراف" — a teacher marks a lesson (for one Group, within one Unit) as
/// finished. Recording this fires a single WhatsApp broadcast to every
/// parent of every student currently in that Group, letting them know the
/// class has ended. Unlike Attendance (per-student, QR-scan driven), this is
/// a one-shot group-wide announcement with no per-student tracking.
/// </summary>
public class Dismissal
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public string Title { get; set; } = default!; // اسم المحاضرة/الحصة
    public int UnitId { get; set; }
    public int GroupId { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
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

public class QuizGroupLink
{
    public int Id { get; set; }
    public int QuizId { get; set; }
    public int GroupId { get; set; }
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

    /// <summary>
    /// TENANT LAYER: which teacher (tenant) this result belongs to (copied from
    /// the parent Quiz at creation time). Without this, a student subscribed to
    /// more than one teacher would see quiz marks from ALL of their teachers
    /// mixed together, since this table was only ever filtered by StudentId.
    /// </summary>
    public int TeacherId { get; set; }
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

    /// <summary>
    /// TENANT LAYER: which teacher (tenant) entered this manual result. Without
    /// this, a student subscribed to more than one teacher would see center-quiz
    /// marks from ALL of their teachers mixed together, since this table was
    /// only ever filtered by StudentId.
    /// </summary>
    public int TeacherId { get; set; }
}

public class HomeworkResult
{
    public int Id { get; set; }
    public int StudentId { get; set; }
    public string Title { get; set; } = default!;
    public int Marks { get; set; }
    public int TotalMarks { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// TENANT LAYER: which teacher (tenant) entered this manual result. Same
    /// leak as CenterQuizResult without it.
    /// </summary>
    public int TeacherId { get; set; }
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

public class AssignmentGroupLink
{
    public int Id { get; set; }
    public int AssignmentId { get; set; }
    public int GroupId { get; set; }
}

public class AssignmentUnitLink
{
    public int Id { get; set; }
    public int AssignmentId { get; set; }
    public int UnitId { get; set; }
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
    // MULTI-TENANT SECURITY: same reasoning as StudentUnitSubscription.TeacherId
    // -- an Assignment belongs to exactly one Teacher, so a submission does too.
    public int TeacherId { get; set; }
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

/// <summary>
/// A single reusable question in the teacher's "question bank" ("بنك الأسئلة"),
/// always scoped to one specific Lesson and tagged with a Difficulty. Students
/// later build their own timed practice quiz ("BankAttempt") by picking
/// unit(s)/lesson(s) + difficulty + how many questions + a duration.
/// </summary>
public class BankQuestion
{
    public int Id { get; set; }

    // Exactly one of LessonId / UnitId is set. LessonId = question belongs to
    // one specific lesson inside a unit (as before). UnitId = the teacher
    // attached it directly to the whole unit without picking a lesson —
    // still shows up whenever a student browses/practices that unit.
    public int? LessonId { get; set; }
    [ForeignKey(nameof(LessonId))] public Lesson? Lesson { get; set; }

    public int? UnitId { get; set; }
    [ForeignKey(nameof(UnitId))] public Unit? Unit { get; set; }

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

    /// <summary>"Easy" | "Medium" | "Hard".</summary>
    public string Difficulty { get; set; } = "Medium";

    /// <summary>TENANT LAYER: which teacher (tenant) this bank question belongs to.</summary>
    public int TeacherId { get; set; }
}

/// <summary>
/// A student-generated timed practice quiz pulled from the question bank.
/// Behaves like a Quiz: the Deadline is computed at creation time
/// (StartedAt + DurationMinutes), and the correction is shown immediately
/// after submitting (or once the deadline passes), unlike Assignment.
/// </summary>
public class BankAttempt
{
    public int Id { get; set; }
    public int StudentId { get; set; }

    /// <summary>TENANT LAYER: which teacher (tenant) this attempt's bank belongs to.</summary>
    public int TeacherId { get; set; }

    public string UnitIdsCsv { get; set; } = string.Empty;
    [NotMapped] public List<int> UnitIds
    {
        get => UnitIdsCsv.Length == 0 ? new() : UnitIdsCsv.Split(',').Select(int.Parse).ToList();
        set => UnitIdsCsv = string.Join(',', value);
    }

    /// <summary>Empty means "all lessons within the selected units".</summary>
    public string LessonIdsCsv { get; set; } = string.Empty;
    [NotMapped] public List<int> LessonIds
    {
        get => LessonIdsCsv.Length == 0 ? new() : LessonIdsCsv.Split(',').Select(int.Parse).ToList();
        set => LessonIdsCsv = string.Join(',', value);
    }

    /// <summary>"Easy" | "Medium" | "Hard" | null (any difficulty).</summary>
    public string? Difficulty { get; set; }

    public int DurationMinutes { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime Deadline { get; set; }

    public int Score { get; set; }
    public int TotalMarks { get; set; }
    public DateTime? SubmittedAt { get; set; }

    public ICollection<BankAttemptQuestion> Questions { get; set; } = new List<BankAttemptQuestion>();
}

public class BankAttemptQuestion
{
    public int Id { get; set; }
    public int AttemptId { get; set; }
    [ForeignKey(nameof(AttemptId))] public BankAttempt? Attempt { get; set; }

    public int BankQuestionId { get; set; }
    [ForeignKey(nameof(BankQuestionId))] public BankQuestion? BankQuestion { get; set; }

    public string? Answer { get; set; }
    public int? MarkAwarded { get; set; }

    /// <summary>
    /// Snapshot of this MCQ's choices in a randomized order, fixed at attempt
    /// creation time so re-loading the same attempt doesn't reshuffle the
    /// options underneath the student's already-selected answer. Empty for
    /// non-MCQ question types.
    /// </summary>
    public string ShuffledChoicesCsv { get; set; } = string.Empty;
    [NotMapped] public List<string> ShuffledChoices
    {
        get => ShuffledChoicesCsv.Length == 0 ? new() : ShuffledChoicesCsv.Split('\u001F').ToList();
        set => ShuffledChoicesCsv = string.Join('\u001F', value);
    }
}

public class AppVersion
{
    public int Id { get; set; }
    public string Platform { get; set; } = default!;
    public string Version { get; set; } = default!;
    public string DownloadUrl { get; set; } = default!;
}
