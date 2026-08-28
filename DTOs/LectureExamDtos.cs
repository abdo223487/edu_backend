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
