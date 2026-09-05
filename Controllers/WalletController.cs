using EduApi.Common;
using EduApi.Data;
using EduApi.DTOs;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// Points wallet, one per student. Route: api/Wallet. Mirrors the Flutter calls:
///  GET  Wallet/students/{studentId}         (teacher/assistant -- view a
///                                             specific student's balance + history)
///  POST Wallet/students/{studentId}/adjust  (teacher/assistant -- add or
///                                             deduct points, from the "student
///                                             details" quick-action button)
///  GET  Wallet/me                           (student -- own balance + history,
///                                             from the profile page's wallet button)
///  POST Wallet/purchase-unit                (student -- spend wallet points to
///                                             unlock a Unit, alternative to a Code)
///
/// Student.WalletBalance is a cached running total; WalletTransactions is the
/// append-only audit trail both endpoints read/write together, always inside
/// a DB transaction so the cached balance and the ledger can never drift apart.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class WalletController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly Common.ITenantContext _tenant;

    public WalletController(AppDbContext db, Common.ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    // ───────────────────────────── TEACHER SIDE ─────────────────────────────

    // GET Wallet/students/{studentId}
    [HttpGet("students/{studentId:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.Assistant}")]
    public async Task<IActionResult> GetStudentWallet(int studentId)
    {
        var student = await _db.Students.AsNoTracking()
            .Where(s => s.Id == studentId)
            .Select(s => new { s.Id, s.Name, s.WalletBalance })
            .FirstOrDefaultAsync();
        if (student == null) return NotFound(new { message = "Student not found." });

        var transactions = await BuildTransactionsAsync(studentId);

        return Ok(new
        {
            wallet = new WalletBalanceDto(student.Id, student.Name, student.WalletBalance),
            transactions
        });
    }

    // POST Wallet/students/{studentId}/adjust  body: { amount, note }
    // amount > 0 adds points, amount < 0 deducts points. The Flutter details
    // page exposes this as two buttons ("إضافة نقاط" / "خصم نقاط") that just
    // send a positive or negative amount to the same endpoint.
    [HttpPost("students/{studentId:int}/adjust")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.Assistant}")]
    public async Task<IActionResult> AdjustStudentWallet(int studentId, [FromBody] WalletAdjustRequest request)
    {
        if (request.Amount == 0)
            return BadRequest(new { message = "Amount must not be zero." });

        var teacherId = User.GetStaffTenantId();
        if (teacherId == null) return Forbid();

        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == studentId);
        if (student == null) return NotFound(new { message = "Student not found." });

        // A deduction can never take the cached balance below zero -- the
        // teacher sees the current balance in the UI before typing an amount,
        // so this only ever fires on a race (two adjustments at once) or a
        // stale screen, and it's better to reject than to let points go negative.
        if (request.Amount < 0 && student.WalletBalance + request.Amount < 0)
            return Conflict(new { message = "Insufficient wallet balance for this deduction." });

        await using var tx = await _db.Database.BeginTransactionAsync();

        student.WalletBalance += request.Amount;
        _db.WalletTransactions.Add(new WalletTransaction
        {
            StudentId = studentId,
            TeacherId = teacherId.Value,
            Amount = request.Amount,
            BalanceAfter = student.WalletBalance,
            Type = "manual",
            Note = request.Note,
            CreatedByStaffId = User.GetUserId(),
        });

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return Ok(new WalletBalanceDto(student.Id, student.Name, student.WalletBalance));
    }

    // ───────────────────────────── STUDENT SIDE ─────────────────────────────

    // GET Wallet/me
    [HttpGet("me")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> GetMyWallet()
    {
        var studentId = User.GetUserId();
        var student = await _db.Students.AsNoTracking()
            .Where(s => s.Id == studentId)
            .Select(s => new { s.Id, s.Name, s.WalletBalance })
            .FirstOrDefaultAsync();
        if (student == null) return NotFound(new { message = "Student not found." });

        var transactions = await BuildTransactionsAsync(studentId);

        return Ok(new
        {
            wallet = new WalletBalanceDto(student.Id, student.Name, student.WalletBalance),
            transactions
        });
    }

    // POST Wallet/purchase-unit  body: { unitId }
    // Alternative to POST Students/codes: instead of entering a Code, the
    // student spends Unit.Price points straight from their wallet. Grants the
    // exact same StudentUnitSubscription a matching Code redemption would.
    [HttpPost("purchase-unit")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> PurchaseUnit([FromBody] WalletPurchaseUnitRequest request)
    {
        var studentId = User.GetUserId();
        var teacherId = _tenant.CurrentTenantId;
        if (teacherId == null) return Forbid();

        var unit = await _db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.UnitId);
        if (unit == null) return NotFound(new { message = "Unit not found." });

        if (unit.Price == null || unit.Price <= 0)
            return BadRequest(new { message = "This course has no price -- it can only be unlocked with a code." });

        if (await _db.StudentUnitSubscriptions.AnyAsync(s => s.StudentId == studentId && s.UnitId == unit.Id))
            return Conflict(new { message = "Already subscribed to this course." });

        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == studentId);
        if (student == null) return NotFound(new { message = "Student not found." });

        if (student.WalletBalance < unit.Price.Value)
            return Conflict(new { message = "Insufficient wallet balance." });

        await using var tx = await _db.Database.BeginTransactionAsync();

        // RACE-CONDITION GUARD: same idea as Codes.RedeemCode -- an atomic,
        // conditional UPDATE (only succeeds while the cached balance still
        // covers the price) so two near-simultaneous purchase taps (or a
        // double-tap) from the same student can't both go through and double
        // spend a balance that only actually covers one of them.
        var debited = await _db.Students
            .Where(s => s.Id == studentId && s.WalletBalance >= unit.Price.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.WalletBalance, x => x.WalletBalance - unit.Price!.Value));

        if (debited == 0)
        {
            await tx.RollbackAsync();
            return Conflict(new { message = "Insufficient wallet balance." });
        }

        var newBalance = student.WalletBalance - unit.Price.Value;

        _db.WalletTransactions.Add(new WalletTransaction
        {
            StudentId = studentId,
            TeacherId = teacherId.Value,
            Amount = -unit.Price.Value,
            BalanceAfter = newBalance,
            Type = "purchase",
            Note = $"شراء كورس: {unit.Name}",
            RelatedUnitId = unit.Id,
        });

        _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription
        {
            TeacherId = teacherId.Value,
            StudentId = studentId,
            UnitId = unit.Id,
        });

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return Ok(new
        {
            message = "Course unlocked successfully.",
            unitId = unit.Id,
            newBalance
        });
    }

    // ───────────────────────────── shared helper ─────────────────────────────

    private async Task<List<WalletTransactionDto>> BuildTransactionsAsync(int studentId)
    {
        var rows = await _db.WalletTransactions.AsNoTracking()
            .Where(w => w.StudentId == studentId)
            .OrderByDescending(w => w.CreatedAt)
            .ToListAsync();

        var unitIds = rows.Where(r => r.RelatedUnitId != null).Select(r => r.RelatedUnitId!.Value).Distinct().ToList();
        var unitNames = await _db.Units.AsNoTracking().Where(u => unitIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Name);

        return rows.Select(r => new WalletTransactionDto(
            r.Id, r.Amount, r.BalanceAfter, r.Type, r.Note, r.RelatedUnitId,
            r.RelatedUnitId != null && unitNames.TryGetValue(r.RelatedUnitId.Value, out var n) ? n : null,
            r.CreatedAt)).ToList();
    }
}
