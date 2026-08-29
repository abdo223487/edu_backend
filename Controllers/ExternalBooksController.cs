using EduApi.Common;
using EduApi.Data;
using EduApi.DTOs;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/ExternalBooks. "الكتب الخارجية" -- a Unit-shaped container
/// (same Name/SchoolYear/Month/Image, its own nested Lessons, Lectures and
/// Materials created inside it exactly like a Unit's) with one extra,
/// optional field: UnitId. When set, every student already subscribed to
/// that Unit automatically gets access to this book too, with no code
/// needed. When null, the only way in is redeeming a Code that carries this
/// book's Id (see Code.ExternalBookIds / StudentsController.RedeemCode),
/// exactly the same "code-gated" model a Unit itself uses.
///
/// Mirrors UnitsController's endpoints one-for-one:
///  GET    ExternalBooks?schoolYear=..        (teacher)
///  GET    ExternalBooks                      (student; year comes from JWT)
///  GET    ExternalBooks/{id}
///  POST   ExternalBooks                      (multipart, image REQUIRED, UnitId OPTIONAL)
///  POST   ExternalBooks/edit                 (multipart)
///  POST   ExternalBooks/delete?externalBookId=..
///  POST   ExternalBooks/lessons              (multipart, image REQUIRED) -- add lesson
///  POST   ExternalBooks/lessons/edit         (multipart)
///  POST   ExternalBooks/lessons/delete?externalBookId=..&lessonIndex=..
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ExternalBooksController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _files;

    public ExternalBooksController(AppDbContext db, IFileStorageService files)
    {
        _db = db;
        _files = files;
    }

    /// <summary>
    /// True when the given student can access this external book: either a
    /// direct redeemed-code subscription, OR (when the book has a UnitId) a
    /// live subscription to that parent Unit -- same "unit unlocks its
    /// linked books" rule described on ExternalBook.UnitId.
    /// </summary>
    private async Task<bool> IsSubscribedAsync(int studentId, int externalBookId, int? unitId)
    {
        var directly = await _db.StudentExternalBookSubscriptions.AsNoTracking()
            .AnyAsync(s => s.StudentId == studentId && s.ExternalBookId == externalBookId);
        if (directly) return true;

        if (unitId.HasValue)
        {
            if (User.GetUnitIds().Contains(unitId.Value)) return true;
            var viaUnit = await _db.StudentUnitSubscriptions.AsNoTracking()
                .AnyAsync(s => s.StudentId == studentId && s.UnitId == unitId.Value);
            if (viaUnit) return true;
        }

        return false;
    }

    [HttpGet]
    public async Task<IActionResult> GetExternalBooks([FromQuery] int? schoolYear)
    {
        var year = schoolYear ?? User.GetSchoolYear();
        var query = _db.ExternalBooks.AsNoTracking().AsQueryable();
        if (year.HasValue) query = query.Where(e => e.SchoolYear == year.Value);

        var books = await query.ToListAsync();

        var isStudent = User.IsInRole(Roles.Student);
        HashSet<int> subscribedIds = new();
        if (isStudent)
        {
            var studentId = User.GetUserId();
            foreach (var b in books)
                if (await IsSubscribedAsync(studentId, b.Id, b.UnitId))
                    subscribedIds.Add(b.Id);
        }

        var items = books
            .Select(b => new ExternalBookListItem(
                b.Id, b.Name, b.SchoolYear, b.Month, b.ImageUrl, b.UnitId,
                !isStudent || subscribedIds.Contains(b.Id)))
            .ToList();

        return Ok(items);
    }

    [HttpGet("{externalBookId:int}")]
    public async Task<IActionResult> GetExternalBook(int externalBookId)
    {
        var book = await _db.ExternalBooks.AsNoTracking().Include(e => e.Lessons)
            .FirstOrDefaultAsync(e => e.Id == externalBookId);
        if (book == null) return NotFound(new { message = "External book not found." });

        if (User.IsInRole(Roles.Student))
        {
            var studentId = User.GetUserId();
            if (!await IsSubscribedAsync(studentId, book.Id, book.UnitId))
                return StatusCode(403, new { message = "Not subscribed to this external book." });
        }

        var lessons = book.Lessons.OrderBy(l => l.LessonIndex)
            .Select(l => new ExternalBookLessonDto(l.Id, l.LessonIndex, l.Name, l.ImageUrl))
            .ToList();

        var dto = new ExternalBookDetailDto(book.Id, book.Name, book.SchoolYear, book.Month, book.ImageUrl, book.UnitId, lessons);
        return Ok(dto);
    }

    // Image is MANDATORY, same as Units. UnitId is OPTIONAL: when provided,
    // students subscribed to that unit get this book for free.
    [HttpPost]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> CreateExternalBook(
        [FromForm] string name,
        [FromForm] int schoolYear,
        [FromForm] int? month,
        [FromForm] int? unitId,
        IFormFile image)
    {
        if (image == null || image.Length == 0)
            return BadRequest(new { message = "Image is required to create an External Book." });

        if (unitId.HasValue && !await _db.Units.AnyAsync(u => u.Id == unitId.Value))
            return NotFound(new { message = "Unit not found." });

        var book = new ExternalBook
        {
            Name = name,
            SchoolYear = schoolYear,
            Month = month,
            UnitId = unitId,
            TeacherId = User.GetStaffTenantId()!.Value, // TENANT LAYER
            ImageUrl = await _files.SaveAsync(image, "external-books")
        };

        _db.ExternalBooks.Add(book);
        await _db.SaveChangesAsync();

        return StatusCode(201, new ExternalBookListItem(book.Id, book.Name, book.SchoolYear, book.Month, book.ImageUrl, book.UnitId, true));
    }

    // Same partial-update contract as EditUnit: multipart, only the fields
    // actually being edited are sent. "ClearUnit" (true) detaches the parent
    // Unit link; otherwise "UnitId" (when present) replaces it.
    [HttpPost("edit")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> EditExternalBook(
        [FromForm(Name = "Id")] int id,
        [FromForm(Name = "Name")] string? name,
        [FromForm(Name = "Month")] int? month,
        [FromForm(Name = "ClearMonth")] bool? clearMonth,
        [FromForm(Name = "UnitId")] int? unitId,
        [FromForm(Name = "ClearUnit")] bool? clearUnit,
        IFormFile? image)
    {
        var book = await _db.ExternalBooks.FirstOrDefaultAsync(e => e.Id == id);
        if (book == null) return NotFound(new { message = "External book not found." });

        if (name != null) book.Name = name;

        if (clearMonth == true) book.Month = null;
        else if (month.HasValue) book.Month = month.Value;

        if (clearUnit == true) book.UnitId = null;
        else if (unitId.HasValue)
        {
            if (!await _db.Units.AnyAsync(u => u.Id == unitId.Value))
                return NotFound(new { message = "Unit not found." });
            book.UnitId = unitId.Value;
        }

        if (image != null && image.Length > 0)
        {
            var oldImageUrl = book.ImageUrl;
            book.ImageUrl = await _files.SaveAsync(image, "external-books");
            await _db.SaveChangesAsync();
            await _files.DeleteAsync(oldImageUrl);
            return Ok(new ExternalBookListItem(book.Id, book.Name, book.SchoolYear, book.Month, book.ImageUrl, book.UnitId, true));
        }

        await _db.SaveChangesAsync();
        return Ok(new ExternalBookListItem(book.Id, book.Name, book.SchoolYear, book.Month, book.ImageUrl, book.UnitId, true));
    }

    [HttpPost("delete")]
    public async Task<IActionResult> DeleteExternalBook([FromQuery] int externalBookId)
    {
        var book = await _db.ExternalBooks.FirstOrDefaultAsync(e => e.Id == externalBookId);
        if (book == null) return NotFound(new { message = "External book not found." });

        // Clean up the lectures inside it too, same defensive cleanup as
        // OnlineLessonsController.DeleteOnlineLesson.
        var childLectures = await _db.Lectures.Where(l => l.ExternalBookId == externalBookId).ToListAsync();
        _db.Lectures.RemoveRange(childLectures);

        _db.ExternalBooks.Remove(book);
        await _db.SaveChangesAsync();
        return Ok(new { message = "External book deleted." });
    }

    // GET ExternalBooks/count — total number of external books belonging to
    // the caller's tenant, mirrors Units/count.
    [HttpGet("count")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Count()
        => Ok(await _db.ExternalBooks.CountAsync());

    [HttpPost("lessons")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> AddLesson(
        [FromForm(Name = "ExternalBookId")] int externalBookId,
        [FromForm(Name = "Name")] string name,
        [FromForm(Name = "Index")] int? index,
        IFormFile image)
    {
        if (image == null || image.Length == 0)
            return BadRequest(new { message = "Image is required to add a Lesson." });

        var book = await _db.ExternalBooks.Include(e => e.Lessons).FirstOrDefaultAsync(e => e.Id == externalBookId);
        if (book == null) return NotFound(new { message = "External book not found." });

        int lessonIndex;
        if (index.HasValue)
        {
            if (book.Lessons.Any(l => l.LessonIndex == index.Value))
                return Conflict(new { message = "This lesson number is already used." });
            lessonIndex = index.Value;
        }
        else
        {
            lessonIndex = book.Lessons.Count == 0 ? 0 : book.Lessons.Max(l => l.LessonIndex) + 1;
        }

        var imageUrl = await _files.SaveAsync(image, "lessons");
        var lesson = new ExternalBookLesson { ExternalBookId = externalBookId, LessonIndex = lessonIndex, Name = name, ImageUrl = imageUrl };
        _db.ExternalBookLessons.Add(lesson);
        await _db.SaveChangesAsync();

        return StatusCode(201, new ExternalBookLessonDto(lesson.Id, lesson.LessonIndex, lesson.Name, lesson.ImageUrl));
    }

    [HttpPost("lessons/edit")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> EditLesson(
        [FromForm(Name = "ExternalBookId")] int externalBookId,
        [FromForm(Name = "LessonIndex")] int lessonIndex,
        [FromForm(Name = "Name")] string? name,
        IFormFile? image)
    {
        var lesson = await _db.ExternalBookLessons.FirstOrDefaultAsync(l => l.ExternalBookId == externalBookId && l.LessonIndex == lessonIndex);
        if (lesson == null) return NotFound(new { message = "Lesson not found." });

        if (name != null) lesson.Name = name;

        if (image != null && image.Length > 0)
        {
            var oldImageUrl = lesson.ImageUrl;
            lesson.ImageUrl = await _files.SaveAsync(image, "lessons");
            await _db.SaveChangesAsync();
            await _files.DeleteAsync(oldImageUrl);
            return Ok(new ExternalBookLessonDto(lesson.Id, lesson.LessonIndex, lesson.Name, lesson.ImageUrl));
        }

        await _db.SaveChangesAsync();
        return Ok(new ExternalBookLessonDto(lesson.Id, lesson.LessonIndex, lesson.Name, lesson.ImageUrl));
    }

    [HttpPost("lessons/delete")]
    public async Task<IActionResult> DeleteLesson([FromQuery] int externalBookId, [FromQuery] int lessonIndex)
    {
        var lesson = await _db.ExternalBookLessons.FirstOrDefaultAsync(l => l.ExternalBookId == externalBookId && l.LessonIndex == lessonIndex);
        if (lesson == null) return NotFound(new { message = "Lesson not found." });

        _db.ExternalBookLessons.Remove(lesson);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Lesson deleted." });
    }
}
