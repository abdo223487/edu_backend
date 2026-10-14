namespace EduApi.DTOs;

/// <summary>
/// "شهاداتي": تقدّم الطالب في نوع واحد من المتطلبات (done من total).
/// total == 0 معناها إن الكورس مفيهوش النوع ده أصلاً (مش مطلوب من الطالب).
/// </summary>
public record CertificateProgressItem(int Done, int Total);

/// <summary>
/// كورس (Unit) واحد الطالب مشترك فيه عند المدرس الحالي، مع حالة الشهادة بتاعته.
///  - IsClosed:   المدرس عمل كورس أحدث في نفس السنة الدراسية (وقتها الكورس ده
///                بيتعتبر خلص وبتتوزع شهاداته).
///  - IsEligible: الطالب خلّص كل المطلوب (كويزات، امتحانات الحصص، حضور السنتر،
///                كل أنواع الواجبات، ومشاهدة الفيديوهات اللي ليها عدد مشاهدات).
///  - IsIssued:   IsClosed && IsEligible -- الشهادة بتظهر للطالب.
///  - CompletedAt: تاريخ آخر حاجة خلّصها الطالب من المطلوب (UTC) -- بيتحسب بس
///                لما IsEligible = true، وبيتعرض كتاريخ على الشهادة.
/// </summary>
public record CertificateCourseDto(
    int UnitId,
    string UnitName,
    int SchoolYear,
    int? Month,
    string? ImageUrl,
    bool IsClosed,
    bool IsEligible,
    bool IsIssued,
    int Percent,
    DateTime? CompletedAt,
    CertificateProgressItem Quizzes,
    CertificateProgressItem LectureExams,
    CertificateProgressItem Attendance,
    CertificateProgressItem Assignments,
    CertificateProgressItem Videos);

public record MyCertificatesResponse(
    string StudentName,
    string TeacherName,
    List<CertificateCourseDto> Courses);
