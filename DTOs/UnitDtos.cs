namespace EduApi.DTOs;

// NOTE: kept only for reference — POST Units now REQUIRES multipart/form-data
// with a mandatory "image"/"Image" file, so this JSON-only shape is no longer
// accepted by the endpoint (see UnitsController.CreateUnit).
public record CreateUnitRequest(string Name, int SchoolYear, int? Month, decimal? Price);

// "Subscribed" is only meaningful for a student caller: true when they're
// actually subscribed to the unit (or have at least one unlocked lesson
// inside it), false otherwise — the unit is still listed either way so
// students can see what's available. For teacher/staff callers (who manage
// the whole catalog) it's always true.
//
// "Price" is optional (null = no price shown/set). It's purely informational
// for the catalog listing itself, but also doubles as the amount a student
// can spend from their points Wallet to unlock the unit instead of using a
// Code -- see WalletController.PurchaseUnit.
public record UnitListItem(int Id, string Name, int SchoolYear, int? Month, string? ImageUrl, bool Subscribed, decimal? Price);

public record LessonDto(int Id, int Index, string Name, string? ImageUrl);

public record UnitDetailDto(
    int Id,
    string Name,
    int SchoolYear,
    int? Month,
    string? ImageUrl,
    decimal? Price,
    List<LessonDto> Lessons);
