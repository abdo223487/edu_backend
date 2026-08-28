namespace EduApi.DTOs;

public record AssignmentQuestionDto(int Id, string QuestionType, string Text, List<string> Choices, int Mark, string? ImageUrl);

// Teacher (and post-deadline student) view includes the correct answer.
public record AssignmentQuestionTeacherDto(int Id, string QuestionType, string Text, List<string> Choices, string CorrectAnswer, int Mark, string? ImageUrl);

// Score/TotalMarks appended at the end (optional, null when not submitted
// yet) so the existing positional-record shape stays backward compatible.
public record AssignmentListItem(int Id, string Title, List<int> UnitIds, List<int> GroupIds, DateTime Deadline, int? SchoolYear, bool HasSubmitted, bool AllowLateReview, int? Score = null, int? TotalMarks = null);

// POST Assignments/submit
// body: { "assignmentId": int, "answers": [ { "questionId": int, "answer": "..." }, ... ] }
public record SubmitAssignmentAnswerDto(int QuestionId, string Answer);
public record SubmitAssignmentRequest(int AssignmentId, List<SubmitAssignmentAnswerDto> Answers);

public record SubmitAssignmentResult(int Mark, int TotalMarks);

// POST Assignments/change-mark
public record ChangeAssignmentMarkRequest(int AssignmentId, int StudentId, int QuestionId, int Mark);

// POST Assignments/edit-question
// body: { "assignmentId": int, "questionId": int, "text": "...", "mark": int, "choices": ["..."], "answer": "..." }
public record EditAssignmentQuestionRequest(int AssignmentId, int QuestionId, string Text, int Mark, List<string>? Choices, string Answer);

// POST Assignments/edit
// body: { "assignmentId": int, "title": "...", "deadline": "...", "allowLateReview": bool }
// Lets a teacher fix the assignment's own basic info (name/deadline/late-review
// policy) after creation, without touching its questions/groups/units.
public record EditAssignmentRequest(int AssignmentId, string Title, DateTime Deadline, bool AllowLateReview);

// POST Assignments/force-review — same idea as Quizzes/force-review.
public record ForceAssignmentReviewRequest(int AssignmentId, int StudentId);

// POST Assignments/reopen — same idea as Quizzes/reopen.
public record ReopenAssignmentRequest(int AssignmentId, int StudentId, int Minutes);

public record AssignmentTakerDto(
    int StudentId,
    string StudentName,
    string GroupName,
    bool HasSubmitted,
    int? Mark,
    int? TotalMarks,
    DateTime? SubmittedAt);
