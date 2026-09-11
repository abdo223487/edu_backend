-- Per-student teacher overrides for LectureExams and LectureAssignments
-- (force-review / reopen from the teacher's "امتحانات الحصص" / "واجبات
-- الحصص" quick actions on a student's own details page) -- same shape as
-- AssignmentStudentOverrides/QuizStudentOverrides, run manually on Neon if
-- `dotnet ef database update` can't be run in this environment.

CREATE TABLE IF NOT EXISTS "LectureExamStudentOverrides" (
    "Id"              SERIAL PRIMARY KEY,
    "LectureExamId"   INTEGER NOT NULL,
    "StudentId"       INTEGER NOT NULL,
    "TeacherId"       INTEGER NOT NULL,
    "ForceReview"     BOOLEAN NOT NULL DEFAULT FALSE,
    "ReopenExpiresAt" TIMESTAMP WITH TIME ZONE NULL,
    "CreatedAt"       TIMESTAMP WITH TIME ZONE NOT NULL
);

CREATE TABLE IF NOT EXISTS "LectureAssignmentStudentOverrides" (
    "Id"                    SERIAL PRIMARY KEY,
    "LectureAssignmentId"   INTEGER NOT NULL,
    "StudentId"             INTEGER NOT NULL,
    "TeacherId"             INTEGER NOT NULL,
    "ForceReview"           BOOLEAN NOT NULL DEFAULT FALSE,
    "ReopenExpiresAt"       TIMESTAMP WITH TIME ZONE NULL,
    "CreatedAt"             TIMESTAMP WITH TIME ZONE NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_LectureExamStudentOverrides_LectureExamId_StudentId"
    ON "LectureExamStudentOverrides" ("LectureExamId", "StudentId");
CREATE INDEX IF NOT EXISTS "IX_LectureExamStudentOverrides_TeacherId" ON "LectureExamStudentOverrides" ("TeacherId");

CREATE UNIQUE INDEX IF NOT EXISTS "IX_LectureAssignmentStudentOverrides_LectureAssignmentId_StudentId"
    ON "LectureAssignmentStudentOverrides" ("LectureAssignmentId", "StudentId");
CREATE INDEX IF NOT EXISTS "IX_LectureAssignmentStudentOverrides_TeacherId" ON "LectureAssignmentStudentOverrides" ("TeacherId");
