namespace EduApi.DTOs;

// POST AssignmentCenters (JSON body — no files/images in this feature)
public record CreateAssignmentCenterQuestionDto(string Text, string Answer, int Mark);
public record CreateAssignmentCenterRequest(
    string Title,
    DateTime Deadline,
    List<int> GroupIds,
    List<int> UnitIds,
    List<CreateAssignmentCenterQuestionDto> Questions,
    // Same idea as Quiz/Assignment.AllowLateReview. Optional so old clients
    // that don't send it yet still work (record default -> false, but the
    // controller treats a JSON-missing value as "true" explicitly — see
    // AssignmentCentersController.Create).
    bool? AllowLateReview = null);

public record AssignmentCenterQuestionTeacherDto(int Id, string Text, string CorrectAnswer, int Mark);

public record AssignmentCenterListItem(int Id, string Title, List<int> UnitIds, List<int> GroupIds, DateTime Deadline, int? SchoolYear, bool HasSubmitted, bool AllowLateReview);

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

// GET AssignmentCenters?studentId=..&p=.. — one item per AssignmentCenter
// visible to the given student, teacher-facing list used by
// TeacherStudentExamsPage's "سنتر الاسايمنت" tab. Same field naming as
// QuizzesController.GetAll/LectureExamsController.GetAll's per-student
// shape (id/title/isTaken/score/totalMarks/reopenActive/forceReviewGranted)
// so the same generic _ExamsLikeListTab widget can drive this tab too.
public record AssignmentCenterStudentListItem(
    int Id,
    string Title,
    bool IsTaken,
    int? Score,
    int? TotalMarks,
    bool ReopenActive,
    bool ForceReviewGranted);

// POST AssignmentCenters/force-review — same idea as ForceQuizReviewRequest.
public record ForceAssignmentCenterReviewRequest(int AssignmentCenterId, int StudentId);

// POST AssignmentCenters/reopen — same idea as ReopenQuizRequest.
public record ReopenAssignmentCenterRequest(int AssignmentCenterId, int StudentId, int Minutes);

public record AssignmentCenterTakerDto(
    int StudentId,
    string StudentName,
    string GroupName,
    bool HasSubmitted,
    int? Mark,
    int? TotalMarks,
    DateTime? SubmittedAt);
