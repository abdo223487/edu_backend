namespace EduApi.DTOs;

// GET Wallet/me (student) and GET Wallet/students/{studentId} (teacher/assistant)
// both return this shape.
public record WalletBalanceDto(int StudentId, string StudentName, decimal Balance);

public record WalletTransactionDto(
    int Id,
    decimal Amount,
    decimal BalanceAfter,
    string Type,
    string? Note,
    int? RelatedUnitId,
    string? RelatedUnitName,
    DateTime CreatedAt);

// POST Wallet/students/{studentId}/adjust body. Amount can be positive (add
// points) or negative (deduct points) -- the teacher/assistant decides which
// via the Flutter UI (two buttons: "إضافة" / "خصم"), but the API itself just
// takes a signed amount so it stays a single simple endpoint either way.
public record WalletAdjustRequest(decimal Amount, string? Note);

// POST Wallet/purchase-unit body (student). Mirrors RedeemCodeRequest but for
// spending wallet points on a specific Unit instead of a Code.
public record WalletPurchaseUnitRequest(int UnitId);
