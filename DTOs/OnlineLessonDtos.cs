namespace EduApi.DTOs;

// POST OnlineLessons (multipart/form-data) fields: Name, SchoolYear, Image (file, REQUIRED)
// POST OnlineLessons/edit (multipart, partial): Id, Name?, Image?
// POST OnlineLessons/delete?onlineLessonId=..
// GET  OnlineLessons?schoolYear=..
//   teacher/staff -> every container for that year
//   student       -> only containers they've redeemed a code for (never
//                     the locked ones — there's nothing to show/tease,
//                     the only way in is already having the code)
// GET  OnlineLessons/{id} -> full detail incl. its lectures
//   teacher/staff -> always allowed
//   student       -> 403 unless they've redeemed a code for it

public record OnlineLessonListItem(int Id, string Name, int SchoolYear, string? ImageUrl);

// Mirrors LectureListItem's video-related shape, minus everything that only
// makes sense for a Center/Unit lecture (no UnitId, no LessonIndex, no
// GroupIds, no AttendanceMethod since it's always "Online" here).
public record OnlineLectureDto(
    int Id,
    string Name,
    string? Link,
    string? VideoSourceType,
    string? ThumbnailUrl,
    DateTime CreatedAt);

public record OnlineLessonDetailDto(
    int Id,
    string Name,
    int SchoolYear,
    string? ImageUrl,
    List<OnlineLectureDto> Lectures);
