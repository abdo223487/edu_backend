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
// body: { "name", "attendanceMethod", "schoolYear", "groupIds" } (partial update; all fields optional)
// "schoolYear" is mainly useful to backfill standalone lectures created before
// SchoolYear was required on them. Video source (file/YoutubeLink) is NOT
// editable here — delete and recreate the lecture to change its video.
// "groupIds", when present, REPLACES the lecture's entire group list (not a
// merge/append) -- same "whole list every time" contract as Create's
// GroupIds. Send the full desired list, not just the ones being added.
// "ViewLimit": when sent, replaces the lecture's view limit (Online lectures
// only -- ignored for Center). Send 0 or a negative number to CLEAR the
// limit (go back to unlimited views) since the field itself has to stay
// present/non-null to be read at all -- omit it entirely to leave the
// current limit untouched.
// "RequireLinkExam"/"RequireLinkAssignment" ("الربط"): when set, replace
// this lecture's link flags (Online lectures only -- ignored for Center).
// Each is independent; omit either to leave it untouched.
public record UpdateLectureRequest(string? Name, string? AttendanceMethod, int? SchoolYear, List<int>? GroupIds, int? ViewLimit = null, bool? RequireLinkExam = null, bool? RequireLinkAssignment = null);

// "Link" is whichever video source is actually playable right now: the R2
// file's public URL if the lecture has one, otherwise the YoutubeLink.
// "VideoSourceType" tells the client which player to use: "File" or
// "Youtube" (null for Center lectures, which have no video at all).
// "ThumbnailUrl" is only set for "File" lectures (a frame auto-extracted via
// ffmpeg at upload time, stored in R2). For "Youtube" lectures the client
// should keep building the thumbnail itself from the YouTube video id
// (img.youtube.com/vi/{id}/0.jpg) exactly like before — this field is null there.
// "ViewLimit" is null for an unlimited-views lecture (every lecture before
// this feature, and every Center lecture). "RemainingViews" is ONLY
// populated when this DTO is being returned to the student themselves (in
// student-facing endpoints like ByGroup/ByYear) AND ViewLimit is set --
// null in every other case (teacher-facing endpoints, or an unlimited
// lecture). It already accounts for that one student's ExtraViews
// grant/revoke, so the client never needs to combine the two fields itself.
public record LectureListItem(
    int Id,
    string Name,
    string AttendanceMethod,
    string? Link,
    string? VideoSourceType,
    string? ThumbnailUrl,
    int? UnitId,
    int? LessonIndex,
    List<int> GroupIds,
    DateTime CreatedAt,
    int? ExternalBookId = null,
    int? ViewLimit = null,
    int? RemainingViews = null,
    bool RequireLinkExam = false,
    bool RequireLinkAssignment = false,
    // Only ever computed for a STUDENT-facing response. True when this
    // lecture has RequireLinkExam and/or RequireLinkAssignment set AND the
    // requesting student hasn't yet finished the required exam/assignment
    // on the previous Online lecture in the same context -- in that case
    // Link/VideoSourceType/ThumbnailUrl above are forced to null so the
    // client can't play the video, and LockReason explains why. Always
    // false (with a null LockReason) for teacher/staff-facing responses,
    // and for the first lecture in a context regardless of its flags.
    bool Locked = false,
    string? LockReason = null);

public record MaterialListItem(int Id, string Name, string Type, string Link);

// GET Lectures/{id}/consume-view response.
// "Allowed" is false when the student has no views left -- the client
// should NOT open the player in that case, and should surface Message.
// Allowed/RemainingViews/Message as before. SessionId is set only when this
// call just started a NEW view of a File-sourced lecture (see
// LecturesController.ConsumeView) -- the client hangs onto it and passes it
// back to ReportViewProgress as it plays, so the teacher's "views" screen
// can show where this particular view stopped. Null for Youtube lectures
// (no in-app player to report a position from) and for the read-only
// view-status check.
public record ConsumeViewResult(bool Allowed, int? RemainingViews, string? Message, int? SessionId = null);

// POST Lectures/{id}/view-progress body -- SessionId from ConsumeViewResult,
// PositionSeconds is the student's current playhead position. Call this
// periodically (e.g. every 10-15s) and once more right when the player
// closes/pauses/the app backgrounds, so a session that never reaches
// natural "finished" still has its last known position saved.
public record ReportViewProgressRequest(int SessionId, int PositionSeconds);

// GET Lectures/student-views?studentId=.. (teacher) — one row per Online
// lecture that has a ViewLimit AND is reachable by this student (subscribed
// unit/book, group-linked, or individually unlocked), regardless of whether
// they've actually opened it yet.
public record StudentLectureViewItem(
    int LectureId,
    string LectureName,
    int ViewLimit,
    int ViewsUsed,
    int ExtraViews,
    int RemainingViews);

// POST Lectures/student-views/adjust (teacher)
// "Delta" is added to that ONE student's remaining views for that lecture
// (negative to take views away). Only ever touches ExtraViews -- the
// lecture's own ViewLimit (and every other student's remaining count) is
// left alone.
public record AdjustStudentViewsRequest(int StudentId, int LectureId, int Delta);

// GET Lectures/{id}/viewers?p=..&q=.. (teacher) — one row per student who can
// reach this ONE lecture, paged/searched like Students. Status is one of
// "NotViewed" (never opened), "Partial" (opened, views remain), "Full" (used
// every allowed view), or "Watched" (opened at least once, lecture has no
// ViewLimit so there's nothing to run out of).
// GET Lectures/{id}/viewers?p=..&q=.. (teacher) — one row per student who can
// reach this ONE lecture, paged/searched like Students. Status is one of
// "NotViewed" (never opened), "Partial" (opened, views remain), "Full" (used
// every allowed view), or "Watched" (opened at least once, lecture has no
// ViewLimit so there's nothing to run out of). Sessions is only ever
// populated when the lecture has a ViewLimit (see LectureViewersResponse.
// TracksProgress) -- one entry per individual view, each with where in the
// video that particular view stopped (null if the student closed the player
// before ever reporting a position). Works for both File and Youtube
// lectures now -- both players report a position (see ReportViewProgress).
public record LectureViewerSessionItem(int? StoppedAtSeconds, DateTime CreatedAt);

public record LectureViewerItem(
    int StudentId,
    string Name,
    string? PhoneNumber,
    string GroupName,
    int ViewsUsed,
    int? ViewLimit,
    int? ExtraViews,
    int? RemainingViews,
    string Status,
    List<LectureViewerSessionItem>? Sessions);

public record LectureViewersResponse(
    int LectureId,
    string LectureName,
    int? ViewLimit,
    bool TracksProgress,
    List<LectureViewerItem> Students);
