using ClosedXML.Excel;
using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/Analytics. This module has the widest variety of teacher dashboard
/// endpoints in the app. Response shapes below are inferred from how each screen
/// consumes the JSON (field names actually read via `e["..."]`); pure display/graph
/// aggregation math is implemented straightforwardly since the client only renders
/// whatever numbers are returned.
///
///  GET Analytics/students?p=..&groupId=..|&schoolYear=..
///  GET Analytics/failed-students?p=..
///  GET Analytics/top-students                          (teacher AND student use this route)
///  GET Analytics/attendance?groupId=..|&schoolYear=..
///  GET Analytics/sheet                                  (file download)
///  GET Analytics/sheet/lectures/{id}                     (file download)
///  GET Analytics/notebooks/{notebookId}/paid|unpaid?groupId=..|&schoolYear=..&p=..
///  GET Analytics/sheets/notebooks/{notebookId}/paid|unpaid?groupId=..|&schoolYear=..  (file download)
///  GET Analytics/student                                (student's own summary)
///  GET Analytics/student/online                         (student's own online-quiz summary)
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AnalyticsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    public AnalyticsController(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    [HttpGet("students")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetStudentsAnalytics(
        [FromQuery] int p = 1, [FromQuery] int? groupId = null, [FromQuery] int? schoolYear = null)
    {
        var query = _db.Students.AsNoTracking().AsQueryable();
        if (groupId.HasValue) query = query.Where(s => s.GroupMemberships.Any(m => m.GroupId == groupId.Value));
        else if (schoolYear.HasValue) query = query.Where(s => s.SchoolYear == schoolYear.Value);

        // Only Id/Name/Group name are ever read below -- project at the SQL
        // level instead of materializing (and Include()-joining) the full
        // Student entity just to throw most of its columns away.
        var students = await query
            .OrderBy(s => s.Name)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .Select(s => new { s.Id, s.Name, GroupName = s.Group!.Name })
            .ToListAsync();

        // PERFORMANCE: this used to run 2 extra queries PER STUDENT in a loop
        // (2 * PageSize round trips per page load). Batch both lookups into a
        // single query each for the whole page, then pick the latest result
        // per student in memory -- same result, 2 queries total instead of
        // up to 40+.
        var pageStudentIds = students.Select(s => s.Id).ToList();

        // Only StudentId/Marks/TotalMarks/Date/Id are read below -- select
        // those columns instead of the whole result row (Title/TeacherId etc.
        // are never touched here).
        var lastQuizByStudent = (await _db.CenterQuizResults.AsNoTracking()
                .Where(r => pageStudentIds.Contains(r.StudentId))
                .Select(r => new { r.Id, r.StudentId, r.Marks, r.TotalMarks, r.Date })
                .ToListAsync())
            .GroupBy(r => r.StudentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Date).First());

        var lastHomeworkByStudent = (await _db.HomeworkResults.AsNoTracking()
                .Where(r => pageStudentIds.Contains(r.StudentId))
                .Select(r => new { r.Id, r.StudentId, r.Marks, r.TotalMarks, r.Date })
                .ToListAsync())
            .GroupBy(r => r.StudentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Date).First());

        var result = students.Select(s =>
        {
            lastQuizByStudent.TryGetValue(s.Id, out var lastQuiz);
            lastHomeworkByStudent.TryGetValue(s.Id, out var lastHomework);

            return new
            {
                id = s.Id,
                name = s.Name,
                groupName = s.GroupName,
                lastQuizMarks = lastQuiz != null ? $"{lastQuiz.Marks}/{lastQuiz.TotalMarks}" : null,
                lastQuizResultId = lastQuiz?.Id,
                lastHomeworkMarks = lastHomework != null ? $"{lastHomework.Marks}/{lastHomework.TotalMarks}" : null,
                lastHomeworkResultId = lastHomework?.Id
            };
        }).ToList();

        return Ok(result);
    }

    // Real client contract (failedstudent analytics .dart): one row PER STUDENT
    // (not per failed result) with groupName, averageMarks, failedQuizzesCount,
    // lastQuizMarks — aggregated across all of that student's center-quiz results.
    // "Failed" = their average is below the pass mark.
    [HttpGet("failed-students")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetFailedStudents(
        [FromQuery] int p = 1, [FromQuery] int? groupId = null, [FromQuery] int? schoolYear = null)
    {
        const int passMarkPercent = 50;

        var studentQuery = _db.Students.AsNoTracking().AsQueryable();
        if (groupId.HasValue) studentQuery = studentQuery.Where(s => s.GroupMemberships.Any(m => m.GroupId == groupId.Value));
        else if (schoolYear.HasValue) studentQuery = studentQuery.Where(s => s.SchoolYear == schoolYear.Value);

        // Only Id/Name/Group name are used below -- project at the SQL level.
        var students = await studentQuery
            .Select(s => new { s.Id, s.Name, GroupName = s.Group!.Name })
            .ToListAsync();
        var studentIds = students.Select(s => s.Id).ToList();

        var allResults = await _db.CenterQuizResults.AsNoTracking()
            .Where(r => studentIds.Contains(r.StudentId))
            .Select(r => new { r.StudentId, r.Marks, r.TotalMarks, r.Date })
            .ToListAsync();

        var items = new List<object>();
        foreach (var s in students)
        {
            var resultsForStudent = allResults.Where(r => r.StudentId == s.Id).ToList();
            if (resultsForStudent.Count == 0) continue;

            var averagePercent = resultsForStudent.Average(r => r.TotalMarks > 0 ? r.Marks * 100.0 / r.TotalMarks : 0);
            if (averagePercent >= passMarkPercent) continue;

            var lastQuiz = resultsForStudent.OrderByDescending(r => r.Date).First();
            var failedCount = resultsForStudent.Count(r => r.TotalMarks > 0 && (r.Marks * 100.0 / r.TotalMarks) < passMarkPercent);

            items.Add(new
            {
                name = s.Name,
                groupName = s.GroupName,
                averageMarks = Math.Round(averagePercent, 1),
                failedQuizzesCount = failedCount,
                lastQuizMarks = $"{lastQuiz.Marks}/{lastQuiz.TotalMarks}"
            });
        }

        var page = items.Skip((p - 1) * PagingDefaults.PageSize).Take(PagingDefaults.PageSize);
        return Ok(page);
    }

    [HttpGet("top-students")]
    public async Task<IActionResult> GetTopStudents(
        [FromQuery] int? groupId = null, [FromQuery] int? schoolYear = null, [FromQuery] bool byYear = false)
    {
        // Priority: explicit groupId > explicit schoolYear/byYear > student's own group from JWT.
        var effectiveGroupId = groupId ?? (User.IsInRole(Roles.Student) ? User.GetGroupId(_tenant.CurrentTenantId) : null);

        var query = from result in _db.CenterQuizResults.AsNoTracking()
                     group result by result.StudentId into g
                     select new { StudentId = g.Key, TotalMarks = g.Sum(x => x.Marks) };

        var top = await query.OrderByDescending(x => x.TotalMarks).Take(5).ToListAsync();
        var ids = top.Select(t => t.StudentId).ToList();

        var studentsQuery = _db.Students.AsNoTracking().Where(s => ids.Contains(s.Id));
        if (effectiveGroupId.HasValue) studentsQuery = studentsQuery.Where(s => s.GroupMemberships.Any(m => m.GroupId == effectiveGroupId.Value));
        else if (schoolYear.HasValue) studentsQuery = studentsQuery.Where(s => s.SchoolYear == schoolYear.Value);

        // Only Id/Name/Group name are read below -- project at the SQL level.
        var students = await studentsQuery
            .Select(s => new { s.Id, s.Name, GroupName = s.Group!.Name })
            .ToDictionaryAsync(s => s.Id);

        // Need per-student result lists for averageMarks/lastQuizMarks (client
        // reads both — see "topfive analytics .dart"). Only the columns
        // actually used in the aggregation below are selected.
        var allResultsByStudent = await _db.CenterQuizResults.AsNoTracking()
            .Where(r => ids.Contains(r.StudentId))
            .Select(r => new { r.StudentId, r.Marks, r.TotalMarks, r.Date })
            .ToListAsync();

        var items = top.Where(t => students.ContainsKey(t.StudentId))
            .Select((t, i) =>
            {
                var resultsForStudent = allResultsByStudent.Where(r => r.StudentId == t.StudentId).ToList();
                var averagePercent = resultsForStudent.Count == 0 ? 0 :
                    resultsForStudent.Average(r => r.TotalMarks > 0 ? r.Marks * 100.0 / r.TotalMarks : 0);
                var lastQuiz = resultsForStudent.OrderByDescending(r => r.Date).FirstOrDefault();

                return new
                {
                    rank = i + 1,
                    studentId = t.StudentId,
                    name = students[t.StudentId].Name,
                    groupName = students[t.StudentId].GroupName,
                    totalMarks = t.TotalMarks,
                    averageMarks = Math.Round(averagePercent, 1),
                    lastQuizMarks = lastQuiz != null ? $"{lastQuiz.Marks}/{lastQuiz.TotalMarks}" : null
                };
            });

        return Ok(items);
    }

    [HttpGet("attendance")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetAttendanceGraph([FromQuery] int? groupId, [FromQuery] int? schoolYear)
    {
        var lectureQuery = _db.Lectures.AsNoTracking().AsQueryable();
        if (groupId.HasValue) lectureQuery = lectureQuery.Where(l => _db.LectureGroupLinks.Any(x => x.LectureId == l.Id && x.GroupId == groupId.Value));
        else if (schoolYear.HasValue) lectureQuery = lectureQuery.Where(l => l.SchoolYear == schoolYear.Value);

        // Only Id/Name are read below -- project at the SQL level instead of
        // pulling every Lecture column.
        var lectures = await lectureQuery.Select(l => new { l.Id, l.Name }).ToListAsync();
        var lectureIds = lectures.Select(l => l.Id).ToList();
        var countsByLecture = await _db.Attendances.AsNoTracking()
            .Where(a => lectureIds.Contains(a.LectureId))
            .GroupBy(a => a.LectureId)
            .Select(g => new { lectureId = g.Key, count = g.Count() })
            .ToDictionaryAsync(x => x.lectureId, x => x.count);

        // Client reads "lectureName"/"attendanceCount" (graph.dart) and maps
        // attendanceCount straight into a non-nullable int, so every lecture
        // must appear with a real name and a real (possibly zero) count —
        // never null, or the chart crashes.
        var result = lectures.Select(l => new
        {
            lectureName = l.Name,
            attendanceCount = countsByLecture.TryGetValue(l.Id, out var c) ? c : 0
        });

        return Ok(result);
    }

    // File-download endpoints. The Flutter client (createCenterlecture.dart,
    // graph.dart, notebbok analytics .dart) always forces a ".xlsx" filename and
    // reads the real name from the "Content-Disposition" header — so these must
    // return an actual OOXML workbook, not CSV text wearing an .xlsx extension.
    // ASP.NET Core's File(bytes, contentType, fileDownloadName) overload sets the
    // Content-Disposition header automatically.
    [HttpGet("sheet/lectures/{id:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> DownloadLectureSheet(int id)
    {
        var lecture = await _db.Lectures.AsNoTracking().FirstOrDefaultAsync(e => e.Id == (id));
        if (lecture == null) return NotFound(new { message = "Lecture not found." });

        // Only StudentId/Date and Name/PhoneNumber are used in the rows below.
        var attendees = await _db.Attendances.AsNoTracking().Where(a => a.LectureId == id)
            .Select(a => new { a.StudentId, a.Date })
            .ToListAsync();
        var studentIds = attendees.Select(a => a.StudentId).ToList();
        var students = await _db.Students.AsNoTracking().Where(s => studentIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name, s.PhoneNumber })
            .ToDictionaryAsync(s => s.Id);

        var rows = attendees.Select(a =>
        {
            var s = students.GetValueOrDefault(a.StudentId);
            return new[] { s?.Name ?? "", s?.PhoneNumber ?? "", a.Date.ToString("O") };
        });

        var bytes = BuildXlsx("Attendance", new[] { "Name", "PhoneNumber", "Date" }, rows);
        return XlsxFile(bytes, $"lecture_{id}_attendance.xlsx");
    }

    [HttpGet("sheet")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> DownloadGeneralSheet([FromQuery] int? schoolYear, [FromQuery] int? groupId)
    {
        var query = _db.Students.AsNoTracking().AsQueryable();
        if (groupId.HasValue) query = query.Where(s => s.GroupMemberships.Any(m => m.GroupId == groupId.Value));
        else if (schoolYear.HasValue) query = query.Where(s => s.SchoolYear == schoolYear.Value);

        // Only Name/PhoneNumber end up in the exported rows.
        var students = await query.Select(s => new { s.Name, s.PhoneNumber }).ToListAsync();
        var rows = students.Select(s => new[] { s.Name, s.PhoneNumber });

        var bytes = BuildXlsx("Students", new[] { "Name", "PhoneNumber" }, rows);
        return XlsxFile(bytes, "students_sheet.xlsx");
    }

    [HttpGet("notebooks/{notebookId:int}/paid")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public Task<IActionResult> GetNotebookPaidStudents(int notebookId, [FromQuery] int? groupId, [FromQuery] int? schoolYear, [FromQuery] int p = 1)
        => GetNotebookStudentsByPaymentStatus(notebookId, paid: true, groupId, schoolYear, p);

    [HttpGet("notebooks/{notebookId:int}/unpaid")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public Task<IActionResult> GetNotebookUnpaidStudents(int notebookId, [FromQuery] int? groupId, [FromQuery] int? schoolYear, [FromQuery] int p = 1)
        => GetNotebookStudentsByPaymentStatus(notebookId, paid: false, groupId, schoolYear, p);

    private async Task<IActionResult> GetNotebookStudentsByPaymentStatus(
        int notebookId, bool paid, int? groupId, int? schoolYear, int p)
    {
        var notebook = await _db.Notebooks.AsNoTracking().FirstOrDefaultAsync(e => e.Id == (notebookId));
        if (notebook == null) return NotFound(new { message = "Notebook not found." });

        var query = _db.Students.AsNoTracking().AsQueryable();
        if (groupId.HasValue) query = query.Where(s => s.GroupMemberships.Any(m => m.GroupId == groupId.Value));
        else if (schoolYear.HasValue) query = query.Where(s => s.SchoolYear == schoolYear.Value);
        else query = query.Where(s => s.GroupMemberships.Any(m => notebook.GroupIds.Contains(m.GroupId)));

        // Only Id/Name/PhoneNumber are read below -- project at the SQL level.
        var students = await query.OrderBy(s => s.Name)
            .Skip((p - 1) * PagingDefaults.PageSize).Take(PagingDefaults.PageSize)
            .Select(s => new { s.Id, s.Name, s.PhoneNumber })
            .ToListAsync();

        var payments = await _db.NotebookPayments.AsNoTracking()
            .Where(pay => pay.NotebookId == notebookId)
            .ToListAsync();
        var paidByStudent = payments.GroupBy(pay => pay.StudentId)
            .ToDictionary(g => g.Key, g => g.Sum(pay => pay.DiscountedPrice ?? pay.Price));

        // Client (notebbok analytics .dart) reads s['student']['name'] (a nested
        // object, not a flat one), plus s['totalPaid'] and s['price'] at the top
        // level for the progress bar — none of which existed before.
        var filtered = students
            .Where(s => paid ? paidByStudent.ContainsKey(s.Id) : !paidByStudent.ContainsKey(s.Id))
            .Select(s => new
            {
                student = new { id = s.Id, name = s.Name, phoneNumber = s.PhoneNumber },
                totalPaid = paidByStudent.TryGetValue(s.Id, out var total) ? total : 0,
                price = notebook.Price
            });

        return Ok(filtered);
    }

    [HttpGet("sheets/notebooks/{notebookId:int}/paid")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public Task<IActionResult> DownloadNotebookPaidSheet(int notebookId, [FromQuery] int? groupId, [FromQuery] int? schoolYear)
        => DownloadNotebookSheet(notebookId, paid: true, groupId, schoolYear);

    [HttpGet("sheets/notebooks/{notebookId:int}/unpaid")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public Task<IActionResult> DownloadNotebookUnpaidSheet(int notebookId, [FromQuery] int? groupId, [FromQuery] int? schoolYear)
        => DownloadNotebookSheet(notebookId, paid: false, groupId, schoolYear);

    private async Task<IActionResult> DownloadNotebookSheet(int notebookId, bool paid, int? groupId, int? schoolYear)
    {
        var result = await GetNotebookStudentsByPaymentStatus(notebookId, paid, groupId, schoolYear, 1) as OkObjectResult;
        var items = (result?.Value as IEnumerable<object>) ?? Enumerable.Empty<object>();
        var rows = items.Select(i =>
        {
            var type = i.GetType();
            var studentObj = type.GetProperty("student")?.GetValue(i);
            var studentType = studentObj?.GetType();
            var name = studentType?.GetProperty("name")?.GetValue(studentObj)?.ToString() ?? "";
            var phone = studentType?.GetProperty("phoneNumber")?.GetValue(studentObj)?.ToString() ?? "";
            return new[] { name, phone };
        });

        var bytes = BuildXlsx(paid ? "Paid" : "Unpaid", new[] { "Name", "PhoneNumber" }, rows);
        var filename = $"notebook_{notebookId}_{(paid ? "paid" : "unpaid")}.xlsx";
        return XlsxFile(bytes, filename);
    }

    // ── xlsx helpers ─────────────────────────────────────────────────────
    private static byte[] BuildXlsx(string sheetName, string[] headers, IEnumerable<string[]> rows)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add(sheetName);

        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];
        ws.Row(1).Style.Font.Bold = true;

        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Length; c++)
                ws.Cell(r, c + 1).Value = row[c];
            r++;
        }

        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private FileContentResult XlsxFile(byte[] bytes, string filename) =>
        File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);

    // Real contract: { "name": "...", "totalMarks": 0 } — shown to the student as "coins".
    // totalMarks aggregates center quizzes + homework (their combined gamified score).
    [HttpGet("student")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> GetOwnAnalytics()
    {
        var studentId = User.GetUserId();
        var studentName = await _db.Students.AsNoTracking()
            .Where(e => e.Id == studentId)
            .Select(e => (string?)e.Name)
            .FirstOrDefaultAsync();
        var quizMarks = await _db.CenterQuizResults.AsNoTracking().Where(r => r.StudentId == studentId).SumAsync(r => r.Marks);
        var homeworkMarks = await _db.HomeworkResults.AsNoTracking().Where(r => r.StudentId == studentId).SumAsync(r => r.Marks);

        return Ok(new { name = studentName, totalMarks = quizMarks + homeworkMarks });
    }

    // Real contract: { "totalMarks": 0, "averageMarks": 0 } — scoped to ONLINE quizzes only
    // (center-quiz averages are computed client-side from Students/quiz-results/center).
    [HttpGet("student/online")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> GetOwnOnlineAnalytics()
    {
        var studentId = User.GetUserId();
        var scores = await _db.QuizResults.AsNoTracking().Where(r => r.StudentId == studentId)
            .Select(r => r.Score)
            .ToListAsync();

        return Ok(new
        {
            totalMarks = scores.Sum(),
            averageMarks = scores.Count == 0 ? 0 : scores.Average()
        });
    }
}
