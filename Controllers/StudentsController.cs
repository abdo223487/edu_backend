using System.Text;
using ClosedXML.Excel;
using EduApi.Common;
using EduApi.Data;
using EduApi.DTOs;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/Students. This is the largest surface in the app; see class-level
/// method XML docs for the exact endpoint each action reconstructs.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class StudentsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly Common.ITenantContext _tenant;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWhatsAppService _whatsApp;
    private readonly ICacheService _cache;
    private readonly ILogger<StudentsController> _logger;
    public StudentsController(AppDbContext db, Common.ITenantContext tenant, IHttpClientFactory httpClientFactory, IWhatsAppService whatsApp, ICacheService cache, ILogger<StudentsController> logger)
    {
        _db = db;
        _tenant = tenant;
        _httpClientFactory = httpClientFactory;
        _whatsApp = whatsApp;
        _cache = cache;
        _logger = logger;
    }

    // POST Students/recover-credentials  (public — no auth required, used from
    // the login screen before the student has any token). Body:
    // { phoneNumber, parentPhoneNumber } -> both must match the SAME student
    // row exactly. On match: generates a brand-new random password (the old
    // one can never be recovered since only its BCrypt hash is stored),
    // saves it, and returns { userName, newPassword } so the Flutter login
    // screen can show it to the student immediately.
    //
    // Uses IgnoreQueryFilters() deliberately: a student's login identity is
    // global across all teachers (same account, multiple teachers), so this
    // must NOT be scoped to any one tenant.
    public record RecoverCredentialsRequest(string PhoneNumber, string ParentPhoneNumber);

    [HttpPost("recover-credentials")]
    [AllowAnonymous]
    public async Task<IActionResult> RecoverCredentials([FromBody] RecoverCredentialsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PhoneNumber) || string.IsNullOrWhiteSpace(request.ParentPhoneNumber))
            return BadRequest(new { message = "رقم الطالب ورقم ولي الأمر مطلوبان." });

        var student = await _db.Students.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s =>
                s.PhoneNumber == request.PhoneNumber &&
                s.ParentPhoneNumber == request.ParentPhoneNumber);

        // Deliberately vague error: never reveal whether the phone number
        // exists at all, or whether it was the phone/parent-phone pair that
        // didn't match — that would let someone enumerate valid numbers.
        if (student == null)
            return NotFound(new { message = "لا يوجد طالب مطابق لهذا الرقم ورقم ولي الأمر." });

        var newPassword = GenerateTempPassword();
        student.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        await _db.SaveChangesAsync();
        await InvalidateStudentsCacheAsync();

        return Ok(new
        {
            userName = student.UserName,
            newPassword
        });
    }

    // POST Students
    // MULTI-TENANT DEDUPE FIX: previously this always inserted a brand new
    // Student row with zero regard for whether the phone number already
    // belonged to someone else's student — a teacher adding a student who
    // already had an account with a DIFFERENT teacher silently ended up with
    // TWO separate accounts (two different Ids, two different
    // usernames/passwords) for the same real person, instead of one shared
    // account visible under both tenants via StudentGroupMembership. Now this
    // does the same phone-number lookup Students/link does, and if a match is
    // found, folds into that existing account (adds a membership + any
    // requested unit subscriptions) instead of creating a duplicate — the
    // teacher doesn't need to know or care whether the student already
    // exists elsewhere; any UserName/Password passed in the request is
    // ignored in that case since the original account's login must win.
    [HttpPost]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> Create([FromBody] CreateStudentRequest request)
    {
        var group = await _db.Groups.FirstOrDefaultAsync(e => e.Id == (request.GroupId));
        if (group == null) return BadRequest(new { message = "Group not found." });

        // Cross-tenant lookup by design — this student may already exist under
        // a completely different teacher's tenant (see Students/link).
        var existing = await _db.Students.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.PhoneNumber == request.PhoneNumber);

        if (existing != null)
        {
            // RE-ADD FIX: this used to only check whether ANY membership row
            // existed for this teacher and, if so, no-op — but a row can
            // exist and be IsCancelled/IsSuspended (e.g. this same teacher
            // deleted this exact student earlier). Treating that as
            // "already linked" silently kept the student cancelled forever:
            // the teacher's Create call looked like it succeeded (and even
            // added unit subscriptions / bumped counts) but the student
            // never came back in Students/GetAll for that teacher. Now we
            // fetch the actual row and reactivate it if it was
            // cancelled/suspended, so re-adding a student really does bring
            // them back for this teacher.
            var membership = await _db.StudentGroupMemberships.IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.StudentId == existing.Id && m.Group!.TeacherId == group.TeacherId);
            if (membership == null)
            {
                _db.StudentGroupMemberships.Add(new StudentGroupMembership { StudentId = existing.Id, GroupId = group.Id });
            }
            else if (membership.IsCancelled || membership.IsSuspended)
            {
                membership.GroupId = group.Id;
                membership.IsCancelled = false;
                membership.IsSuspended = false;
                _db.StateHistoryEntries.Add(new StateHistoryEntry { StudentId = existing.Id, Action = "Reactivated", Reason = "Re-added by teacher" });
            }

            foreach (var unitId in request.UnitIds ?? new())
            {
                if (!await _db.StudentUnitSubscriptions
                        .AnyAsync(s => s.StudentId == existing.Id && s.UnitId == unitId && s.TeacherId == group.TeacherId))
                    _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { TeacherId = group.TeacherId, StudentId = existing.Id, UnitId = unitId });
            }
            await _db.SaveChangesAsync();
            await InvalidateStudentsCacheAsync();

            // NOTE: same tradeoff as Students/link — if this student is
            // already logged in elsewhere, their current access token won't
            // include this new membership until they log in again / refresh.
            return Ok(new
            {
                message = "A student with this phone number already existed, so they were linked to your group instead of creating a duplicate account.",
                student = ToListItem(existing, group.Name)
            });
        }

        var plainPassword = string.IsNullOrEmpty(request.Password) ? GenerateTempPassword() : request.Password;
        var student = new Student
        {
            Name = request.Name,
            PhoneNumber = request.PhoneNumber,
            ParentPhoneNumber = request.ParentPhoneNumber,
            UserName = request.UserName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(plainPassword),
            GroupId = request.GroupId,
            SchoolYear = group.SchoolYear
        };

        _db.Students.Add(student);
        await _db.SaveChangesAsync();

        // MULTI-TENANT: keep the membership table in sync with the legacy GroupId
        // from day one, so every student (old or new) is queryable the same way.
        _db.StudentGroupMemberships.Add(new StudentGroupMembership { StudentId = student.Id, GroupId = group.Id });

        foreach (var unitId in request.UnitIds ?? new())
            _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { TeacherId = group.TeacherId, StudentId = student.Id, UnitId = unitId });
        await _db.SaveChangesAsync();

        await SendWelcomeWhatsAppAsync(student, plainPassword);
        await InvalidateStudentsCacheAsync();

        return StatusCode(201, ToListItem(student, group.Name));
    }

    // POST Students/link  body: { phoneNumber, groupId }
    // MULTI-TENANT: lets a Teacher/AssistantAdmin subscribe an EXISTING student
    // (already registered under a DIFFERENT teacher) to their OWN tenant, by
    // looking them up via phone number and creating a StudentGroupMembership
    // under one of the caller's own Groups. The student keeps their single
    // login/password and can now switch between all their teachers via
    // X-TenantId. Idempotent: linking twice to the same teacher just no-ops.
    // NOTE: Students/Create (POST Students) now does this exact same lookup
    // and merge automatically, so a teacher never has to know in advance
    // whether a student already exists elsewhere — this endpoint is kept as
    // an explicit alternative (e.g. a dedicated "link existing student" UI
    // flow that doesn't collect a Name/etc.) but is no longer required to
    // avoid duplicate accounts.
    [HttpPost("link")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> LinkExistingStudent([FromBody] LinkStudentRequest request)
    {
        var group = await _db.Groups.FirstOrDefaultAsync(g => g.Id == request.GroupId);
        if (group == null) return BadRequest(new { message = "Group not found." });

        // Cross-tenant lookup by design — this student may not belong to the
        // caller's tenant yet, so the normal tenant-scoped filter must be bypassed.
        var student = await _db.Students.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.PhoneNumber == request.PhoneNumber);
        if (student == null)
            return NotFound(new { message = "No student with this phone number exists yet. Use Create instead." });

        // RE-ADD FIX: same reasoning as Students/Create above -- a membership
        // row can already exist for this teacher but be cancelled/suspended
        // (e.g. this teacher deleted this student before). Previously that
        // counted as "already linked" and was left untouched, so re-linking
        // never actually brought the student back for this teacher. Now we
        // reactivate it.
        var membership = await _db.StudentGroupMemberships.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.StudentId == student.Id && m.Group!.TeacherId == group.TeacherId);
        if (membership == null)
        {
            _db.StudentGroupMemberships.Add(new StudentGroupMembership { StudentId = student.Id, GroupId = group.Id });
            await _db.SaveChangesAsync();
        }
        else if (membership.IsCancelled || membership.IsSuspended)
        {
            membership.GroupId = group.Id;
            membership.IsCancelled = false;
            membership.IsSuspended = false;
            _db.StateHistoryEntries.Add(new StateHistoryEntry { StudentId = student.Id, Action = "Reactivated", Reason = "Re-linked by teacher" });
            await _db.SaveChangesAsync();
        }

        foreach (var unitId in request.UnitIds ?? new())
        {
            if (!await _db.StudentUnitSubscriptions
                    .AnyAsync(s => s.StudentId == student.Id && s.UnitId == unitId && s.TeacherId == group.TeacherId))
                _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { TeacherId = group.TeacherId, StudentId = student.Id, UnitId = unitId });
        }
        await _db.SaveChangesAsync();
        await InvalidateStudentsCacheAsync();

        // NOTE: the student's CURRENT access token won't include this new
        // membership until they log in again or hit /Auth/refresh (same
        // snapshot tradeoff already accepted for groupId/unitIds).
        return Ok(new { message = "Student linked to your account.", studentId = student.Id, studentName = student.Name });
    }

    // POST Students/import  multipart/form-data: { file, groupId }
    // Bulk-registers students from an uploaded Excel sheet (.xlsx). Expected
    // columns in the first sheet, header row first (order doesn't matter,
    // matched by name, case-insensitive, and Arabic spelling variants —
    // see NormalizeHeader):
    //   Name | PhoneNumber | ParentPhoneNumber (optional) | UserName (optional) | Password (optional) | GroupId (optional) | GroupName (optional)
    // The `groupId` form field is the DEFAULT group used for any row that
    // doesn't specify its own GroupId/GroupName column — this keeps the
    // simple single-group sheet (no Group columns at all) working exactly as
    // before. If a row DOES have a GroupId or GroupName value, that group is
    // used instead, so one sheet can register students into several
    // different groups (e.g. different school-year classes) in one upload.
    // GroupName lookups only ever match groups owned by the calling
    // teacher's tenant (same as every other Groups query in this app), so a
    // typo'd/foreign group name just fails that row rather than leaking or
    // touching another teacher's group.
    // Every row runs through the EXACT same dedupe-by-phone logic as the
    // single-student Create endpoint above: if the phone number already
    // belongs to a student (possibly under a different teacher), that
    // existing account is linked into the row's group instead of creating a
    // duplicate. Rows are processed independently — one bad row (missing
    // phone, unknown group, suspended existing account, etc.) doesn't abort
    // the rest of the sheet; each row gets its own status back so the
    // teacher can see and fix just the rows that failed.
    [HttpPost("import")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    [RequestSizeLimit(10_000_000)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> ImportFromExcel([FromForm] ImportFromExcelForm form)
    {
        var file = form.File;
        if (file == null || file.Length == 0)
            return BadRequest(new { message = "No file uploaded." });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext != ".xlsx")
            return BadRequest(new { message = "Only .xlsx files are supported." });

        using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        stream.Position = 0;
        return await ImportStudentsFromWorkbookStream(stream, form.GroupId);
    }

    // POST Students/import/google-sheet  body: { url, groupId }
    // Same bulk-import as Students/import above, but the source is a Google
    // Sheet instead of an uploaded file. `url` can be the normal
    // "https://docs.google.com/spreadsheets/d/{id}/edit#gid=..." link the
    // teacher copies straight out of their browser — the spreadsheet id is
    // extracted from it automatically. If the teacher used a Google Form,
    // this must be the link to the FORM'S RESPONSES SHEET (Responses tab ->
    // create/view spreadsheet), not the form's own /viewform link. The same
    // columns, GroupId/GroupName per-row override, and phone-number dedupe
    // rules from Students/import apply here unchanged; this endpoint is just
    // a different way to get the rows into the same processing logic.
    // REQUIREMENT: the sheet must be shared as "Anyone with the link can
    // view" (Share -> General access -> Anyone with the link), since this
    // downloads it the same way Google's own "File > Download > .xlsx" export
    // does, without going through OAuth / a Google account login.
    [HttpPost("import/google-sheet")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> ImportFromGoogleSheet([FromBody] ImportFromGoogleSheetRequest request)
    {
        var spreadsheetId = ExtractGoogleSheetId(request.Url);
        if (spreadsheetId == null)
            return BadRequest(new { message = "Couldn't find a spreadsheet id in that URL. Paste the full Google Sheets link." });

        var exportUrl = $"https://docs.google.com/spreadsheets/d/{spreadsheetId}/export?format=xlsx";

        var http = _httpClientFactory.CreateClient("GoogleSheets");
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(exportUrl);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = "Couldn't reach Google Sheets to download the file.", detail = ex.Message });
        }

        if (!response.IsSuccessStatusCode)
        {
            // Google returns a 302->login HTML page (200 after redirect) for
            // private sheets rather than a clean 403/404, so this mostly
            // catches sheets that don't exist / were deleted.
            return BadRequest(new { message = "Couldn't download the sheet. Make sure the link is correct and the sheet is shared as 'Anyone with the link can view'." });
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("text/html"))
            return BadRequest(new { message = "That sheet isn't publicly viewable. In Google Sheets, click Share -> General access -> Anyone with the link, then try again." });

        using var stream = new MemoryStream();
        await response.Content.CopyToAsync(stream);
        stream.Position = 0;
        return await ImportStudentsFromWorkbookStream(stream, request.GroupId);
    }

    // POST Students/import/preview  multipart/form-data: { file }
    // DYNAMIC SHEET MAPPING (step 1/2): reads the uploaded sheet's ACTUAL
    // columns — 1-based index, raw header text, up to 5 sample values per
    // column — plus a best-effort suggested mapping using the exact same
    // header-matching rules as Students/import, WITHOUT importing anything
    // yet. Meant for a UI (e.g. the SuperAdmin panel) that shows the sheet to
    // a person and lets them confirm or fix which column means what — since
    // a sheet the person is handed may use column order or headers the
    // built-in auto-detection doesn't recognize. Feed the confirmed mapping
    // into Students/import/mapped to actually run the import.
    [HttpPost("import/preview")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    [RequestSizeLimit(10_000_000)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> PreviewImportSheet([FromForm] ImportFromExcelForm form)
    {
        var file = form.File;
        if (file == null || file.Length == 0)
            return BadRequest(new { message = "No file uploaded." });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext != ".xlsx")
            return BadRequest(new { message = "Only .xlsx files are supported." });

        using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        stream.Position = 0;

        try
        {
            using var workbook = new XLWorkbook(stream);
            var ws = workbook.Worksheets.First();
            var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            if (lastCol == 0 || lastRow == 0)
                return BadRequest(new { message = "The sheet appears to be empty." });

            var headerRow = ws.Row(1);
            var columns = new List<ImportColumnPreview>();
            var normalizedHeaders = new Dictionary<string, int>();

            for (var c = 1; c <= lastCol; c++)
            {
                var headerText = headerRow.Cell(c).GetString().Trim();
                var normalized = NormalizeHeader(headerText);
                if (!string.IsNullOrEmpty(normalized) && !normalizedHeaders.ContainsKey(normalized))
                    normalizedHeaders[normalized] = c;

                var sample = new List<string>();
                for (var r = 2; r <= Math.Min(lastRow, 6); r++)
                {
                    var v = ws.Row(r).Cell(c).GetString().Trim();
                    if (!string.IsNullOrEmpty(v)) sample.Add(v);
                }

                columns.Add(new ImportColumnPreview(c, headerText, sample));
            }

            int GetCol(params string[] names) =>
                names.Select(NormalizeHeader)
                     .Select(n => normalizedHeaders.TryGetValue(n, out var c) ? c : 0)
                     .FirstOrDefault(c => c != 0);

            var suggested = new Dictionary<string, int>();
            void Suggest(string field, params string[] names)
            {
                var c = GetCol(names);
                if (c != 0) suggested[field] = c;
            }
            Suggest("Name", "Name", "StudentName", "الاسم", "اسم", "أسم", "إسم", "اسم الطالب", "الطالب");
            Suggest("PhoneNumber", "PhoneNumber", "Phone", "رقم الهاتف", "رقم التليفون", "رقم الموبايل",
                "الهاتف", "التليفون", "الموبايل", "رقم الطالب", "موبايل الطالب", "هاتف الطالب");
            Suggest("ParentPhoneNumber", "ParentPhoneNumber", "ParentPhone", "رقم ولي الأمر", "رقم ولى الامر",
                "هاتف ولي الأمر", "موبايل ولي الأمر", "تليفون ولي الأمر", "رقم الوالد", "رقم الأب");
            Suggest("UserName", "UserName", "Username", "اسم المستخدم", "أسم المستخدم", "يوزر");
            Suggest("Password", "Password", "كلمة المرور", "كلمة السر", "الرقم السري", "باسورد");
            Suggest("GroupId", "GroupId", "معرف المجموعة", "رقم المجموعة");
            Suggest("GroupName", "GroupName", "Group", "المجموعة", "الجروب", "اسم المجموعة", "أسم المجموعة", "الفصل", "السنتر");

            return Ok(new ImportPreviewResponse(
                columns,
                suggested,
                new[] { "Name", "PhoneNumber", "ParentPhoneNumber", "UserName", "Password", "GroupId", "GroupName" }));
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = "Could not read the Excel file. Make sure it's a valid .xlsx sheet.", detail = ex.Message });
        }
    }

    // POST Students/import/mapped  multipart/form-data: { file, groupId?, mapping, hasHeaderRow? }
    // DYNAMIC SHEET MAPPING (step 2/2): identical import logic to
    // Students/import, except column meaning comes entirely from `mapping` —
    // a JSON object of fieldName -> 1-based column number (e.g.
    // {"Name":2,"PhoneNumber":1}) — instead of matching header text. This
    // makes the backend fully dynamic with respect to the sheet: it adapts to
    // whatever column order/labels the sheet actually has (even the exact
    // opposite of what's normally expected), because the caller is telling it
    // explicitly what each column is rather than asking it to guess. Set
    // hasHeaderRow=false if row 1 is real data (no header row at all) — the
    // mapping still applies either way. See Students/import/preview to get
    // the sheet's columns/suggested mapping first.
    [HttpPost("import/mapped")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    [RequestSizeLimit(10_000_000)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> ImportFromExcelMapped([FromForm] ImportMappedForm form)
    {
        var file = form.File;
        if (file == null || file.Length == 0)
            return BadRequest(new { message = "No file uploaded." });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext != ".xlsx")
            return BadRequest(new { message = "Only .xlsx files are supported." });

        Dictionary<string, int> mapping;
        try
        {
            mapping = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(form.Mapping) ?? new();
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = "mapping must be a JSON object of fieldName -> columnNumber, e.g. {\"Name\":2,\"PhoneNumber\":1}.", detail = ex.Message });
        }

        if (mapping.Count == 0)
            return BadRequest(new { message = "mapping is required — call Students/import/preview first to see the sheet's actual columns." });

        using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        stream.Position = 0;
        return await ImportStudentsFromWorkbookStream(stream, form.GroupId, mapping, form.HasHeaderRow ?? true);
    }

    // Shared core for Students/import and Students/import/google-sheet: both
    // endpoints just get an .xlsx byte stream by different means and hand it
    // here. Everything else — column mapping, per-row GroupId/GroupName
    // resolution, and the phone-number dedupe — is identical either way.
    //
    // DYNAMIC MAPPING: `explicitColumnMap` (field name -> 1-based column
    // number) lets a caller (see Students/import/mapped) tell the backend
    // exactly what each column means, instead of relying on header-text
    // auto-detection. Used when the SuperAdmin (or teacher) previews a sheet
    // whose columns are in an unexpected order or use headers the built-in
    // Arabic/English synonym list doesn't recognize — the backend adapts to
    // WHATEVER layout the sheet actually has, rather than requiring the sheet
    // to match a fixed expected layout. When null (the normal case), header
    // auto-detection runs exactly as before.
    private async Task<IActionResult> ImportStudentsFromWorkbookStream(
        Stream xlsxStream, int? groupId,
        Dictionary<string, int>? explicitColumnMap = null, bool hasHeaderRow = true)
    {
        // Tenant-scoped by the standard Groups query filter — a teacher can
        // only ever resolve their own groups here, by id or by name.
        var myGroups = await _db.Groups.ToListAsync();
        if (myGroups.Count == 0)
            return BadRequest(new { message = "You have no groups yet. Create a group first." });

        Group? defaultGroup = groupId.HasValue ? myGroups.FirstOrDefault(g => g.Id == groupId.Value) : null;
        if (groupId.HasValue && defaultGroup == null)
            return BadRequest(new { message = "Group not found." });

        var groupsByName = myGroups
            .GroupBy(g => NormalizeHeader(g.Name))
            .ToDictionary(g => g.Key, g => g.First());

        try
        {
            using var workbook = new XLWorkbook(xlsxStream);
            var ws = workbook.Worksheets.First();

            var results = new List<ImportStudentsRowResult>();
            int created = 0, linked = 0, failed = 0;

            int nameCol, phoneCol, parentPhoneCol, userNameCol, passwordCol, groupIdCol, groupNameCol;

            if (explicitColumnMap != null)
            {
                // DYNAMIC MAPPING: columns are whatever the caller says they are —
                // no header text is inspected at all, so a reversed/relabeled/
                // language-mismatched sheet still imports correctly as long as the
                // mapping itself is right.
                int Col(string field) => explicitColumnMap.TryGetValue(field, out var c) ? c : 0;
                nameCol = Col("Name");
                phoneCol = Col("PhoneNumber");
                parentPhoneCol = Col("ParentPhoneNumber");
                userNameCol = Col("UserName");
                passwordCol = Col("Password");
                groupIdCol = Col("GroupId");
                groupNameCol = Col("GroupName");
            }
            else
            {
                // Map header name -> column index from row 1. Matching is done on
                // a NORMALIZED form of the header (see NormalizeHeader) so that
                // Arabic spelling variants of the same word — أسم / اسم / الاسم,
                // إيميل / ايميل, رقم الهاتف / رقم الموبايل / تليفون — and
                // stray spaces/diacritics all resolve to the same column, instead
                // of requiring the teacher's sheet to use one exact spelling.
                var headerRow = ws.Row(1);
                var columns = new Dictionary<string, int>();
                foreach (var cell in headerRow.CellsUsed())
                {
                    var header = NormalizeHeader(cell.GetString());
                    if (!string.IsNullOrEmpty(header) && !columns.ContainsKey(header))
                        columns[header] = cell.Address.ColumnNumber;
                }

                int GetCol(params string[] names) =>
                    names.Select(NormalizeHeader)
                         .Select(n => columns.TryGetValue(n, out var c) ? c : 0)
                         .FirstOrDefault(c => c != 0);

                nameCol = GetCol("Name", "StudentName", "الاسم", "اسم", "أسم", "إسم", "اسم الطالب", "الطالب");
                phoneCol = GetCol("PhoneNumber", "Phone", "رقم الهاتف", "رقم التليفون", "رقم الموبايل",
                    "الهاتف", "التليفون", "الموبايل", "رقم الطالب", "موبايل الطالب", "هاتف الطالب");
                parentPhoneCol = GetCol("ParentPhoneNumber", "ParentPhone", "رقم ولي الأمر", "رقم ولى الامر",
                    "هاتف ولي الأمر", "موبايل ولي الأمر", "تليفون ولي الأمر", "رقم الوالد", "رقم الأب");
                userNameCol = GetCol("UserName", "Username", "اسم المستخدم", "أسم المستخدم", "يوزر");
                passwordCol = GetCol("Password", "كلمة المرور", "كلمة السر", "الرقم السري", "باسورد");
                groupIdCol = GetCol("GroupId", "معرف المجموعة", "رقم المجموعة");
                groupNameCol = GetCol("GroupName", "Group", "المجموعة", "الجروب", "اسم المجموعة", "أسم المجموعة", "الفصل", "السنتر");
            }

            if (nameCol == 0 || phoneCol == 0)
                return BadRequest(new { message = explicitColumnMap != null
                    ? "The mapping must assign a column to both 'Name' and 'PhoneNumber'."
                    : "The sheet must have 'Name' and 'PhoneNumber' columns in the header row." });
            if (defaultGroup == null && groupIdCol == 0 && groupNameCol == 0)
                return BadRequest(new { message = "Provide a default groupId, or add a GroupId/GroupName column to the sheet." });

            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
            var firstDataRow = hasHeaderRow ? 2 : 1;
            for (var r = firstDataRow; r <= lastRow; r++)
            {
                var row = ws.Row(r);
                if (row.IsEmpty()) continue;

                var name = row.Cell(nameCol).GetString().Trim();
                var phone = row.Cell(phoneCol).GetString().Trim();
                var parentPhone = parentPhoneCol != 0 ? row.Cell(parentPhoneCol).GetString().Trim() : null;
                var userName = userNameCol != 0 ? row.Cell(userNameCol).GetString().Trim() : null;
                var password = passwordCol != 0 ? row.Cell(passwordCol).GetString().Trim() : null;
                var rowGroupIdRaw = groupIdCol != 0 ? row.Cell(groupIdCol).GetString().Trim() : null;
                var rowGroupName = groupNameCol != 0 ? row.Cell(groupNameCol).GetString().Trim() : null;

                if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(phone)) continue;

                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(phone))
                {
                    failed++;
                    results.Add(new ImportStudentsRowResult(r, name, phone, "Failed", "Name and PhoneNumber are both required.", null));
                    continue;
                }

                // Resolve this row's group: explicit GroupId column wins, then
                // GroupName column, then the sheet-wide default groupId.
                Group? group = defaultGroup;
                if (!string.IsNullOrEmpty(rowGroupIdRaw))
                {
                    if (!int.TryParse(rowGroupIdRaw, out var rowGroupId) ||
                        (group = myGroups.FirstOrDefault(g => g.Id == rowGroupId)) == null)
                    {
                        failed++;
                        results.Add(new ImportStudentsRowResult(r, name, phone, "Failed", $"GroupId '{rowGroupIdRaw}' was not found among your groups.", null));
                        continue;
                    }
                }
                else if (!string.IsNullOrEmpty(rowGroupName))
                {
                    var (resolvedGroup, error) = ResolveGroupByName(rowGroupName, myGroups, groupsByName);
                    if (resolvedGroup == null)
                    {
                        failed++;
                        results.Add(new ImportStudentsRowResult(r, name, phone, "Failed", error!, null));
                        continue;
                    }
                    group = resolvedGroup;
                }

                if (group == null)
                {
                    failed++;
                    results.Add(new ImportStudentsRowResult(r, name, phone, "Failed", "No group specified for this row and no default groupId was provided.", null));
                    continue;
                }

                // Same cross-tenant lookup-by-phone dedupe as Create/link above.
                var existing = await _db.Students.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(s => s.PhoneNumber == phone);

                if (existing != null)
                {
                    // RE-ADD FIX: same reasoning as Students/Create -- reactivate a
                    // cancelled/suspended membership instead of treating it as a
                    // no-op "already linked" row, otherwise a re-imported student
                    // never actually comes back for this teacher.
                    var membership = await _db.StudentGroupMemberships.IgnoreQueryFilters()
                        .FirstOrDefaultAsync(m => m.StudentId == existing.Id && m.Group!.TeacherId == group.TeacherId);
                    if (membership == null)
                    {
                        _db.StudentGroupMemberships.Add(new StudentGroupMembership { StudentId = existing.Id, GroupId = group.Id });
                    }
                    else if (membership.IsCancelled || membership.IsSuspended)
                    {
                        membership.GroupId = group.Id;
                        membership.IsCancelled = false;
                        membership.IsSuspended = false;
                        _db.StateHistoryEntries.Add(new StateHistoryEntry { StudentId = existing.Id, Action = "Reactivated", Reason = "Re-added via import" });
                    }

                    await _db.SaveChangesAsync();
                    linked++;
                    results.Add(new ImportStudentsRowResult(r, name, phone, "Linked", $"A student with this phone number already existed, so they were linked to group '{group.Name}' instead of creating a duplicate.", existing.Id));
                    continue;
                }

                var plainPassword = string.IsNullOrEmpty(password) ? GenerateTempPassword() : password;
                var student = new Student
                {
                    Name = name,
                    PhoneNumber = phone,
                    ParentPhoneNumber = string.IsNullOrEmpty(parentPhone) ? null : parentPhone,
                    UserName = string.IsNullOrEmpty(userName) ? null : userName,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(plainPassword),
                    GroupId = group.Id,
                    SchoolYear = group.SchoolYear
                };
                _db.Students.Add(student);
                await _db.SaveChangesAsync();

                _db.StudentGroupMemberships.Add(new StudentGroupMembership { StudentId = student.Id, GroupId = group.Id });
                await _db.SaveChangesAsync();

                await SendWelcomeWhatsAppAsync(student, plainPassword);

                created++;
                results.Add(new ImportStudentsRowResult(r, name, phone, "Created", $"Student created in group '{group.Name}'.", student.Id));
            }

            if (created > 0 || linked > 0)
                await InvalidateStudentsCacheAsync();

            return Ok(new ImportStudentsResponse(results.Count, created, linked, failed, results));
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = "Could not read the Excel file. Make sure it's a valid .xlsx sheet.", detail = ex.Message });
        }
    }

    // Resolves a GroupName cell value against the teacher's groups:
    //  1. Exact match (after NormalizeHeader) — e.g. "الصفوة ثلاثاء" == "الصفوة ثلاثاء".
    //  2. Otherwise, PARTIAL match: if the sheet's text is a substring of
    //     exactly one group's name (or vice versa) after normalization —
    //     e.g. sheet says "الصفوة" and the teacher only has one group whose
    //     name contains that, "الصفوة ثلاثاء" — that one group is used.
    //  3. If the partial match is AMBIGUOUS (matches 2+ groups, e.g. teacher
    //     has both "الصفوة ثلاثاء" and "الصفوة خميس"), this fails the row
    //     rather than guessing, and lists the candidates so the teacher can
    //     make the sheet's value more specific.
    private static (Group? group, string? error) ResolveGroupByName(
        string rowGroupName, List<Group> myGroups, Dictionary<string, Group> groupsByName)
    {
        var normalized = NormalizeHeader(rowGroupName);

        if (groupsByName.TryGetValue(normalized, out var exact))
            return (exact, null);

        var candidates = myGroups
            .Where(g =>
            {
                var gn = NormalizeHeader(g.Name);
                return gn.Contains(normalized) || normalized.Contains(gn);
            })
            .ToList();

        if (candidates.Count == 1)
            return (candidates[0], null);

        if (candidates.Count > 1)
        {
            var names = string.Join(", ", candidates.Select(g => g.Name));
            return (null, $"GroupName '{rowGroupName}' matches more than one of your groups ({names}) — use the exact group name or GroupId to disambiguate.");
        }

        return (null, $"GroupName '{rowGroupName}' was not found among your groups.");
    }

    // Normalizes a column header or group name so different spellings of the
    // same word match the same column, covering the variation that's common
    // in real teacher-written sheets:
    //  - trims/collapses whitespace and drops diacritics (tashkeel/tatweel)
    //  - unifies hamza forms: أ/إ/آ/ٱ -> ا  (so أسم / إسم / اسم all match)
    //  - unifies ة -> ه and ى -> ي  (trailing-letter spelling variants)
    //  - strips a leading "ال" (so "الاسم" matches "اسم")
    //  - lowercases Latin text and strips spaces (so "Phone Number" ==
    //    "phonenumber" == "PHONE_NUMBER" once underscores are stripped too)
    private static string NormalizeHeader(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var text = s.Trim();

        // Strip Arabic diacritics (tashkeel) and tatweel.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[\u064B-\u065F\u0670\u0640]", "");

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case 'أ': case 'إ': case 'آ': case 'ٱ': sb.Append('ا'); break;
                case 'ة': sb.Append('ه'); break;
                case 'ى': sb.Append('ي'); break;
                case ' ': case '_': case '-': case '/': case '\t': break; // drop separators entirely
                default:
                    sb.Append(char.ToLowerInvariant(ch));
                    break;
            }
        }

        var normalized = sb.ToString();
        if (normalized.StartsWith("ال") && normalized.Length > 2)
            normalized = normalized[2..];

        return normalized;
    }

    // Pulls the spreadsheet id out of any of the URL shapes Google Sheets
    // hands out (full edit link, /d/{id}/... with or without a trailing
    // segment, or a bare id pasted on its own).
    private static string? ExtractGoogleSheetId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(url, @"/d/([a-zA-Z0-9_-]{20,})");
        if (match.Success) return match.Groups[1].Value;

        // Fall back to treating the whole input as a bare id if it looks like one.
        return System.Text.RegularExpressions.Regex.IsMatch(url.Trim(), @"^[a-zA-Z0-9_-]{20,}$")
            ? url.Trim()
            : null;
    }

    // GET Students?schoolYear=..&groupId=..&p=..&q=..
    [HttpGet]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetAll(
        [FromQuery] int? schoolYear, [FromQuery] int? groupId,
        [FromQuery] int p = 1, [FromQuery] string? q = null)
        => await ListStudents(StudentStatusFilter.Active, schoolYear, groupId, p, q);

    // GET Students/suspended?schoolYear=..&p=..&q=..
    [HttpGet("suspended")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetSuspended(
        [FromQuery] int? schoolYear, [FromQuery] int p = 1, [FromQuery] string? q = null)
        => await ListStudents(StudentStatusFilter.Suspended, schoolYear, null, p, q);

    // GET Students/cancelled?schoolYear=..&p=..&q=..
    [HttpGet("cancelled")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetCancelled(
        [FromQuery] int? schoolYear, [FromQuery] int p = 1, [FromQuery] string? q = null)
        => await ListStudents(StudentStatusFilter.Cancelled, schoolYear, null, p, q);

    private enum StudentStatusFilter { Active, Suspended, Cancelled }

    // PER-TENANT FIX: statusFilter now runs against StudentGroupMembership
    // (IsSuspended/IsCancelled per teacher-relationship), not against a global
    // flag on Student -- StudentGroupMemberships is already tenant-scoped via
    // its own query filter (Group.TeacherId == current tenant), so ".Any(...)"
    // here only ever looks at THIS teacher's relationship with the student,
    // never a status set by some other teacher.
    private async Task<IActionResult> ListStudents(
        StudentStatusFilter statusFilter,
        int? schoolYear, int? groupId, int p, string? q, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        var tenantId = _tenant.CurrentTenantId;
        var cacheKey = $"{StudentsCachePrefix(tenantId)}{caller}:{schoolYear}:{groupId}:{p}:{q}";

        var result = await _cache.GetOrCreateAsync(cacheKey, TimeSpan.FromSeconds(60), async () =>
        {
            // MULTI-TENANT: filter/display by the Group under the CURRENT tenant, not
            // necessarily the student's legacy single Group (which may belong to a
            // different teacher if the student is also linked elsewhere).
            IQueryable<Student> query = statusFilter switch
            {
                StudentStatusFilter.Suspended => _db.Students.AsNoTracking().Where(s => s.GroupMemberships.Any(m => m.IsSuspended)),
                StudentStatusFilter.Cancelled => _db.Students.AsNoTracking().Where(s => s.GroupMemberships.Any(m => m.IsCancelled)),
                _ => _db.Students.AsNoTracking().Where(s => s.GroupMemberships.Any(m => !m.IsSuspended && !m.IsCancelled)),
            };
            if (groupId.HasValue)
                query = query.Where(s => s.GroupMemberships.Any(m => m.GroupId == groupId.Value));
            else if (schoolYear.HasValue)
                // PER-TENANT FIX: filter on THIS tenant's Group.SchoolYear via the
                // membership row, not the legacy Student.SchoolYear field. That field
                // is stamped once from whichever teacher created the Student FIRST and
                // is never updated when a second teacher later links the same existing
                // student to their own group (Students/Create's "existing" branch,
                // Students/link) -- so a student added for the first time under
                // teacher B can fail this filter even when teacher B's own group is
                // "the same year", if the legacy value differs. The membership row
                // itself was correct all along (counts driven off
                // StudentGroupMemberships already reflected it), only this filter was
                // reading the wrong field.
                query = query.Where(s => s.GroupMemberships.Any(m => m.Group!.SchoolYear == schoolYear.Value));
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(s => s.Name.Contains(q) || s.PhoneNumber.Contains(q));

            var students = await query
                .OrderBy(s => s.Name)
                .Skip((p - 1) * PagingDefaults.PageSize)
                .Take(PagingDefaults.PageSize)
                .Select(s => new { s.Id, s.Name, s.PhoneNumber, s.UserName, s.GroupId })
                .ToListAsync();

            var studentIds = students.Select(s => s.Id).ToList();
            var tenantGroupNames = await _db.GetTenantGroupNamesAsync(studentIds);
            // A student has at most one membership per tenant (enforced in code --
            // see StudentGroupMembership docs), so this is a safe 1:1 lookup.
            var tenantMemberships = await _db.StudentGroupMemberships.AsNoTracking()
                .Where(m => studentIds.Contains(m.StudentId))
                .Select(m => new { m.StudentId, m.IsSuspended, m.IsCancelled })
                .ToDictionaryAsync(m => m.StudentId);

            return students.Select(s =>
            {
                var groupName = tenantGroupNames.GetValueOrDefault(s.Id);
                tenantMemberships.TryGetValue(s.Id, out var membership);
                var isSuspended = membership?.IsSuspended ?? false;
                var isCancelled = membership?.IsCancelled ?? false;
                var status = isCancelled ? "Cancelled" : isSuspended ? "Suspended" : "Active";
                return new StudentListItem(
                    s.Id, s.Name, s.PhoneNumber, s.UserName, s.GroupId, groupName, isSuspended, isCancelled,
                    status, null);
            }).ToList();
        });

        return Ok(result);
    }

    // CACHING: prefix shared by every cached Students list for this tenant
    // (Active/Suspended/Cancelled, any schoolYear/groupId/page/search combo)
    // — RemoveByPrefixAsync(this) wipes all of them at once after a write.
    private static string StudentsCachePrefix(int? tenantId) => $"tenant:{tenantId}:students:list:";

    private Task InvalidateStudentsCacheAsync() => _cache.RemoveByPrefixAsync(StudentsCachePrefix(_tenant.CurrentTenantId));

    // GET Students/count
    [HttpGet("count")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Count()
        // BUGFIX: this used to go through _db.Students.CountAsync(s =>
        // s.GroupMemberships.Any(...)), which always returned 0 -- combining
        // Student's own tenant query filter with StudentGroupMembership's
        // tenant query filter on the SAME navigation, inside one more
        // explicit .Any() predicate, didn't translate/combine correctly.
        // Querying StudentGroupMemberships directly (already tenant-scoped
        // via its own HasQueryFilter) sidesteps that entirely -- same
        // approach already used successfully in
        // TeachersController.SuperAdminStudentCounts.
        => Ok(await _db.StudentGroupMemberships.AsNoTracking()
            .Where(m => !m.IsCancelled)
            .Select(m => m.StudentId)
            .Distinct()
            .CountAsync());

    // GET Students/{id}
    [HttpGet("{id:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetById(int id)
    {
        var student = await _db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        if (student == null) return NotFound(new { message = "Student not found." });

        var subs = await _db.StudentUnitSubscriptions.AsNoTracking().Where(u => u.StudentId == id).Select(u => u.UnitId).ToListAsync();

        // MULTI-TENANT: this student's Group/GroupId under the CALLER's own
        // tenant specifically, not necessarily their legacy single Group.
        // isSuspended/isCancelled also come from THIS membership row -- see
        // StudentGroupMembership -- so a status set by a different teacher
        // never leaks into this teacher's view of the student.
        var tenantMembership = await _db.StudentGroupMemberships.AsNoTracking()
            .Where(m => m.StudentId == id)
            .Select(m => new { m.GroupId, GroupName = m.Group!.Name, GroupSchoolYear = m.Group!.SchoolYear, m.IsSuspended, m.IsCancelled })
            .FirstOrDefaultAsync();

        return Ok(new
        {
            id = student.Id,
            name = student.Name,
            phoneNumber = student.PhoneNumber,
            parentPhoneNumber = student.ParentPhoneNumber,
            // NOTE: intentionally lowercase "username" (not "userName") — the
            // Flutter profile page reads _student!["username"] exactly.
            username = student.UserName,
            groupId = tenantMembership?.GroupId ?? student.GroupId,
            groupName = tenantMembership?.GroupName,
            // PER-TENANT FIX: same issue as the GetAll schoolYear filter -- the
            // legacy student.SchoolYear field is stamped once by whichever teacher
            // created the Student FIRST and never updated for a second teacher's
            // own group/year. Report THIS tenant's group SchoolYear instead.
            schoolYear = tenantMembership?.GroupSchoolYear ?? student.SchoolYear,
            isSuspended = tenantMembership?.IsSuspended ?? false,
            isCancelled = tenantMembership?.IsCancelled ?? false,
            createdAt = student.CreatedAt,
            // Client reads this as `data['subscribedUnits'] as List` then `unit['id']` per item.
            subscribedUnits = subs.Select(unitId => new { id = unitId })
        });
    }

    // GET Students/{id}/state-history
    [HttpGet("{id:int}/state-history")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetStateHistory(int id)
    {
        var history = await _db.StateHistoryEntries.AsNoTracking().Where(h => h.StudentId == id)
            .OrderByDescending(h => h.Date)
            .Select(h => new StateHistoryItem(h.Action, null, h.Reason, h.Date))
            .ToListAsync();

        return Ok(history);
    }

    // PUT Students/{id}/group  body: { groupId }
    [HttpPut("{id:int}/group")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangeGroup(int id, [FromBody] ChangeGroupRequest request)
    {
        // `_db.Students`/`_db.Groups` are both tenant-filtered, so this already
        // only matches a student/group that belong to the CALLER's own tenant —
        // safe even now that a student can have groups under several tenants.
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (id));
        if (student == null) return NotFound(new { message = "Student not found." });

        var group = await _db.Groups.FirstOrDefaultAsync(e => e.Id == (request.GroupId));
        if (group == null) return BadRequest(new { message = "Group not found." });

        // MULTI-TENANT: only touch the legacy single GroupId if it currently
        // points at THIS tenant (or is unset) — never let one teacher's group
        // change stomp on the student's group under a different teacher.
        if (student.GroupId == 0 || _tenant.CurrentTenantId == group.TeacherId && !await _db.Groups.IgnoreQueryFilters()
                .AnyAsync(g => g.Id == student.GroupId && g.TeacherId != _tenant.CurrentTenantId))
        {
            student.GroupId = request.GroupId;
        }

        var membership = await _db.StudentGroupMemberships.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.StudentId == id && m.Group!.TeacherId == group.TeacherId);
        if (membership == null)
            _db.StudentGroupMemberships.Add(new StudentGroupMembership { StudentId = id, GroupId = request.GroupId });
        else
            membership.GroupId = request.GroupId;

        await _db.SaveChangesAsync();
        await InvalidateStudentsCacheAsync();
        return Ok(new { message = "Group updated." });
    }

    // POST Students/change/password?studentId=..  -> generates + returns a new password
    [HttpPost("change/password")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ResetPassword([FromQuery] int studentId)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (studentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        var newPassword = GenerateTempPassword();
        student.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        await _db.SaveChangesAsync();

        return Ok(new { newPassword });
    }

    // POST Students/change/phone-number?studentId=..&newNumber=..
    [HttpPost("change/phone-number")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangePhoneNumber([FromQuery] int studentId, [FromQuery] string newNumber)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (studentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        student.PhoneNumber = newNumber;
        await _db.SaveChangesAsync();
        await InvalidateStudentsCacheAsync();
        return Ok(new { message = "Phone number updated." });
    }

    // POST Students/change/parent-phone-number?studentId=..&newParentPhoneNumber=..
    [HttpPost("change/parent-phone-number")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangeParentPhoneNumber([FromQuery] int studentId, [FromQuery] string newParentPhoneNumber)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (studentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        student.ParentPhoneNumber = newParentPhoneNumber;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Parent phone number updated." });
    }

    // POST Students/change/name?studentId=..&newName=..
    [HttpPost("change/name")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangeName([FromQuery] int studentId, [FromQuery] string newName)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (studentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        student.Name = newName;
        await _db.SaveChangesAsync();
        await InvalidateStudentsCacheAsync();
        return Ok(new { message = "Name updated." });
    }

    // POST Students/suspend  body: { studentId, comment }
    [HttpPost("suspend")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Suspend([FromBody] SuspendRequest request)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (request.StudentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        // PER-TENANT FIX: suspend THIS teacher's membership row only -- already
        // tenant-scoped via StudentGroupMembership's query filter (Group.TeacherId
        // == current tenant), so this can never touch another teacher's relationship
        // with the same student.
        var membership = await _db.StudentGroupMemberships.FirstOrDefaultAsync(m => m.StudentId == request.StudentId);
        if (membership == null) return NotFound(new { message = "This student isn't linked to one of your groups." });

        membership.IsSuspended = true;
        _db.StateHistoryEntries.Add(new StateHistoryEntry { StudentId = student.Id, Action = "Suspended", Reason = request.Comment });
        await _db.SaveChangesAsync();
        await InvalidateStudentsCacheAsync();
        return Ok(new { message = "Student suspended." });
    }

    // POST Students/reactivate  body: { studentId, comment }
    [HttpPost("reactivate")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Reactivate([FromBody] ReactivateRequest request)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (request.StudentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        var membership = await _db.StudentGroupMemberships.FirstOrDefaultAsync(m => m.StudentId == request.StudentId);
        if (membership == null) return NotFound(new { message = "This student isn't linked to one of your groups." });

        membership.IsSuspended = false;
        membership.IsCancelled = false;
        _db.StateHistoryEntries.Add(new StateHistoryEntry { StudentId = student.Id, Action = "Reactivated", Reason = request.Comment });
        await _db.SaveChangesAsync();
        await InvalidateStudentsCacheAsync();
        return Ok(new { message = "Student reactivated." });
    }

    // POST Students/delete?studentId=..
    // NOTE: was a hard `_db.Students.Remove(...)` before — that meant a
    // "deleted" student vanished from the DB entirely and could never show
    // up in Students/cancelled nor be brought back via Students/reactivate
    // (that endpoint just flips flags on a row that no longer existed).
    // Now this is a SOFT delete: same IsCancelled flag the rest of the app
    // (GetCancelled / Reactivate) already expects, so a "cancelled" student
    // behaves exactly like a suspended one — listed under Students/cancelled
    // and restorable with Students/reactivate. PER-TENANT: only cancels THIS
    // teacher's own membership -- the student stays completely normal for any
    // other teacher they're linked to.
    [HttpPost("delete")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Delete([FromQuery] int studentId)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (studentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        var membership = await _db.StudentGroupMemberships.FirstOrDefaultAsync(m => m.StudentId == studentId);
        if (membership == null) return NotFound(new { message = "This student isn't linked to one of your groups." });

        membership.IsCancelled = true;
        _db.StateHistoryEntries.Add(new StateHistoryEntry { StudentId = student.Id, Action = "Cancelled", Reason = null });
        await _db.SaveChangesAsync();
        await InvalidateStudentsCacheAsync();
        return Ok(new { message = "Student deleted." });
    }

    // POST Students/codes  body: { code }  (student redeems a code)
    [HttpPost("codes")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> RedeemCode([FromBody] RedeemCodeRequest request)
    {
        var code = await _db.Codes.FirstOrDefaultAsync(c => c.Value == request.Code);
        if (code == null || code.IsTemplate) return NotFound(new { message = "Invalid code." });
        if (code.IsUsed) return Conflict(new { message = "Code already used." });

        var studentId = User.GetUserId();
        code.IsUsed = true;
        code.UsedByStudentId = studentId;
        code.UsedAt = DateTime.UtcNow;

        foreach (var unitId in code.UnitIds)
        {
            if (!await _db.StudentUnitSubscriptions.AnyAsync(s => s.StudentId == studentId && s.UnitId == unitId))
                _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { TeacherId = code.TeacherId, StudentId = studentId, UnitId = unitId });
        }

        // BUGFIX: code.LectureIds (standalone/no-unit lectures, e.g. an online
        // lecture created without a Unit) used to be returned in the response
        // but never actually persisted anywhere — the standalone lecture never
        // became tied to this student. Meanwhile LecturesController.ByGroup let
        // EVERY noUnitOnly lecture through untouched for students, no gate at
        // all, so codes for standalone lectures never restricted anything.
        // Now we record the unlock here, and ByGroup gates on it below.
        foreach (var lectureId in code.LectureIds)
        {
            if (!await _db.StudentLectureUnlocks.AnyAsync(u => u.StudentId == studentId && u.LectureId == lectureId))
                _db.StudentLectureUnlocks.Add(new StudentLectureUnlock { TeacherId = code.TeacherId, StudentId = studentId, LectureId = lectureId });
        }

        // Same pattern as UnitIds/LectureIds above: an unlock here grants
        // access to the WHOLE OnlineLesson container (every lecture inside
        // it), gated in OnlineLessonsController.GetOnlineLesson/GetOnlineLessons.
        foreach (var onlineLessonId in code.OnlineLessonIds)
        {
            if (!await _db.StudentOnlineLessonUnlocks.AnyAsync(u => u.StudentId == studentId && u.OnlineLessonId == onlineLessonId))
                _db.StudentOnlineLessonUnlocks.Add(new StudentOnlineLessonUnlock { TeacherId = code.TeacherId, StudentId = studentId, OnlineLessonId = onlineLessonId });
        }

        await _db.SaveChangesAsync();
        return Ok(new {
            message = "Code redeemed successfully.",
            unitIds = code.UnitIds,
            lectureIds = code.LectureIds,
            onlineLessonIds = code.OnlineLessonIds
        });
    }

    // GET Students/codes  (student, own codes — manually redeemed AND
    // auto-issued via attending a code-triggering Center lecture, see
    // AttendanceController.IssueTriggeredCodesAsync). Never returns other
    // students' codes or unissued templates — only rows already assigned to
    // the calling student (UsedByStudentId == them).
    [HttpGet("codes")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> GetStudentCodes()
    {
        var studentId = User.GetUserId();
        var codes = await _db.Codes.AsNoTracking()
            .Where(c => c.UsedByStudentId == studentId)
            .OrderByDescending(c => c.UsedAt)
            .ToListAsync();

        var unitIdsFlat = codes.SelectMany(c => c.UnitIds).Distinct().ToList();
        var lectureIdsFlat = codes.SelectMany(c => c.LectureIds).Distinct().ToList();
        var onlineLessonIdsFlat = codes.SelectMany(c => c.OnlineLessonIds).Distinct().ToList();
        var unitNames = await _db.Units.AsNoTracking().Where(u => unitIdsFlat.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Name);
        var lectureNames = await _db.Lectures.AsNoTracking().Where(l => lectureIdsFlat.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l.Name);
        var onlineLessonNames = await _db.OnlineLessons.AsNoTracking().Where(o => onlineLessonIdsFlat.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.Name);

        var dtos = codes.Select(c => new
        {
            id = c.Id,
            code = c.Value,
            schoolYear = c.SchoolYear,
            units = c.UnitIds.Select(id => new { id, name = unitNames.GetValueOrDefault(id, "") }).ToList(),
            lectures = c.LectureIds.Select(id => new { id, name = lectureNames.GetValueOrDefault(id, "") }).ToList(),
            onlineLessons = c.OnlineLessonIds.Select(id => new { id, name = onlineLessonNames.GetValueOrDefault(id, "") }).ToList(),
            redeemedAt = c.UsedAt?.ToString("O"),
            // true when this code was earned automatically by attendance
            // rather than typed in manually.
            fromAttendance = c.SourceCodeTemplateId != null
        });

        return Ok(dtos);
    }

    // GET Students/attendance?studentId=..&unitId=..  (teacher, for a specific student)
    // GET Students/attendance?unitId=..                (student, own attendance)
    //
    // Response: one row PER LECTURE the student's group (optionally narrowed to a
    // unit) is scheduled for, each with `isAttended: true|false` — NOT just the
    // lectures the student actually attended. The Flutter client reads exactly
    // `lec['lectureName']` and `lec['isAttended'] ?? false` and computes
    // attended/absent counts locally by filtering this array, so an absent
    // lecture still needs to appear here with isAttended == false.
    [HttpGet("attendance")]
    public async Task<IActionResult> GetAttendance([FromQuery] int? studentId, [FromQuery] int? unitId)
    {
        var sid = User.IsInRole(Roles.Student) ? User.GetUserId() : studentId;
        if (sid == null) return BadRequest(new { message = "studentId is required." });

        var student = await _db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid.Value);
        if (student == null) return NotFound(new { message = "Student not found." });

        // Lectures scheduled for this student's group. Attendance-taking only
        // makes sense for Center (on-site) lectures — Online lectures are
        // watched on-demand and were never meant to show up here at all, but
        // this query used to return both, so a student's attendance summary
        // counted Online lectures as "absent" even though nobody ever takes
        // attendance for them.
        // MULTI-TENANT: lectures live under a specific tenant already (_db.Lectures
        // is tenant-filtered), so we need THIS student's group under the CURRENT
        // tenant specifically -- not their legacy single GroupId, which may
        // belong to a different teacher entirely.
        var tenantGroupId = await _db.StudentGroupMemberships.AsNoTracking()
            .Where(m => m.StudentId == sid.Value)
            .Select(m => (int?)m.GroupId)
            .FirstOrDefaultAsync();

        // Only trust the legacy single GroupId as a fallback when it actually
        // belongs to the CURRENT tenant — otherwise a subscription-only student
        // (linked to this teacher via a redeemed code, with no GroupMembership
        // row at all) would silently pull in a completely different teacher's
        // group here, instead of correctly resolving to "no Center lectures for
        // this tenant" (an empty result, not a leak of another tenant's group).
        if (tenantGroupId == null)
            tenantGroupId = await _db.Groups.AsNoTracking()
                .Where(g => g.Id == student.GroupId && g.TeacherId == _tenant.CurrentTenantId)
                .Select(g => (int?)g.Id)
                .FirstOrDefaultAsync();

        var lecturesQuery = _db.Lectures.AsNoTracking()
            .Where(l => _db.LectureGroupLinks.Any(x => x.LectureId == l.Id && x.GroupId == tenantGroupId)
                && l.AttendanceMethod == AttendanceMethod.Center);
        if (unitId.HasValue) lecturesQuery = lecturesQuery.Where(l => l.UnitId == unitId.Value);

        var lectures = await lecturesQuery.OrderBy(l => l.CreatedAt).ToListAsync();

        var attendedLookup = await _db.Attendances.AsNoTracking()
            .Where(a => a.StudentId == sid.Value)
            .ToDictionaryAsync(a => a.LectureId, a => a.Date);

        var result = lectures.Select(l => new AttendanceRecordDto(
            l.Id,
            l.Name,
            attendedLookup.ContainsKey(l.Id),
            attendedLookup.TryGetValue(l.Id, out var date) ? date : null));

        return Ok(result);
    }

    // GET Students/qr  (student) -> returns a quoted base64-encoded PNG QR string
    [HttpGet("qr")]
    [Authorize(Roles = Roles.Student)]
    public IActionResult GetQr()
    {
        var studentId = User.GetUserId();
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(studentId.ToString(), QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(data);
        var bytes = qrCode.GetGraphic(10);
        var base64 = Convert.ToBase64String(bytes);

        // Client does: response.body.trim().replaceAll('"', '') then base64Decode(...)
        return Content($"\"{base64}\"", "application/json", Encoding.UTF8);
    }

    // POST Students/subscribe/unit  body: { studentId, unitId }
    [HttpPost("subscribe/unit")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> SubscribeUnit([FromBody] UnitSubscriptionRequest request)
    {
        // SECURITY FIX: previously this had NO check that the Unit even belongs
        // to the caller's own tenant, nor that the student is actually linked to
        // that teacher — a caller could subscribe ANY student to ANY unit from
        // ANY teacher. Both are now enforced. `_db.Units` is already
        // tenant-filtered, so a unitId from another tenant simply won't be found.
        var unit = await _db.Units.FirstOrDefaultAsync(u => u.Id == request.UnitId);
        if (unit == null) return NotFound(new { message = "Unit not found." });

        var isLinked = await _db.StudentGroupMemberships.IgnoreQueryFilters()
            .AnyAsync(m => m.StudentId == request.StudentId && m.Group!.TeacherId == unit.TeacherId);
        if (!isLinked)
            return BadRequest(new
            {
                message = "This student isn't linked to your account yet. Use POST Students/link first."
            });

        if (!await _db.StudentUnitSubscriptions
                .AnyAsync(s => s.StudentId == request.StudentId && s.UnitId == request.UnitId))
        {
            _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { TeacherId = unit.TeacherId, StudentId = request.StudentId, UnitId = request.UnitId });
            await _db.SaveChangesAsync();
        }
        return Ok(new { message = "Subscribed." });
    }

    // POST Students/unsubscribe/unit  body: { studentId, unitId }
    [HttpPost("unsubscribe/unit")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> UnsubscribeUnit([FromBody] UnitSubscriptionRequest request)
    {
        var sub = await _db.StudentUnitSubscriptions
            .FirstOrDefaultAsync(s => s.StudentId == request.StudentId && s.UnitId == request.UnitId);
        if (sub != null)
        {
            _db.StudentUnitSubscriptions.Remove(sub);
            await _db.SaveChangesAsync();
        }
        return Ok(new { message = "Unsubscribed." });
    }

    // GET Students/{notebookId}/payments?studentId=..
    [HttpGet("{notebookId:int}/payments")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetNotebookPaymentsForStudent(int notebookId, [FromQuery] int studentId)
    {
        // BUGFIX: previously selected straight off NotebookPayments with no
        // lecture info at all, so the Flutter payments screen always fell
        // back to a generic "محاضرة" label with no unit — now that
        // NotebookPayment.LectureId is actually persisted (see PayNotebook
        // below), include the Lecture so each row can report which lecture
        // the payment was actually made for.
        var payments = await _db.NotebookPayments.AsNoTracking()
            .Where(p => p.NotebookId == notebookId && p.StudentId == studentId)
            .Include(p => p.Lecture)
            .ToListAsync();

        // Lecture doesn't have a Unit navigation property (just a raw
        // UnitId), so batch-load the handful of Units these lectures point
        // to in one extra query instead of shaping this in SQL.
        var unitIds = payments.Where(p => p.Lecture?.UnitId != null)
            .Select(p => p.Lecture!.UnitId!.Value).Distinct().ToList();
        var unitsById = await _db.Units.AsNoTracking()
            .Where(u => unitIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Name);

        var result = payments.Select(p => new
        {
            id = p.Id,
            amount = p.Price,
            discountedPrice = p.DiscountedPrice,
            date = p.Date,
            lecture = p.Lecture == null ? null : new
            {
                id = p.Lecture.Id,
                name = p.Lecture.Name,
                unit = p.Lecture.UnitId != null && unitsById.TryGetValue(p.Lecture.UnitId.Value, out var uName)
                    ? new { id = p.Lecture.UnitId.Value, name = uName }
                    : null
            }
        });

        return Ok(result);
    }

    // POST Students/{notebookId}/pay  body: { studentId, amount, lectureId }
    [HttpPost("{notebookId:int}/pay")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> PayNotebook(int notebookId, [FromBody] PayNotebookRequest request)
    {
        var notebook = await _db.Notebooks.FirstOrDefaultAsync(e => e.Id == (notebookId));
        if (notebook == null) return NotFound(new { message = "Notebook not found." });

        // SAFETY: make sure request.StudentId is actually a real student who
        // belongs to a group this notebook targets, under the CURRENT tenant.
        // StudentGroupMemberships already carries a tenant query filter, so
        // this can't match a membership from another teacher. Without this
        // check a stale/incorrect StudentId from the client would silently
        // get recorded as a payment for the wrong student.
        var studentGroupId = await _db.StudentGroupMemberships.AsNoTracking()
            .Where(m => m.StudentId == request.StudentId)
            .Select(m => (int?)m.GroupId)
            .FirstOrDefaultAsync();
        if (studentGroupId == null || !notebook.GroupIds.Contains(studentGroupId.Value))
            return BadRequest(new { message = "This student is not enrolled in a group this notebook applies to." });

        if (request.Amount <= 0)
            return BadRequest(new { message = "Payment amount must be greater than zero." });

        // SAFETY: don't let a student "overpay" past the notebook's price
        // (or their applied discounted price, if any). Cap the accepted
        // amount to whatever is actually still owed.
        var existingPayments = await _db.NotebookPayments.AsNoTracking()
            .Where(p => p.NotebookId == notebookId && p.StudentId == request.StudentId)
            .ToListAsync();
        var alreadyPaid = existingPayments.Sum(p => p.DiscountedPrice ?? p.Price);
        var owedTotal = existingPayments.FirstOrDefault(p => p.DiscountedPrice.HasValue)?.DiscountedPrice
                        ?? notebook.Price;
        var remaining = owedTotal - alreadyPaid;
        if (remaining <= 0)
            return BadRequest(new { message = "This student has already fully paid for this notebook." });
        if (request.Amount > remaining)
            return BadRequest(new { message = $"Amount exceeds what's left to pay ({remaining})." });

        var payment = new NotebookPayment
        {
            TeacherId = notebook.TeacherId,
            NotebookId = notebookId,
            StudentId = request.StudentId,
            Price = request.Amount,
            LectureId = request.LectureId,
        };
        _db.NotebookPayments.Add(payment);
        await _db.SaveChangesAsync();

        return StatusCode(201, new { id = payment.Id, message = "Payment recorded." });
    }

    // GET Students/{notebookId}/discount?studentId=..
    // Returns the notebook's info + this student's payment summary for it.
    // Added alongside the existing POST of the same route (which applies a
    // discount) — the client fetches this first to show notebook details.
    [HttpGet("{notebookId:int}/discount")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetNotebookDiscountInfo(int notebookId, [FromQuery] int studentId)
    {
        var notebook = await _db.Notebooks.AsNoTracking().FirstOrDefaultAsync(e => e.Id == (notebookId));
        if (notebook == null) return NotFound(new { message = "Notebook not found." });

        var payments = await _db.NotebookPayments.AsNoTracking()
            .Where(p => p.NotebookId == notebookId && p.StudentId == studentId)
            .OrderByDescending(p => p.Date)
            .ToListAsync();

        var paid = payments.Sum(p => p.Price);
        var discountedPrice = payments.FirstOrDefault(p => p.DiscountedPrice.HasValue)?.DiscountedPrice
                               ?? notebook.Price;

        return Ok(new
        {
            notebook = new { name = notebook.Name, price = notebook.Price, paid },
            discountedPrice
        });
    }

    // POST Students/{notebookId}/discount?studentId=..&discountedPrice=..
    [HttpPost("{notebookId:int}/discount")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ApplyDiscount(int notebookId, [FromQuery] int studentId, [FromQuery] decimal discountedPrice)
    {
        var notebook = await _db.Notebooks.FirstOrDefaultAsync(e => e.Id == (notebookId));
        if (notebook == null) return NotFound(new { message = "Notebook not found." });

        // SAFETY: same tenant-scoped membership check as PayNotebook -- make
        // sure this studentId actually belongs to a group this notebook applies to.
        var studentGroupId = await _db.StudentGroupMemberships.AsNoTracking()
            .Where(m => m.StudentId == studentId)
            .Select(m => (int?)m.GroupId)
            .FirstOrDefaultAsync();
        if (studentGroupId == null || !notebook.GroupIds.Contains(studentGroupId.Value))
            return BadRequest(new { message = "This student is not enrolled in a group this notebook applies to." });

        // Prices are stored as whole numbers; the client can send something
        // like 50.0, so accept decimal and round rather than rejecting it
        // outright with a binding error.
        var roundedDiscountedPrice = (int)Math.Round(discountedPrice, MidpointRounding.AwayFromZero);
        if (roundedDiscountedPrice <= 0 || roundedDiscountedPrice > notebook.Price)
            return BadRequest(new { message = $"Discounted price must be between 1 and the notebook price ({notebook.Price})." });

        var payment = new NotebookPayment
        {
            TeacherId = notebook.TeacherId,
            NotebookId = notebookId,
            StudentId = studentId,
            Price = notebook.Price,
            DiscountedPrice = roundedDiscountedPrice
        };
        _db.NotebookPayments.Add(payment);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Discount applied.", discountedPrice = roundedDiscountedPrice });
    }

    // GET Students/notebooks  (student, own — no params, studentId comes from JWT)
    // Every notebook targeted at the student's group under the CURRENT tenant,
    // with: name, price, total paid so far, and the full payment history --
    // each payment shows which lecture it was made for and how much of the
    // notebook's price was still remaining right after that payment.
    [HttpGet("notebooks")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> GetMyNotebooks()
    {
        var studentId = User.GetUserId();

        // Resolve this student's group under the CURRENT tenant -- same
        // resolution order as GetAttendance, since a student can belong to
        // several teachers' groups at once (MULTI-TENANT MEMBERSHIP).
        var tenantGroupId = await _db.StudentGroupMemberships.AsNoTracking()
            .Where(m => m.StudentId == studentId)
            .Select(m => (int?)m.GroupId)
            .FirstOrDefaultAsync();

        if (tenantGroupId == null)
        {
            var student = await _db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == studentId);
            if (student != null)
                tenantGroupId = await _db.Groups.AsNoTracking()
                    .Where(g => g.Id == student.GroupId && g.TeacherId == _tenant.CurrentTenantId)
                    .Select(g => (int?)g.Id)
                    .FirstOrDefaultAsync();
        }

        var notebooks = tenantGroupId == null
            ? new List<Notebook>()
            : (await _db.Notebooks.AsNoTracking().ToListAsync())
                .Where(n => n.GroupIds.Contains(tenantGroupId.Value))
                .ToList();

        var notebookIds = notebooks.Select(n => n.Id).ToList();

        var payments = await _db.NotebookPayments.AsNoTracking()
            .Where(p => p.StudentId == studentId && notebookIds.Contains(p.NotebookId))
            .Include(p => p.Lecture)
            .OrderBy(p => p.Date)
            .ToListAsync();

        // Lecture has no Unit navigation property (just a raw UnitId), so
        // batch-load the handful of Units these lectures point to.
        var unitIds = payments.Where(p => p.Lecture?.UnitId != null)
            .Select(p => p.Lecture!.UnitId!.Value).Distinct().ToList();
        var unitsById = await _db.Units.AsNoTracking()
            .Where(u => unitIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Name);

        var paymentsByNotebook = payments.GroupBy(p => p.NotebookId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = notebooks.Select(n =>
        {
            var notebookPayments = paymentsByNotebook.GetValueOrDefault(n.Id, new List<NotebookPayment>());

            // Running remaining balance: computed step-by-step in payment
            // order, so each payment can report exactly what was left of the
            // notebook's price right after it was made.
            var runningPaid = 0;
            var paymentDtos = notebookPayments.Select(p =>
            {
                var amountPaid = p.DiscountedPrice ?? p.Price;
                runningPaid += amountPaid;
                return new
                {
                    id = p.Id,
                    amountPaid,
                    date = p.Date,
                    lecture = p.Lecture == null ? null : new
                    {
                        id = p.Lecture.Id,
                        name = p.Lecture.Name,
                        unit = p.Lecture.UnitId != null && unitsById.TryGetValue(p.Lecture.UnitId.Value, out var uName)
                            ? new { id = p.Lecture.UnitId.Value, name = uName }
                            : null
                    },
                    remaining = Math.Max(0, n.Price - runningPaid)
                };
            }).ToList();

            var totalPaid = notebookPayments.Sum(p => p.DiscountedPrice ?? p.Price);

            return new
            {
                id = n.Id,
                name = n.Name,
                price = n.Price,
                totalPaid,
                remaining = Math.Max(0, n.Price - totalPaid),
                payments = paymentDtos
            };
        });

        return Ok(result);
    }

    // GET Students/quiz-results/center?studentId=..  (teacher) / no params (student, own)
    [HttpGet("quiz-results/center")]
    public async Task<IActionResult> GetCenterQuizResults([FromQuery] int? studentId)
    {
        var sid = User.IsInRole(Roles.Student) ? User.GetUserId() : studentId;
        if (sid == null) return BadRequest(new { message = "studentId is required." });

        var results = await _db.CenterQuizResults.AsNoTracking().Where(r => r.StudentId == sid.Value)
            .Select(r => new { id = r.Id, title = r.Title, mark = r.Marks, quizTotalMarks = r.TotalMarks, date = r.Date })
            .ToListAsync();

        return Ok(results);
    }

    // POST Students/quiz-results/center/add  body: { studentId, quizTotalMarks, mark }
    [HttpPost("quiz-results/center/add")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> AddCenterQuizResult([FromBody] AddCenterQuizResultRequest request)
    {
        if (_tenant.CurrentTenantId == null) return Forbid();

        var result = new CenterQuizResult
        {
            StudentId = request.StudentId,
            Title = "Center Quiz",
            Marks = request.Mark,
            TotalMarks = request.QuizTotalMarks,
            TeacherId = _tenant.CurrentTenantId.Value
        };
        _db.CenterQuizResults.Add(result);
        await _db.SaveChangesAsync();
        return StatusCode(201, new { id = result.Id });
    }

    // POST Students/quiz-results/change?studentId=..&quizResultId=..&newMark=..  body: {}
    [HttpPost("quiz-results/change")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangeCenterQuizMark([FromQuery] int studentId, [FromQuery] int quizResultId, [FromQuery] int newMark)
    {
        var result = await _db.CenterQuizResults.FirstOrDefaultAsync(r => r.Id == quizResultId && r.StudentId == studentId);
        if (result == null) return NotFound(new { message = "Result not found." });

        result.Marks = newMark;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Mark updated." });
    }

    // POST Students/quiz-results/delete?studentId=..&quizResultId=..
    [HttpPost("quiz-results/delete")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> DeleteCenterQuizResult([FromQuery] int studentId, [FromQuery] int quizResultId)
    {
        var result = await _db.CenterQuizResults.FirstOrDefaultAsync(r => r.Id == quizResultId && r.StudentId == studentId);
        if (result == null) return NotFound(new { message = "Result not found." });

        _db.CenterQuizResults.Remove(result);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Result deleted." });
    }

    // GET Students/homework-results?studentId=..  (teacher) / no params (student, own)
    [HttpGet("homework-results")]
    public async Task<IActionResult> GetHomeworkResults([FromQuery] int? studentId)
    {
        var sid = User.IsInRole(Roles.Student) ? User.GetUserId() : studentId;
        if (sid == null) return BadRequest(new { message = "studentId is required." });

        var results = await _db.HomeworkResults.AsNoTracking().Where(r => r.StudentId == sid.Value)
            .Select(r => new { id = r.Id, title = r.Title, mark = r.Marks, totalMarks = r.TotalMarks, date = r.Date })
            .ToListAsync();

        return Ok(results);
    }

    // POST Students/homework-results/add  body: { studentId, totalMarks, mark }
    [HttpPost("homework-results/add")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> AddHomeworkResult([FromBody] AddHomeworkResultRequest request)
    {
        if (_tenant.CurrentTenantId == null) return Forbid();

        var result = new HomeworkResult
        {
            StudentId = request.StudentId,
            Title = "Homework",
            Marks = request.Mark,
            TotalMarks = request.TotalMarks,
            TeacherId = _tenant.CurrentTenantId.Value
        };
        _db.HomeworkResults.Add(result);
        await _db.SaveChangesAsync();
        return StatusCode(201, new { id = result.Id });
    }

    // POST Students/homework-results/change?studentId=..&homeworkResultId=..&newMark=..  body: {}
    [HttpPost("homework-results/change")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangeHomeworkMark([FromQuery] int studentId, [FromQuery] int homeworkResultId, [FromQuery] int newMark)
    {
        var result = await _db.HomeworkResults.FirstOrDefaultAsync(r => r.Id == homeworkResultId && r.StudentId == studentId);
        if (result == null) return NotFound(new { message = "Result not found." });

        result.Marks = newMark;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Mark updated." });
    }

    // POST Students/homework-results/delete?studentId=..&homeworkResultId=..
    [HttpPost("homework-results/delete")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> DeleteHomeworkResult([FromQuery] int studentId, [FromQuery] int homeworkResultId)
    {
        var result = await _db.HomeworkResults.FirstOrDefaultAsync(r => r.Id == homeworkResultId && r.StudentId == studentId);
        if (result == null) return NotFound(new { message = "Result not found." });

        _db.HomeworkResults.Remove(result);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Result deleted." });
    }

    // GET Students/quiz-results/online  (student, own online-quiz history)
    [HttpGet("quiz-results/online")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> GetOnlineQuizResults()
    {
        var studentId = User.GetUserId();
        // PERFORMANCE: no .Include(r => r.Answers) here -- the projection below
        // only reads scalar columns off QuizResult itself, so pulling in the
        // Answers collection would mean a needless extra join/round trip for
        // data that's thrown away immediately.
        var results = await _db.QuizResults.AsNoTracking()
            .Where(r => r.StudentId == studentId)
            .Select(r => new { id = r.Id, quizId = r.QuizId, mark = r.Score, quizTotalMarks = r.TotalMarks, date = r.GradedAt })
            .ToListAsync();

        return Ok(results);
    }

    private static string GenerateTempPassword() => Guid.NewGuid().ToString("N")[..8];

    // FEATURE: fires the "here's your AcademIQ account" WhatsApp message to
    // the student's own phone right after their Student row is created (via
    // Create, or a "Created" row in the Excel/Google Sheet import). Sends the
    // effective login (UserName, falling back to PhoneNumber exactly like
    // AuthController's login lookup does) plus the plain-text password —
    // must be called BEFORE the password is discarded/hashed-over.
    // Best-effort only, same as SendAttendanceWhatsAppAsync: never let a
    // WhatsApp failure fail the student-creation request itself.
    private async Task SendWelcomeWhatsAppAsync(Student student, string plainPassword)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(student.PhoneNumber))
            {
                _logger.LogInformation("Skipping WhatsApp welcome message for student {StudentId}: no phone number on file.", student.Id);
                return;
            }

            var data = new StudentWelcomeWhatsAppNotification(
                StudentName: student.Name,
                UserName: student.UserName ?? student.PhoneNumber,
                Password: plainPassword
            );

            var sent = await _whatsApp.SendWelcomeMessageAsync(student.PhoneNumber, data);
            if (!sent)
                _logger.LogWarning("WhatsApp welcome message not sent for student {StudentId}.", student.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error while sending WhatsApp welcome message for student {StudentId}.", student.Id);
        }
    }

    private static StudentListItem ToListItem(Student s, string? groupName, bool isSuspended = false, bool isCancelled = false)
    {
        var status = isCancelled ? "Cancelled" : isSuspended ? "Suspended" : "Active";
        return new(
            s.Id, s.Name, s.PhoneNumber, s.UserName, s.GroupId, groupName, isSuspended, isCancelled,
            status, null);
    }
}
