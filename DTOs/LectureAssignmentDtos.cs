namespace EduApi.DTOs;

// POST LectureAssignments/grade
// body: { "lectureAssignmentId": int, "answers": [ { "questionId": int, "answer": "..." }, ... ] }
public record GradeLectureAssignmentRequest(int LectureAssignmentId, List<SubmitAnswerDto> Answers);

// POST LectureAssignments/change-answer-mark
public record ChangeLectureAssignmentAnswerMarkRequest(int LectureAssignmentId, int StudentId, int QuestionId, int Mark);

// POST LectureAssignments/edit-question
public record EditLectureAssignmentQuestionRequest(int LectureAssignmentId, int QuestionId, string Text, int Mark, List<string>? Choices, string Answer);

// POST LectureAssignments/edit
// Deliberately no group/unit/deadline/duration fields to touch — a lecture
// assignment doesn't have any of them (see LectureAssignment in
// Models/Entities.cs). Title is the only thing that can change.
public record EditLectureAssignmentRequest(int LectureAssignmentId, string Title);

// GET LectureAssignments?studentId=..&p=.. — one item per LectureAssignment
// attached to any lecture the given student can reach, teacher-facing list
// used by TeacherStudentAssignmentsPage's "واجبات الحصص" tab. Same field
// naming as LectureExamListItem, minus timing.
public record LectureAssignmentListItem(
    int Id,
    string Title,
    int LectureId,
    string LectureTitle,
    bool IsTaken,
    int? Score,
    int? TotalMarks,
    bool ReopenActive,
    bool ForceReviewGranted);

// POST LectureAssignments/force-review — same idea as ForceAssignmentReviewRequest.
public record ForceLectureAssignmentReviewRequest(int LectureAssignmentId, int StudentId);

// POST LectureAssignments/reopen — same idea as ReopenAssignmentRequest.
public record ReopenLectureAssignmentRequest(int LectureAssignmentId, int StudentId, int Minutes);
