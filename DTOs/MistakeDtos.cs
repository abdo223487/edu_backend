namespace EduApi.DTOs;

/// <summary>
/// "أخطائي": تاب واحد فوق في الصفحة -- كورس (Unit) أو Online Lesson أو كتاب
/// خارجي مش مربوط بكورس الطالب.
/// </summary>
public record MistakeCourseDto(
    string Kind,   // Unit | OnlineLesson | ExternalBook
    int Id,
    string Name,
    int? SchoolYear,
    int? Month,
    string? ImageUrl);

/// <summary>
/// سؤال واحد الطالب غلط فيه (أو سابه من غير إجابة)، بنفس شكل وضع المراجعة:
/// نص السؤال + الصورة (لو فيه) + الاختيارات + إجابة الطالب + الإجابة الصحيحة.
/// </summary>
public record MistakeQuestionDto(
    int QuestionId,
    string Text,
    string? ImageUrl,
    string QuestionType,
    List<string> Choices,
    string? StudentAnswer,
    string? CorrectAnswer,
    int Mark,
    int? MarkAwarded);

/// <summary>
/// مصدر واحد (كويز / امتحان محاضرة / واجب / واجب سنتر / واجب محاضرة / محاولة
/// بنك أسئلة) فيه أسئلة غلط.
///  - SourceType: Quiz | LectureExam | Assignment | AssignmentCenter |
///                LectureAssignment | BankAttempt
///  - LectureName: اسم المحاضرة (لامتحانات وواجبات المحاضرات بس).
///  - ExternalBookName: اسم الكتاب الخارجي لو المحاضرة جوه كتاب خارجي مربوط بالكورس.
/// </summary>
public record MistakeSourceDto(
    string SourceType,
    int SourceId,
    string Title,
    string? LectureName,
    string? ExternalBookName,
    DateTime? Date,
    int? Score,
    int? TotalMarks,
    List<MistakeQuestionDto> Mistakes);

/// <summary>
/// تفاصيل أخطاء كورس واحد.
/// HiddenSources = عدد المصادر اللي اتسلّمت بس الديدلاين بتاعها لسه ماخلصش،
/// فالحل بتاعها (وبالتالي أخطاؤها) متخبّي لحد ما الديدلاين يعدّي -- نفس قاعدة
/// صفحات الامتحان والواجب.
/// </summary>
public record MistakesCourseDetailsDto(
    string Kind,
    int Id,
    string Name,
    int TotalMistakes,
    int HiddenSources,
    List<MistakeSourceDto> Sources);
