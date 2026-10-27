using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EduApi.Models;

// ═══════════════════════════════════════════════════════════════════════════
// STUDENT ENGAGEMENT FEATURES
//   1) Push notifications (FCM)          -> DeviceToken, PushDispatchLog
//   2) Gamification                      -> StudentPointEvent, StudentBadge
//   3) Spaced repetition on mistakes     -> ReviewCard, ReviewLog
//   4) Resume position + bookmarks       -> LecturePlaybackPosition, LectureBookmark
//   5) Lecture Q&A threads               -> LectureComment
//   6) Offline downloads (license)       -> OfflineGrant
//
// Every table that is owned by one teacher (tenant) carries TeacherId and gets
// the same global query filter as the rest of the tenant-owned tables (see
// AppDbContext). DeviceToken / PushDispatchLog are intentionally NOT tenant
// filtered: a device belongs to a person, not to a teacher, and the push job
// runs outside any HTTP request (no tenant).
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// One FCM registration token. A phone token is unique across the table: if the
/// same phone logs into another account, the row is re-assigned to that account
/// (see DevicesController.Register).
/// UserType: "Student" (UserId = Students.Id) or "Staff" (UserId = Teachers.Id,
/// which also covers assistants).
/// </summary>
public class DeviceToken
{
    public int Id { get; set; }
    public string UserType { get; set; } = "Student";
    public int UserId { get; set; }
    [Required, MaxLength(512)] public string Token { get; set; } = default!;
    [MaxLength(20)] public string Platform { get; set; } = "android";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Idempotency log for the background push job: one row per push "campaign"
/// (e.g. "asg-new:12", "quiz-remind24:7", "review-due:55:2026-10-25") so a
/// restart or a second instance never sends the same push twice.
/// </summary>
public class PushDispatchLog
{
    [Key, MaxLength(200)] public string Key { get; set; } = default!;
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Immutable points ledger. RefKey makes every award idempotent
/// (unique per StudentId + TeacherId + RefKey), e.g. "quiz:12", "bank:7",
/// "att:99", "lec:5", "rev:1234", "qa:88", "streak:7:2026-10-20".
/// OccurredAt is the time the real activity happened (not the sync time) and
/// drives both the streak and the weekly/monthly leaderboards.
/// </summary>
public class StudentPointEvent
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public int StudentId { get; set; }
    [MaxLength(30)] public string Kind { get; set; } = default!;
    [MaxLength(80)] public string RefKey { get; set; } = default!;
    public int Points { get; set; }
    /// <summary>Optional 0..100 percentage for graded sources (used by badges).</summary>
    public int? Percent { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class StudentBadge
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public int StudentId { get; set; }
    [MaxLength(40)] public string Code { get; set; } = default!;
    public DateTime AwardedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One flash-card = one question the student got wrong (taken from "أخطائي").
/// The question is stored as a SNAPSHOT so the card keeps working even if the
/// teacher later edits/deletes the original. Scheduling = Leitner boxes:
/// Box 0 = new, 1..5 = increasing intervals (see ReviewController.Intervals).
/// </summary>
public class ReviewCard
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public int StudentId { get; set; }

    // Identity of the original question (unique per student+tenant).
    [MaxLength(30)] public string SourceType { get; set; } = default!;
    public int SourceId { get; set; }
    public int QuestionId { get; set; }

    // Grouping / display.
    [MaxLength(20)] public string CourseKind { get; set; } = "Unit";   // Unit | OnlineLesson | ExternalBook
    public int CourseId { get; set; }
    [MaxLength(200)] public string CourseName { get; set; } = string.Empty;
    [MaxLength(200)] public string SourceTitle { get; set; } = string.Empty;

    // Snapshot of the question.
    public string Text { get; set; } = default!;
    public string? ImageUrl { get; set; }
    [MaxLength(20)] public string QuestionType { get; set; } = "MCQ";
    public string ChoicesCsv { get; set; } = string.Empty; // '\u001F' separated (same as Question.ChoicesCsv)
    [NotMapped]
    public List<string> Choices
    {
        get => ChoicesCsv.Length == 0 ? new() : ChoicesCsv.Split('\u001F').ToList();
        set => ChoicesCsv = string.Join('\u001F', value);
    }
    public string CorrectAnswer { get; set; } = string.Empty;

    // Scheduling state.
    public int Box { get; set; }
    public DateTime DueAt { get; set; } = DateTime.UtcNow;
    public int Reps { get; set; }
    public int Lapses { get; set; }
    public DateTime? LastReviewedAt { get; set; }
    public DateTime? MasteredAt { get; set; }
    /// <summary>Student removed the card from the deck.</summary>
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ReviewLog
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public int StudentId { get; set; }
    public int CardId { get; set; }
    public bool Correct { get; set; }
    public int BoxBefore { get; set; }
    public int BoxAfter { get; set; }
    public DateTime ReviewedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Last known playhead per (student, lecture) -- works for EVERY lecture
/// (YouTube / file, with or without a view limit), unlike the old resume logic
/// that only existed for ViewLimit'd lectures. Also receives positions queued
/// while the student was offline (last-write-wins by UpdatedAt).
/// </summary>
public class LecturePlaybackPosition
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public int StudentId { get; set; }
    public int LectureId { get; set; }
    public int PositionSeconds { get; set; }
    public int? DurationSeconds { get; set; }
    /// <summary>True once the student got to >= 90% of the video at least once.</summary>
    public bool Completed { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class LectureBookmark
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public int StudentId { get; set; }
    public int LectureId { get; set; }
    public int Seconds { get; set; }
    [MaxLength(200)] public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One row of a lecture's Q&amp;A thread. ParentId == null  => a question;
/// ParentId != null => an answer/reply to that question (one level only).
/// AuthorRole is "Student" or "Staff" (teacher / assistant). The author name is
/// stored as a snapshot so listing a thread never needs to join Students.
/// </summary>
public class LectureComment
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public int LectureId { get; set; }
    public int? ParentId { get; set; }
    [MaxLength(10)] public string AuthorRole { get; set; } = "Student";
    public int AuthorId { get; set; }
    [MaxLength(120)] public string AuthorName { get; set; } = string.Empty;
    [MaxLength(2000)] public string Body { get; set; } = default!;
    /// <summary>Optional: the video second the question is about.</summary>
    public int? VideoSeconds { get; set; }
    public bool IsAccepted { get; set; }
    public bool IsPinned { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EditedAt { get; set; }
}

/// <summary>
/// A time-boxed "license" to keep a lecture video / PDF offline. The app must
/// re-validate (GET Offline/grants) when online and deletes expired items.
/// ItemType: "Lecture" | "Material".
/// </summary>
public class OfflineGrant
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public int StudentId { get; set; }
    [MaxLength(10)] public string ItemType { get; set; } = "Lecture";
    public int ItemId { get; set; }
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
}
