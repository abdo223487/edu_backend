namespace EduApi.DTOs;

// POST Students
public record CreateStudentRequest(
    string PhoneNumber,
    string Name,
    int GroupId,
    List<int> UnitIds,
    string? ParentPhoneNumber,
    string? UserName,
    string? Password);

public record StudentListItem(int Id, string Name, string PhoneNumber, string? UserName, int GroupId, string? GroupName, bool IsSuspended, bool IsCancelled, string Status, string? SuspensionType);

public record StudentDetailDto(
    int Id, string Name, string PhoneNumber, string? ParentPhoneNumber, string? UserName,
    int GroupId, string? GroupName, int SchoolYear, bool IsSuspended, bool IsCancelled, DateTime CreatedAt,
    List<int> SubscribedUnitIds);

// PUT Students/{id}/group  body: { groupId }
public record ChangeGroupRequest(int GroupId);

// POST Students/suspend  body: { studentId, comment }
public record SuspendRequest(int StudentId, string Comment);

// POST Students/reactivate body: { studentId, comment }
public record ReactivateRequest(int StudentId, string Comment);

// POST Students/subscribe|unsubscribe/unit  body: { studentId, unitId }
public record UnitSubscriptionRequest(int StudentId, int UnitId);

// POST Students/codes body: { code }
public record RedeemCodeRequest(string Code);

// POST Students/{notebookId}/pay body: { studentId, amount, lectureId }
public record PayNotebookRequest(int StudentId, int Amount, int? LectureId);

// POST Students/quiz-results/center/add body: { studentId, quizTotalMarks, mark }
public record AddCenterQuizResultRequest(int StudentId, int QuizTotalMarks, int Mark);

// POST Students/homework-results/add body: { studentId, totalMarks, mark }
public record AddHomeworkResultRequest(int StudentId, int TotalMarks, int Mark);

public record QrCodeResponse(string StudentId, string QrPayload);

// GET Students/attendance response item — the client reads exactly `lectureName`
// and `isAttended` (bool), computing attended/absent counts locally by filtering
// this array. One entry per lecture the student's group/unit is scheduled for,
// not one entry per attendance record (a student can be *absent*, which still
// needs a row with isAttended == false).
public record AttendanceRecordDto(int LectureId, string LectureName, bool IsAttended, DateTime? Date);

// Field names match exactly what student_history_.dart reads: item['status'],
// item['suspensionType'], item['comment'], item['date']. There is no separate
// "type" of suspension tracked in the data model, so suspensionType is always
// null — the client only shows that extra badge when the value is non-empty.
public record StateHistoryItem(string Status, string? SuspensionType, string? Comment, DateTime Date);
