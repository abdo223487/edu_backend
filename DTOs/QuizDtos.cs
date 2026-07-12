namespace EduApi.DTOs;

public record QuestionDto(int Id, string Type, string Text, List<string> Choices, int Mark, string? ImageUrl);

// Teacher view includes the correct answer; student view omits it.
public record QuestionTeacherDto(int Id, string Type, string Text, List<string> Choices, string Answer, int Mark, string? ImageUrl);

public record QuizListItem(int Id, string Title, int UnitId, int DurationInMinutes, DateTime Deadline, List<int> GroupIds);

public record QuizDetailForStudent(int Id, string Title, int UnitId, int DurationInMinutes, DateTime Deadline, List<QuestionDto> Questions);

public record QuizDetailForTeacher(int Id, string Title, int UnitId, int DurationInMinutes, DateTime Deadline, List<QuestionTeacherDto> Questions);

// POST Quizzes/grade
// body: { "quizId": int, "answers": [ { "questionId": int, "answer": "..." }, ... ] }
public record SubmitAnswerDto(int QuestionId, string Answer);
public record GradeQuizRequest(int QuizId, List<SubmitAnswerDto> Answers);

public record GradeQuizResult(int Mark, int QuizTotalMarks);

// POST Quizzes/change-answer-mark
// body: { "quizResultId"/"studentId"+"quizId", "questionId", "newMark" } (inferred shape)
public record ChangeAnswerMarkRequest(int QuizId, int StudentId, int QuestionId, int Mark);

// Field names match Takers.dart's expected JSON exactly: quizMark (not
// "score"), totalQuizMarks (not "totalMarks"), date, groupName.
// StudentId is sent as a STRING (not int) — ExamReview_for_Taker.dart's
// StudentFullExamReviewPage widget declares `final String studentId;` and
// Takers.dart forwards this field straight through untouched, so the JSON
// shape has to match that String type or the widget crashes (gray screen)
// when Takers.dart navigates to it.
public record TakerDto(
    string StudentId,
    string StudentName,
    string GroupName,
    bool HasSubmitted,
    int? QuizMark,
    int TotalQuizMarks,
    DateTime? Date);
