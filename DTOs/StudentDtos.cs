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

// MULTI-TENANT: link an existing student (from another teacher) to the caller's
// own tenant via phone number, optionally granting Unit access at the same time.
public record LinkStudentRequest(string PhoneNumber, int GroupId, List<int>? UnitIds);

// POST Students/codes body: { code }
public record RedeemCodeRequest(string Code);

// POST Students/{notebookId}/pay body: { studentId, amount, lectureId }
public record PayNotebookRequest(int StudentId, int Amount, int? LectureId);

// POST Students/quiz-results/center/add body: { studentId, quizTotalMarks, mark }
public record AddCenterQuizResultRequest(int StudentId, int QuizTotalMarks, int Mark);

// POST Students/homework-results/add body: { studentId, totalMarks, mark }
public record AddHomeworkResultRequest(int StudentId, int TotalMarks, int Mark);

public record QrCodeResponse(string StudentId, string QrPayload);

// POST Students/import (multipart/form-data: file + groupId) and
// POST Students/import/google-sheet (body: { url, groupId }) — bulk-register
// students from an Excel sheet or a Google Sheet. One entry per row
// processed, success or not, so the teacher can see exactly what happened to
// every row (including which ones were skipped and why) instead of just a
// total count.
// Bound as a single class (not two separate [FromForm] params) because
// Swashbuckle/Swagger cannot generate a schema for an action with more than
// one [FromForm] parameter when one of them is IFormFile — it throws and
// takes down the ENTIRE /swagger/v1/swagger.json endpoint, not just this one.
public class ImportFromExcelForm
{
    public Microsoft.AspNetCore.Http.IFormFile File { get; set; } = default!;
    public int? GroupId { get; set; }
}

public record ImportStudentsRowResult(int Row, string? Name, string? PhoneNumber, string Status, string Message, int? StudentId);
public record ImportStudentsResponse(int TotalRows, int Created, int Linked, int Failed, List<ImportStudentsRowResult> Results);

// POST Students/import/preview (multipart/form-data: file) — returns the
// sheet's ACTUAL columns (1-based index, raw header text, a few sample
// values) plus a best-effort suggested field mapping, so a caller (e.g. a
// SuperAdmin's admin panel) can show the sheet to a person and let them
// confirm/correct which column means what BEFORE importing — instead of the
// backend guessing silently and failing if the sheet's layout/labels don't
// match what's expected.
public record ImportColumnPreview(int Index, string Header, List<string> SampleValues);
public record ImportPreviewResponse(List<ImportColumnPreview> Columns, Dictionary<string, int> SuggestedMapping, string[] AvailableFields);

// POST Students/import/mapped (multipart/form-data: file, groupId?, mapping,
// hasHeaderRow?) — same bulk-import as Students/import, but column meaning
// comes ENTIRELY from `mapping` (a JSON object of fieldName -> 1-based column
// number, e.g. {"Name":2,"PhoneNumber":1,"GroupName":3}) instead of matching
// header text. This makes the import fully dynamic: it works no matter what
// order the sheet's columns are in, what language/wording the headers use, or
// even if there's no usable header row at all (set hasHeaderRow=false and the
// mapping still applies — row 1 is then treated as real data, not a header).
// Valid field names: Name, PhoneNumber, ParentPhoneNumber, UserName,
// Password, GroupId, GroupName (same set Students/import/preview reports via
// AvailableFields). Name and PhoneNumber are required in the mapping.
public class ImportMappedForm
{
    public Microsoft.AspNetCore.Http.IFormFile File { get; set; } = default!;
    public int? GroupId { get; set; }
    public string Mapping { get; set; } = "{}";
    public bool? HasHeaderRow { get; set; }
}

// POST Students/import/google-sheet body: { url, groupId }
// `url` is the normal Google Sheets share/edit link (or a Google Form's
// RESPONSES sheet link — not the form's own /viewform link); `groupId` is
// the optional default group (same semantics as the `groupId` form field on
// Students/import — omit it if every row has its own GroupId/GroupName column).
public record ImportFromGoogleSheetRequest(string Url, int? GroupId);

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
