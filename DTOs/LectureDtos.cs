namespace EduApi.DTOs;

// POST Lectures (multipart/form-data)
// fields: Name, AttendanceMethod ("Center"|"Online"), GroupIds[i], UnitId?,
//         LessonIndex?, SchoolYear?, YoutubeLink? (text field)
// file:   VideoFile (optional, .mp4) — mutually exclusive with YoutubeLink.
// "schoolYear" is required when unitId is omitted (standalone/no-unit lecture) —
// it's ignored when unitId is set, since the Unit's own SchoolYear is used instead.
//
// Online lectures need EXACTLY ONE video source: either a recorded file
// (VideoFile, uploaded straight to Cloudflare R2 the same way quiz-question
// images and lecture materials already are) or a YoutubeLink. Never both,
// never neither.

// PATCH Lectures/{id}
// body: { "name", "attendanceMethod", "schoolYear" } (partial update; all fields optional)
// "schoolYear" is mainly useful to backfill standalone lectures created before
// SchoolYear was required on them. Video source (file/YoutubeLink) is NOT
// editable here — delete and recreate the lecture to change its video.
public record UpdateLectureRequest(string? Name, string? AttendanceMethod, int? SchoolYear);

// "Link" is whichever video source is actually playable right now: the R2
// file's public URL if the lecture has one, otherwise the YoutubeLink.
// "VideoSourceType" tells the client which player to use: "File" or
// "Youtube" (null for Center lectures, which have no video at all).
public record LectureListItem(
    int Id,
    string Name,
    string AttendanceMethod,
    string? Link,
    string? VideoSourceType,
    int? UnitId,
    int? LessonIndex,
    List<int> GroupIds,
    DateTime CreatedAt);

public record MaterialListItem(int Id, string Name, string Type, string Link);
