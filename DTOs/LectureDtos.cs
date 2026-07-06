namespace EduApi.DTOs;

// POST Lectures
// body: { "name", "attendanceMethod": "Center"|"Online", "youtubeLink", "storageFileKey",
//         "groupIds": [..], "unitId": int?, "lessonIndex": int?, "schoolYear": int? }
// "schoolYear" is required when unitId is omitted (standalone/no-unit lecture) —
// it's ignored when unitId is set, since the Unit's own SchoolYear is used instead.
public record CreateLectureRequest(
    string Name,
    string AttendanceMethod,
    string? YoutubeLink,
    string? StorageFileKey,
    List<int> GroupIds,
    int? UnitId,
    int? LessonIndex,
    int? SchoolYear);

// PATCH Lectures/{id}
// body: { "name", "attendanceMethod", "schoolYear" } (partial update; all fields optional)
// "schoolYear" is mainly useful to backfill standalone lectures created before
// SchoolYear was required on them (see CreateLectureRequest note above).
public record UpdateLectureRequest(string? Name, string? AttendanceMethod, int? SchoolYear);

public record LectureListItem(
    int Id,
    string Name,
    string AttendanceMethod,
    string? Link,
    int? UnitId,
    int? LessonIndex,
    List<int> GroupIds,
    DateTime CreatedAt);

public record MaterialListItem(int Id, string Name, string Type, string Link);
