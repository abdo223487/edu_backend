namespace EduApi.DTOs;

// POST Lectures
// body: { "name", "attendanceMethod": "Center"|"Online", "youtubeLink", "storageFileKey",
//         "groupIds": [..], "unitId": int?, "lessonIndex": int? }
public record CreateLectureRequest(
    string Name,
    string AttendanceMethod,
    string? YoutubeLink,
    string? StorageFileKey,
    List<int> GroupIds,
    int? UnitId,
    int? LessonIndex);

// PATCH Lectures/{id}
// body: { "name", "attendanceMethod" } (partial update; other fields optional)
public record UpdateLectureRequest(string? Name, string? AttendanceMethod);

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
