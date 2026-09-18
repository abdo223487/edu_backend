using EduApi.Common;
using EduApi.Data;
using EduApi.DTOs;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// Points wallet, one per (student, teacher) pair. Route: api/Wallet. Mirrors
/// the Flutter calls:
///  GET  Wallet/students/{studentId}         (teacher/assistant -- view a
///                                             specific student's balance + history,
///                                             scoped to THIS teacher only)
///  POST Wallet/students/{studentId}/adjust  (teacher/assistant -- add or
///                                             deduct points, from the "student
///                                             details" quick-action button)
///  GET  Wallet/me                           (student -- own balance + history
///                                             under the CURRENT teacher (X-TenantId),
///                                             from the profile page's wallet button)
///  POST Wallet/purchase-unit                (student -- spend wallet points to
///                                             unlock a Unit, alternative to a Code)
///
/// BUGFIX (cross-tenant wallet leak): balances used to live on a single
/// Student.WalletBalance column, shared by every teacher that student is
/// linked to -- points added by teacher A were visible and spendable under
/// teacher B too. Balances now live in StudentWallet, one row per
/// (StudentId, TeacherId), exactly like WalletTransactions already did (see
/// its TeacherId column / global query filter). Every read/write below is
/// scoped to the CURRENT teacher's row only, and StudentWallet carries the
/// same TeacherId global query filter as WalletTransaction, so a stray query
/// that forgets to filter still can't cross tenants.
/// Student.WalletBalance itself is no longer read or written anywhere in
/// this controller; it is left in place only as a harmless legacy column
/// (its pre-existing values were migrated into StudentWallet rows once --
/// see the AddStudentWalletPerTeacher migration).
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
        var teacherId = User.GetStaffTenantId();
        if (teacherId == null) return Forbid();

        // Student query filter already ensures this student actually belongs
        // to the current teacher (legacy Group or a StudentGroupMembership).
        var student = await _db.Students.AsNoTracking()
            .Where(s => s.Id == studentId)
            .Select(s => new { s.Id, s.Name })
            .FirstOrDefaultAsync();
        if (student == null) return NotFound(new { message = "Student not found." });

        // No row yet == this student has never had a wallet transaction with
        // THIS teacher, i.e. a balance of 0 -- not an error.
        var balance = await _db.StudentWallets.AsNoTracking()
            .Where(w => w.StudentId == studentId && w.TeacherId == teacherId.Value)
            .Select(w => (decimal?)w.Balance)
            .FirstOrDefaultAsync() ?? 0m;

        var transactions = await BuildTransactionsAsync(studentId);

        return Ok(new
        {
            wallet = new WalletBalanceDto(student.Id, student.Name, balance),
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

        var studentExists = await _db.Students.AsNoTracking().AnyAsync(s => s.Id == studentId);
        if (!studentExists) return NotFound(new { message = "Student not found." });

        await using var tx = await _db.Database.BeginTransactionAsync();

        // Get-or-create THIS teacher's wallet row for THIS student.
        var wallet = await _db.StudentWallets
            .FirstOrDefaultAsync(w => w.StudentId == studentId && w.TeacherId == teacherId.Value);
        if (wallet == null)
        {
            wallet = new StudentWallet { StudentId = studentId, TeacherId = teacherId.Value, Balance = 0 };
            _db.StudentWallets.Add(wallet);
        }

        // A deduction can never take the balance below zero -- the teacher
        // sees the current balance in the UI before typing an amount, so this
        // only ever fires on a race (two adjustments at once) or a stale
        // screen, and it's better to reject than to let points go negative.
        if (request.Amount < 0 && wallet.Balance + request.Amount < 0)
        {
            await tx.RollbackAsync();
            return Conflict(new { message = "Insufficient wallet balance for this deduction." });
        }

        wallet.Balance += request.Amount;
        _db.WalletTransactions.Add(new WalletTransaction
        {
            StudentId = studentId,
            TeacherId = teacherId.Value,
            Amount = request.Amount,
            BalanceAfter = wallet.Balance,
            Type = "manual",
            Note = request.Note,
            CreatedByStaffId = User.GetUserId(),
        });

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        var studentName = await _db.Students.AsNoTracking()
            .Where(s => s.Id == studentId).Select(s => s.Name).FirstOrDefaultAsync();

        return Ok(new WalletBalanceDto(studentId, studentName ?? "", wallet.Balance));
    }

    // ───────────────────────────── STUDENT SIDE ─────────────────────────────

    // GET Wallet/me
    [HttpGet("me")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> GetMyWallet()
    {
        var studentId = User.GetUserId();
        var teacherId = _tenant.CurrentTenantId;
        if (teacherId == null) return Forbid();

        var student = await _db.Students.AsNoTracking()
            .Where(s => s.Id == studentId)
            .Select(s => new { s.Id, s.Name })
            .FirstOrDefaultAsync();
        if (student == null) return NotFound(new { message = "Student not found." });

        var balance = await _db.StudentWallets.AsNoTracking()
            .Where(w => w.StudentId == studentId && w.TeacherId == teacherId.Value)
            .Select(w => (decimal?)w.Balance)
            .FirstOrDefaultAsync() ?? 0m;

        var transactions = await BuildTransactionsAsync(studentId);

        return Ok(new
        {
            wallet = new WalletBalanceDto(student.Id, student.Name, balance),
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

        var studentExists = await _db.Students.AsNoTracking().AnyAsync(s => s.Id == studentId);
        if (!studentExists) return NotFound(new { message = "Student not found." });

        await using var tx = await _db.Database.BeginTransactionAsync();

        // RACE-CONDITION GUARD: same idea as Codes.RedeemCode -- an atomic,
        // conditional UPDATE (only succeeds while the balance still covers the
        // price) so two near-simultaneous purchase taps (or a double-tap) from
        // the same student can't both go through and double spend a balance
        // that only actually covers one of them. If no StudentWallet row
        // exists yet for this (student, teacher) pair, the balance is
        // implicitly 0 and this simply matches zero rows below, which is
        // exactly what we want (can't afford it).
        var debited = await _db.StudentWallets
            .Where(w => w.StudentId == studentId && w.TeacherId == teacherId.Value && w.Balance >= unit.Price.Value)
            .ExecuteUpdateAsync(w => w.SetProperty(x => x.Balance, x => x.Balance - unit.Price!.Value));

        if (debited == 0)
        {
            await tx.RollbackAsync();
            return Conflict(new { message = "Insufficient wallet balance." });
        }

        var newBalance = await _db.StudentWallets.AsNoTracking()
            .Where(w => w.StudentId == studentId && w.TeacherId == teacherId.Value)
            .Select(w => w.Balance)
            .FirstAsync();

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
        // WalletTransaction already carries the TeacherId global query filter,
        // so this is automatically scoped to the current tenant -- no change
        // needed here, only the balance itself was ever unscoped.
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
