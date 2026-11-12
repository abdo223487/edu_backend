using System.ComponentModel.DataAnnotations;

namespace EduApi.Models;

// ═══════════════════════════════════════════════════════════════════════════
// LEARNING INSIGHTS FEATURES
//   1) Question -> video minute ("شوف الشرح")   -> QuestionExplanation
//   2) Student lecture notes (timestamped)       -> LectureNote
//   3) Student-vs-student challenges             -> StudentChallenge
//
// Everything else in this batch (daily study plan, "ليه غلطت؟", grade prediction,
// at-risk students, class understanding map, video drop-off analysis, grouping of
// similar student questions) is COMPUTED from rows that already exist and needs
// NO new table.
//
// Every table here is owned by one teacher (tenant): it carries TeacherId and gets
// the same global query filter as the rest of the tenant-owned tables (see
// AppDbContext.ConfigureLearningInsights).
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// "السؤال مربوط بدقيقة الشرح": the teacher links ONE question to the exact second of a
/// lecture video that explains it. A student who got the question wrong gets a
/// "شوف الشرح" button that opens that lecture at <see cref="StartSeconds"/>.
///
/// SourceType (normalised): "Bank" | "Quiz" | "LectureExam" | "LectureAssignment" | "Assignment".
/// QuestionId is the id in that source's question table (BankQuestions / Questions /
/// LectureExamQuestions / LectureAssignmentQuestions / AssignmentQuestions).
/// One explanation per (tenant, SourceType, QuestionId): saving again replaces it.
/// </summary>
public class QuestionExplanation
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    [MaxLength(30)] public string SourceType { get; set; } = "Bank";
    public int QuestionId { get; set; }
    public int LectureId { get; set; }
    public int StartSeconds { get; set; }
    /// <summary>Optional end of the explaining segment (purely informational for the player).</summary>
    public int? EndSeconds { get; set; }
    [MaxLength(300)] public string? Note { get; set; }
    /// <summary>How many times students tapped "شوف الشرح" (feeds the video analysis).</summary>
    public int OpenCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A student's own timestamped note on a lecture video (longer than a bookmark's
/// 200-char note). Private to the student; never shown to the teacher.
/// </summary>
public class LectureNote
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public int StudentId { get; set; }
    public int LectureId { get; set; }
    public int Seconds { get; set; }
    [MaxLength(1000)] public string Body { get; set; } = default!;
    public bool IsStarred { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A 1v1 challenge between two classmates of the same teacher on a fixed set of
/// question-bank questions. The server picks the questions, grades the answers and
/// decides the winner (higher score, then faster). Nothing is trusted from the client
/// except the answers themselves.
///
/// Status: Pending (waiting for the opponent) -> Active (accepted, both may play until
/// <see cref="PlayUntil"/>) -> Completed. Side exits: Declined, Cancelled, Expired.
/// </summary>
public class StudentChallenge
{
    public int Id { get; set; }
    public int TeacherId { get; set; }

    public int ChallengerId { get; set; }
    public int OpponentId { get; set; }

    public int? UnitId { get; set; }
    public int? LessonId { get; set; }
    [MaxLength(10)] public string? Difficulty { get; set; }

    /// <summary>Bank question ids, comma separated, in the order they are asked.</summary>
    public string QuestionIdsCsv { get; set; } = string.Empty;
    public int TotalMarks { get; set; }

    [MaxLength(20)] public string Status { get; set; } = "Pending";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>The opponent must accept before this moment.</summary>
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    /// <summary>After acceptance: the last moment either player may still start/finish.</summary>
    public DateTime? PlayUntil { get; set; }

    public DateTime? ChallengerStartedAt { get; set; }
    public DateTime? ChallengerFinishedAt { get; set; }
    public int? ChallengerScore { get; set; }
    public int? ChallengerSeconds { get; set; }

    public DateTime? OpponentStartedAt { get; set; }
    public DateTime? OpponentFinishedAt { get; set; }
    public int? OpponentScore { get; set; }
    public int? OpponentSeconds { get; set; }

    /// <summary>Null while running, and also null for a draw.</summary>
    public int? WinnerId { get; set; }
    public DateTime? CompletedAt { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════════
// VIDEO CHECKPOINTS ("استنى وحل السؤال ده الأول")
//   The teacher drops a checkpoint on a second of a lecture video (YouTube or file).
//   When a student's playback reaches it the app pauses the video and asks one of the
//   checkpoint's questions: right -> the video continues, wrong -> another question.
//   Questions live in the normal question bank (BankQuestions), so they are reusable.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>A question stop on one second of a lecture video.</summary>
public class LectureCheckpoint
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public int LectureId { get; set; }
    public int Seconds { get; set; }
    [MaxLength(120)] public string? Title { get; set; }
    /// <summary>Wrong answers in one round before the video is allowed to continue anyway.</summary>
    public int MaxAttempts { get; set; } = 3;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One of the questions the teacher attached to a checkpoint (the first is asked first,
/// the others are the "another question" after a wrong answer).</summary>
public class LectureCheckpointQuestion
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public int CheckpointId { get; set; }
    public int BankQuestionId { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>
/// Where one student stands on one checkpoint. A "round" starts when the student meets the
/// checkpoint and ends when they answer right (Passed) or burn all attempts (Exhausted: the
/// video continues and the checkpoint will ask again next time).
/// </summary>
public class LectureCheckpointProgress
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public int CheckpointId { get; set; }
    public int StudentId { get; set; }

    public bool Passed { get; set; }
    public bool PassedFirstTry { get; set; }
    public DateTime? PassedAt { get; set; }

    /// <summary>Wrong answers in the current round.</summary>
    public int WrongInRound { get; set; }
    /// <summary>Bank question ids already asked in the current round, comma separated.</summary>
    public string AskedCsv { get; set; } = string.Empty;
    /// <summary>The question issued and not answered yet (only this one can be answered).</summary>
    public int? CurrentQuestionId { get; set; }

    public int TotalAttempts { get; set; }
    public int TotalWrong { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Every answer a student gave to a checkpoint question (history + teacher statistics).</summary>
public class LectureCheckpointAttempt
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public int CheckpointId { get; set; }
    public int StudentId { get; set; }
    public int BankQuestionId { get; set; }
    [MaxLength(500)] public string? Answer { get; set; }
    public bool IsCorrect { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
