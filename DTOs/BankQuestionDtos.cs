namespace EduApi.DTOs;

// ── Teacher: browsing/managing bank questions ──
public record BankQuestionDto(
    int Id, int LessonId, string LessonName, int UnitId, string UnitName,
    string QuestionType, string Text, List<string> Choices, string CorrectAnswer,
    int Mark, string? ImageUrl, string Difficulty);

// POST BankQuestions (multipart): LessonId, Type, Text, Answer, Mark, Difficulty, Choices[], image

// ── Student: scope picker (units → lessons → per-difficulty counts) ──
public record BankLessonScopeDto(int LessonId, string LessonName, int Easy, int Medium, int Hard);
public record BankUnitScopeDto(int UnitId, string UnitName, List<BankLessonScopeDto> Lessons);

// POST BankQuestions/start
// - ExcludeSolved: skip any question this student has ever answered before (right or wrong).
// - WeakOnly: only draw from questions this student previously answered WRONG. Takes
//   priority over ExcludeSolved (a "weak" question is by definition already solved).
// - UseMixedDifficulty: ignore Difficulty and auto-split Count as 30% Easy / 40% Medium / 30% Hard.
public record StartBankAttemptRequest(
    List<int> UnitIds, List<int>? LessonIds, string? Difficulty, int Count, int DurationMinutes,
    bool? ExcludeSolved = false, bool? WeakOnly = false, bool? UseMixedDifficulty = false);

public record StartBankAttemptResult(int AttemptId, DateTime Deadline, int QuestionCount, int TotalMarks);

// POST BankQuestions/submit
public record SubmitBankAnswerDto(int QuestionId, string Answer);
public record SubmitBankAttemptRequest(int AttemptId, List<SubmitBankAnswerDto> Answers);
public record SubmitBankAttemptResult(int Mark, int TotalMarks);

// GET BankQuestions/attempts (teacher roster of student practice attempts)
public record BankAttemptListItemDto(
    int Id, int StudentId, string StudentName, string GroupName,
    DateTime StartedAt, DateTime Deadline, bool IsSubmitted,
    int? Score, int? TotalMarks, string? Difficulty, int QuestionCount);

// GET BankQuestions/my-attempts (student's own practice-attempt history).
// Score/TotalMarks are null until the attempt is either submitted or its
// Deadline has passed — same reveal rule as GET BankQuestions/attempt/{id}.
public record MyBankAttemptListItemDto(
    int Id, DateTime StartedAt, DateTime Deadline, bool IsSubmitted,
    int? Score, int? TotalMarks, string? Difficulty, int QuestionCount);

// GET BankQuestions/stats (teacher — usage analytics per question)
public record BankQuestionStatsDto(
    int QuestionId, string Text, string Difficulty, string LessonName,
    int TimesAnswered, int CorrectCount, int IncorrectCount,
    double CorrectPercentage, int StudentsWrongCount);
