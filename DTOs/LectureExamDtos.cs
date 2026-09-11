namespace EduApi.DTOs;

// POST LectureExams/grade
// body: { "lectureExamId": int, "answers": [ { "questionId": int, "answer": "..." }, ... ] }
public record GradeLectureExamRequest(int LectureExamId, List<SubmitAnswerDto> Answers);

// POST LectureExams/change-answer-mark
public record ChangeLectureExamAnswerMarkRequest(int LectureExamId, int StudentId, int QuestionId, int Mark);

// POST LectureExams/edit-question
public record EditLectureExamQuestionRequest(int LectureExamId, int QuestionId, string Text, int Mark, List<string>? Choices, string Answer);

// POST LectureExams/edit
// Deliberately no group/unit/deadline fields to touch — a lecture exam
// doesn't have any (see LectureExam in Models/Entities.cs).
public record EditLectureExamRequest(int LectureExamId, string Title, int DurationInMinutes);

// GET LectureExams?studentId=..&p=.. — one item per LectureExam attached to
// any lecture the given student can reach, teacher-facing list used by
// TeacherStudentExamsPage's "امتحانات الحصص" tab. Same field naming as
// QuizListItem's per-student shape, plus lectureId/lectureTitle so the
// teacher can tell which lecture each one belongs to.
public record LectureExamListItem(
    int Id,
    string Title,
    int LectureId,
    string LectureTitle,
    bool IsTaken,
    int? Score,
    int? TotalMarks,
    bool ReopenActive,
    bool ForceReviewGranted);

// POST LectureExams/force-review — same idea as ForceQuizReviewRequest.
public record ForceLectureExamReviewRequest(int LectureExamId, int StudentId);

// POST LectureExams/reopen — same idea as ReopenQuizRequest.
public record ReopenLectureExamRequest(int LectureExamId, int StudentId, int Minutes);
