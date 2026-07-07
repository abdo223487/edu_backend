namespace EduApi.DTOs;

// NOTE: kept only for reference — POST Units now REQUIRES multipart/form-data
// with a mandatory "image"/"Image" file, so this JSON-only shape is no longer
// accepted by the endpoint (see UnitsController.CreateUnit).
public record CreateUnitRequest(string Name, int SchoolYear, int? Month);

public record UnitListItem(int Id, string Name, int SchoolYear, int? Month, string? ImageUrl);

public record LessonDto(int Id, int Index, string Name, string? ImageUrl);

public record UnitDetailDto(
    int Id,
    string Name,
    int SchoolYear,
    int? Month,
    string? ImageUrl,
    List<LessonDto> Lessons);
