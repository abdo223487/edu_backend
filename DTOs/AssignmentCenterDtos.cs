namespace EduApi.DTOs;

// POST AssignmentCenters (JSON body — no files/images in this feature)
public record CreateAssignmentCenterQuestionDto(string Text, string Answer, int Mark);
public record CreateAssignmentCenterRequest(
    string Title,
    DateTime Deadline,
    List<int> GroupIds,
    List<int> UnitIds,
    List<CreateAssignmentCenterQuestionDto> Questions);

public record AssignmentCenterQuestionTeacherDto(int Id, string Text, string CorrectAnswer, int Mark);

public record AssignmentCenterListItem(int Id, string Title, List<int> UnitIds, List<int> GroupIds, DateTime Deadline, int? SchoolYear, bool HasSubmitted);

// POST AssignmentCenters/submit
// body: { "assignmentCenterId": int, "answers": [ { "questionId": int, "answer": "أ" }, ... ] }
public record SubmitAssignmentCenterAnswerDto(int QuestionId, string Answer);
public record SubmitAssignmentCenterRequest(int AssignmentCenterId, List<SubmitAssignmentCenterAnswerDto> Answers);

public record SubmitAssignmentCenterResult(int Mark, int TotalMarks);

// POST AssignmentCenters/change-mark
public record ChangeAssignmentCenterMarkRequest(int AssignmentCenterId, int StudentId, int QuestionId, int Mark);

// POST AssignmentCenters/edit-question
// body: { "assignmentCenterId": int, "questionId": int, "text": "...", "mark": int, "answer": "أ" }
public record EditAssignmentCenterQuestionRequest(int AssignmentCenterId, int QuestionId, string Text, int Mark, string Answer);

public record AssignmentCenterTakerDto(
    int StudentId,
    string StudentName,
    string GroupName,
    bool HasSubmitted,
    int? Mark,
    int? TotalMarks,
    DateTime? SubmittedAt);
