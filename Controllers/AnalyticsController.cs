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

        // Only the last 4 lectures in this scope -- most recent last, so the
        // graph reads left-to-right in chronological order.
        var lectures = await lectureQuery
            .OrderByDescending(l => l.Id)
            .Take(4)
            .Select(l => new { l.Id, l.Name })
            .ToListAsync();
        lectures.Reverse();

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

    // GET Analytics/sheet/lectures/{id}
    // One worksheet PER DAY this lecture was actually attended on (Egypt-local
    // calendar date, e.g. "17-9-2026") -- a Center lecture can recur across
    // several dates, so a single flat list used to mix every date together.
    // Each row: Name, PhoneNumber, ParentPhoneNumber, GroupName, and the time
    // the student attended, converted to Egypt local time.
    [HttpGet("sheet/lectures/{id:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> DownloadLectureSheet(int id)
    {
        var lecture = await _db.Lectures.AsNoTracking().FirstOrDefaultAsync(e => e.Id == (id));
        if (lecture == null) return NotFound(new { message = "Lecture not found." });

        var attendees = await _db.Attendances.AsNoTracking().Where(a => a.LectureId == id)
            .Select(a => new { a.StudentId, a.Date })
            .ToListAsync();

        var studentIds = attendees.Select(a => a.StudentId).Distinct().ToList();
        var students = await _db.Students.AsNoTracking().Where(s => studentIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name, s.PhoneNumber, s.ParentPhoneNumber })
            .ToDictionaryAsync(s => s.Id);
        // Group name under the CURRENT tenant -- a student can belong to more
        // than one teacher's group, so this must be resolved per-tenant, not
        // read off the student's legacy single GroupId.
        var groupNames = await _db.GetTenantGroupNamesAsync(studentIds);

        var sheets = attendees
            .Select(a => new { a.StudentId, Local = ToEgyptTime(a.Date) })
            .GroupBy(a => a.Local.Date)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var rows = g.Select(a =>
                {
                    students.TryGetValue(a.StudentId, out var s);
                    return new[]
                    {
                        s?.Name ?? "",
                        s?.PhoneNumber ?? "",
                        s?.ParentPhoneNumber ?? "",
                        groupNames.GetValueOrDefault(a.StudentId, ""),
                        a.Local.ToString("hh:mm tt")
                    };
                });
                return (
                    SheetName: SafeSheetName(g.Key.ToString("d-M-yyyy")),
                    Headers: new[] { "Name", "PhoneNumber", "ParentPhoneNumber", "GroupName", "AttendedAt" },
                    Rows: rows
                );
            });

        var bytes = BuildMultiSheetXlsx(sheets);
        return XlsxFile(bytes, $"lecture_{id}_attendance.xlsx");
    }

    // GET Analytics/sheet?schoolYear=..&groupId=..
    // ABSENT students, not present ones. Up to 4 worksheets, one per each of
    // the last 4 Center lectures targeted at the requested group (or every
    // group of the requested year, or every group of the caller's tenant if
    // neither filter is given) -- most recent lecture first. Each sheet lists
    // the students who belong to that scope but have NO Attendance row at all
    // for that specific lecture: Name, PhoneNumber, ParentPhoneNumber.
    [HttpGet("sheet")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> DownloadGeneralSheet([FromQuery] int? schoolYear, [FromQuery] int? groupId)
    {
        var groupsQuery = _db.Groups.AsNoTracking().AsQueryable();
        if (groupId.HasValue) groupsQuery = groupsQuery.Where(g => g.Id == groupId.Value);
        else if (schoolYear.HasValue) groupsQuery = groupsQuery.Where(g => g.SchoolYear == schoolYear.Value);
        var groupIds = await groupsQuery.Select(g => g.Id).ToListAsync();

        var headers = new[] { "Name", "PhoneNumber", "ParentPhoneNumber" };
        if (groupIds.Count == 0)
        {
            var emptyBytes = BuildMultiSheetXlsx(
                Enumerable.Empty<(string SheetName, string[] Headers, IEnumerable<string[]> Rows)>());
            return XlsxFile(emptyBytes, "absent_students.xlsx");
        }

        // Attendance-taking only ever applies to Center (on-site) lectures --
        // Online lectures are watched on-demand and have no "absent" concept
        // (same reasoning already used in Students/attendance).
        // GroupIds is a [NotMapped] CSV-backed property, so the group-overlap
        // filter has to run in memory after loading candidate lectures.
        var candidateLectures = await _db.Lectures.AsNoTracking()
            .Where(l => l.AttendanceMethod == AttendanceMethod.Center)
            .Select(l => new { l.Id, l.Name, l.GroupIdsCsv })
            .ToListAsync();

        var targetLectures = candidateLectures
            .Select(l => new { l.Id, l.Name, GroupIds = l.GroupIdsCsv.Length == 0 ? new List<int>() : l.GroupIdsCsv.Split(',').Select(int.Parse).ToList() })
            .Where(l => l.GroupIds.Any(groupIds.Contains))
            .OrderByDescending(l => l.Id)
            .Take(4)
            .ToList();

        var lectureIds = targetLectures.Select(l => l.Id).ToList();

        var students = await _db.Students.AsNoTracking()
            .Where(s => s.GroupMemberships.Any(m => groupIds.Contains(m.GroupId)))
            .Select(s => new { s.Id, s.Name, s.PhoneNumber, s.ParentPhoneNumber })
            .ToListAsync();
        var studentIds = students.Select(s => s.Id).ToList();

        var attendedPairs = (await _db.Attendances.AsNoTracking()
                .Where(a => lectureIds.Contains(a.LectureId) && studentIds.Contains(a.StudentId))
                .Select(a => new { a.LectureId, a.StudentId })
                .ToListAsync())
            .ToHashSet();

        var sheets = targetLectures.Select((l, idx) =>
        {
            var absentees = students.Where(s => !attendedPairs.Contains(new { LectureId = l.Id, StudentId = s.Id }));
            var rows = absentees.Select(s => new[] { s.Name, s.PhoneNumber, s.ParentPhoneNumber ?? "" });
            return (
                SheetName: SafeSheetName($"{idx + 1}-{l.Name}"),
                Headers: headers,
                Rows: rows
            );
        });

        var bytes = BuildMultiSheetXlsx(sheets);
        return XlsxFile(bytes, "absent_students.xlsx");
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

    // "paid" file: TWO sheets -- "دفعوا كامل" (paid the notebook's FULL price)
    // and "دفعوا جزء" (paid only PART of it and never finished) -- each row
    // has Name/PhoneNumber/ParentPhoneNumber/AmountPaid, with a totals row at
    // the bottom summing the money actually collected on that sheet.
    //
    // "unpaid" file: one sheet, everyone with zero payments at all, now with
    // ParentPhoneNumber added.
    //
    // BUGFIX: this used to go through the PAGINATED
    // GetNotebookStudentsByPaymentStatus (always p=1), so a notebook with
    // more students than one page silently exported only the first page.
    // Sheet downloads now pull every matching student directly instead.
    private async Task<IActionResult> DownloadNotebookSheet(int notebookId, bool paid, int? groupId, int? schoolYear)
    {
        var notebook = await _db.Notebooks.AsNoTracking().FirstOrDefaultAsync(e => e.Id == (notebookId));
        if (notebook == null) return NotFound(new { message = "Notebook not found." });

        var query = _db.Students.AsNoTracking().AsQueryable();
        if (groupId.HasValue) query = query.Where(s => s.GroupMemberships.Any(m => m.GroupId == groupId.Value));
        else if (schoolYear.HasValue) query = query.Where(s => s.SchoolYear == schoolYear.Value);
        else query = query.Where(s => s.GroupMemberships.Any(m => notebook.GroupIds.Contains(m.GroupId)));

        var students = await query.OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.PhoneNumber, s.ParentPhoneNumber })
            .ToListAsync();

        var payments = await _db.NotebookPayments.AsNoTracking()
            .Where(pay => pay.NotebookId == notebookId)
            .ToListAsync();
        var paidByStudent = payments.GroupBy(pay => pay.StudentId)
            .ToDictionary(g => g.Key, g => g.Sum(pay => pay.DiscountedPrice ?? pay.Price));

        IEnumerable<(string SheetName, string[] Headers, IEnumerable<string[]> Rows)> sheets;

        if (paid)
        {
            var withTotals = students.Select(s => new
            {
                s.Name,
                s.PhoneNumber,
                s.ParentPhoneNumber,
                TotalPaid = paidByStudent.TryGetValue(s.Id, out var t) ? t : 0
            }).ToList();

            var fullPaid = withTotals.Where(s => s.TotalPaid > 0 && s.TotalPaid >= notebook.Price).ToList();
            var partialPaid = withTotals.Where(s => s.TotalPaid > 0 && s.TotalPaid < notebook.Price).ToList();

            sheets = new[]
            {
                ("دفعوا كامل", new[] { "Name", "PhoneNumber", "ParentPhoneNumber", "AmountPaid" }, BuildPaymentRows(fullPaid)),
                ("دفعوا جزء", new[] { "Name", "PhoneNumber", "ParentPhoneNumber", "AmountPaid" }, BuildPaymentRows(partialPaid)),
            };
        }
        else
        {
            var unpaid = students.Where(s => !paidByStudent.ContainsKey(s.Id))
                .Select(s => new[] { s.Name, s.PhoneNumber, s.ParentPhoneNumber ?? "" });

            sheets = new[]
            {
                ("Unpaid", new[] { "Name", "PhoneNumber", "ParentPhoneNumber" }, unpaid),
            };
        }

        var bytes = BuildMultiSheetXlsx(sheets);
        var filename = $"notebook_{notebookId}_{(paid ? "paid" : "unpaid")}.xlsx";
        return XlsxFile(bytes, filename);
    }

    // Per-student rows plus a trailing totals row summing AmountPaid across
    // everyone on the sheet -- "مجموع الفلوس اللي فيها" at the bottom.
    private static IEnumerable<string[]> BuildPaymentRows<T>(List<T> students)
        where T : class
    {
        var rows = new List<string[]>();
        var total = 0;
        foreach (dynamic s in students)
        {
            rows.Add(new[] { (string)s.Name, (string)s.PhoneNumber, (string?)s.ParentPhoneNumber ?? "", ((int)s.TotalPaid).ToString() });
            total += (int)s.TotalPaid;
        }
        rows.Add(new[] { "", "", "", "" });
        rows.Add(new[] { "الإجمالي", "", "", total.ToString() });
        return rows;
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

    // Multi-sheet variant of BuildXlsx above -- one worksheet per (name,
    // headers, rows) tuple, used by every export that now needs to split its
    // data across several sheets (per-day attendance, per-lecture absentees,
    // full-vs-partial notebook payments).
    private static byte[] BuildMultiSheetXlsx(
        IEnumerable<(string SheetName, string[] Headers, IEnumerable<string[]> Rows)> sheets)
    {
        using var workbook = new XLWorkbook();

        foreach (var sheet in sheets)
        {
            var ws = workbook.Worksheets.Add(SafeSheetName(sheet.SheetName));

            for (var c = 0; c < sheet.Headers.Length; c++)
                ws.Cell(1, c + 1).Value = sheet.Headers[c];
            ws.Row(1).Style.Font.Bold = true;

            var r = 2;
            foreach (var row in sheet.Rows)
            {
                for (var c = 0; c < row.Length; c++)
                    ws.Cell(r, c + 1).Value = row[c];
                r++;
            }

            ws.Columns().AdjustToContents();
        }

        // A workbook needs at least one worksheet -- if there was nothing to
        // export at all (e.g. no lectures/attendance yet), add a single empty
        // placeholder sheet instead of ClosedXML throwing on save.
        if (workbook.Worksheets.Count == 0)
            workbook.Worksheets.Add("Sheet1");

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    // Excel worksheet names: max 31 chars, and none of \ / ? * [ ] : allowed.
    // Also guards against duplicate names (e.g. two lectures that happen to
    // share the exact same title) by falling back to a generic name rather
    // than letting ClosedXML throw on a duplicate Add().
    private static readonly HashSet<string> _reservedSheetChars = new() { '\\'.ToString(), '/'.ToString(), '?'.ToString(), '*'.ToString(), '['.ToString(), ']'.ToString(), ':'.ToString() };
    private static string SafeSheetName(string name)
    {
        var cleaned = new string(name.Where(c => !_reservedSheetChars.Contains(c.ToString())).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "Sheet";
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    // EGYPT LOCAL TIME: attendance timestamps are stored in UTC (Attendance.Date
    // defaults to DateTime.UtcNow) -- convert to Egypt local time for display on
    // exported sheets. Tries the IANA id first (Linux/ICU, what this API runs
    // on), then the Windows id, then falls back to a fixed UTC+2 offset so this
    // never throws even on an unusual host.
    private static readonly TimeZoneInfo EgyptTimeZone = ResolveEgyptTimeZone();

    private static TimeZoneInfo ResolveEgyptTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo"); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        try { return TimeZoneInfo.FindSystemTimeZoneById("Egypt Standard Time"); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        return TimeZoneInfo.CreateCustomTimeZone("Egypt-Fallback", TimeSpan.FromHours(2), "Egypt", "Egypt");
    }

    private static DateTime ToEgyptTime(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, EgyptTimeZone);
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
