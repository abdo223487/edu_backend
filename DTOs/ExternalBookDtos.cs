namespace EduApi.DTOs;

// NOTE: kept only for reference — POST ExternalBooks REQUIRES multipart/form-data
// with a mandatory "image"/"Image" file, see ExternalBooksController.CreateExternalBook.
public record CreateExternalBookRequest(string Name, int SchoolYear, int? Month, int? UnitId);

// "Subscribed" is only meaningful for a student caller: true when they can
// actually access the book -- either via a direct StudentExternalBookSubscription
// (redeemed a code for it) or, when UnitId is set, via a live/claimed
// subscription to that parent Unit. False otherwise -- the book is still
// listed either way so students can see what's available, same as Units.
// For teacher/staff callers it's always true.
public record ExternalBookListItem(
    int Id, string Name, int SchoolYear, int? Month, string? ImageUrl, int? UnitId, bool Subscribed);

public record ExternalBookLessonDto(int Id, int Index, string Name, string? ImageUrl);

public record ExternalBookDetailDto(
    int Id,
    string Name,
    int SchoolYear,
    int? Month,
    string? ImageUrl,
    int? UnitId,
    List<ExternalBookLessonDto> Lessons);
