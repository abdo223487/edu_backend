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
