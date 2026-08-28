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

// BUGFIX: field was previously named "QuizTotalMarks" (serialized as
// "quizTotalMarks"), but every other grading response in this API
// (e.g. SubmitAssignmentResult) uses "totalMarks", and that's what the
// client's exam-result screen reads. Because "totalMarks" was missing from
// the JSON, the client couldn't find the total and the score never showed
// up to the student after submitting the exam. Renamed to match.
public record GradeQuizResult(int Mark, int TotalMarks);

// POST Quizzes/change-answer-mark
// body: { "quizResultId"/"studentId"+"quizId", "questionId", "newMark" } (inferred shape)
public record ChangeAnswerMarkRequest(int QuizId, int StudentId, int QuestionId, int Mark);

// POST Quizzes/edit-question
// body: { "quizId": int, "questionId": int, "text": "...", "mark": int, "choices": ["..."], "answer": "..." }
// Lets a teacher fix a typo/mark/choice/correct-answer on an already-created
// exam question without deleting and recreating the whole quiz.
public record EditQuestionRequest(int QuizId, int QuestionId, string Text, int Mark, List<string>? Choices, string Answer);

// POST Quizzes/edit
// body: { "quizId": int, "title": "...", "durationInMinutes": int, "allowLateReview": bool }
// Lets a teacher fix the exam's own basic info (name/duration/late-review
// policy) after creation, without touching its questions/groups/unit.
public record EditQuizRequest(int QuizId, string Title, int DurationInMinutes, bool AllowLateReview);

// Field names match Takers.dart's expected JSON exactly: quizMark (not
// "score"), totalQuizMarks (not "totalMarks"), date, groupName.
// StudentId is sent as a STRING (not int) — ExamReview_for_Taker.dart's
// StudentFullExamReviewPage widget declares `final String studentId;` and
// Takers.dart forwards this field straight through untouched, so the JSON
// shape has to match that String type or the widget crashes (gray screen)
// when Takers.dart navigates to it.
// POST Quizzes/force-review
// body: { "quizId": int, "studentId": int }
// Teacher-only, from the student's own "Exams" quick-action list: the next
// time this student opens the exam, they land straight in review mode
// (correct answers shown) even though they never submitted.
public record ForceQuizReviewRequest(int QuizId, int StudentId);

// POST Quizzes/reopen
// body: { "quizId": int, "studentId": int, "minutes": int }
// Teacher-only: wipes this student's prior attempt (if any) and gives them
// a fresh, normal attempt window of exactly "minutes" starting now,
// regardless of the quiz's own Deadline.
public record ReopenQuizRequest(int QuizId, int StudentId, int Minutes);

public record TakerDto(
    string StudentId,
    string StudentName,
    string GroupName,
    bool HasSubmitted,
    int? QuizMark,
    int TotalQuizMarks,
    DateTime? Date);
