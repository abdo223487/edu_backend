-- Lecture Assignments feature (homework counterpart to LectureExams, no
-- duration/timer): run manually on Neon if `dotnet ef database update`
-- can't be run in this environment. Matches Migrations/
-- 20260903120000_AddLectureAssignments.cs exactly -- run ONE of the two,
-- never both.

CREATE TABLE IF NOT EXISTS "LectureAssignments" (
    "Id"        SERIAL PRIMARY KEY,
    "Title"     TEXT NOT NULL,
    "LectureId" INTEGER NOT NULL,
    "TeacherId" INTEGER NOT NULL,
    "CreatedAt" TIMESTAMP WITH TIME ZONE NOT NULL
);

CREATE TABLE IF NOT EXISTS "LectureAssignmentQuestions" (
    "Id"                    SERIAL PRIMARY KEY,
    "LectureAssignmentId"   INTEGER NOT NULL REFERENCES "LectureAssignments" ("Id") ON DELETE CASCADE,
    "Type"                  TEXT NOT NULL,
    "Text"                  TEXT NOT NULL,
    "Answer"                TEXT NOT NULL,
    "Mark"                  INTEGER NOT NULL,
    "ChoicesCsv"            TEXT NOT NULL,
    "ImageUrl"              TEXT NULL
);

CREATE TABLE IF NOT EXISTS "LectureAssignmentResults" (
    "Id"                    SERIAL PRIMARY KEY,
    "LectureAssignmentId"   INTEGER NOT NULL,
    "StudentId"             INTEGER NOT NULL,
    "Score"                 INTEGER NOT NULL,
    "TotalMarks"            INTEGER NOT NULL,
    "GradedAt"              TIMESTAMP WITH TIME ZONE NOT NULL,
    "TeacherId"             INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS "LectureAssignmentAnswers" (
    "Id"                        SERIAL PRIMARY KEY,
    "LectureAssignmentResultId" INTEGER NOT NULL REFERENCES "LectureAssignmentResults" ("Id") ON DELETE CASCADE,
    "QuestionId"                INTEGER NOT NULL,
    "Answer"                    TEXT NOT NULL,
    "MarkAwarded"               INTEGER NULL
);

CREATE INDEX IF NOT EXISTS "IX_LectureAssignments_TeacherId" ON "LectureAssignments" ("TeacherId");
CREATE INDEX IF NOT EXISTS "IX_LectureAssignments_LectureId" ON "LectureAssignments" ("LectureId");
CREATE INDEX IF NOT EXISTS "IX_LectureAssignmentQuestions_LectureAssignmentId" ON "LectureAssignmentQuestions" ("LectureAssignmentId");

CREATE UNIQUE INDEX IF NOT EXISTS "IX_LectureAssignmentResults_LectureAssignmentId_StudentId"
    ON "LectureAssignmentResults" ("LectureAssignmentId", "StudentId");
CREATE INDEX IF NOT EXISTS "IX_LectureAssignmentResults_TeacherId" ON "LectureAssignmentResults" ("TeacherId");

CREATE INDEX IF NOT EXISTS "IX_LectureAssignmentAnswers_LectureAssignmentResultId"
    ON "LectureAssignmentAnswers" ("LectureAssignmentResultId");
