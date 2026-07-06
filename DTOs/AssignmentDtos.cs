namespace EduApi.DTOs;

public record AssignmentQuestionDto(int Id, string Type, string Text, List<string> Choices, int Mark, string? ImageUrl);

// Teacher (and post-deadline student) view includes the correct answer.
public record AssignmentQuestionTeacherDto(int Id, string Type, string Text, List<string> Choices, string Answer, int Mark, string? ImageUrl);

public record AssignmentListItem(int Id, string Title, List<int> UnitIds, List<int> GroupIds, DateTime Deadline, int? SchoolYear, bool HasSubmitted);

// POST Assignments/submit
// body: { "assignmentId": int, "answers": [ { "questionId": int, "answer": "..." }, ... ] }
public record SubmitAssignmentAnswerDto(int QuestionId, string Answer);
public record SubmitAssignmentRequest(int AssignmentId, List<SubmitAssignmentAnswerDto> Answers);

public record SubmitAssignmentResult(int Mark, int TotalMarks);

// POST Assignments/change-mark
public record ChangeAssignmentMarkRequest(int AssignmentId, int StudentId, int QuestionId, int Mark);

public record AssignmentTakerDto(
    int StudentId,
    string StudentName,
    string GroupName,
    bool HasSubmitted,
    int? Mark,
    int? TotalMarks,
    DateTime? SubmittedAt);
