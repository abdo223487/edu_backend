-- Lecture view-limit feature: run manually on Neon if `dotnet ef database
-- update` can't be run in this environment. Matches Migrations/
-- 20260901000000_AddLectureViewLimits.cs exactly -- run ONE of the two,
-- never both.

ALTER TABLE "Lectures" ADD COLUMN IF NOT EXISTS "ViewLimit" INTEGER NULL;

CREATE TABLE IF NOT EXISTS "StudentLectureViewUsages" (
    "Id"           SERIAL PRIMARY KEY,
    "TeacherId"    INTEGER NOT NULL,
    "StudentId"    INTEGER NOT NULL,
    "LectureId"    INTEGER NOT NULL,
    "ViewsUsed"    INTEGER NOT NULL DEFAULT 0,
    "ExtraViews"   INTEGER NOT NULL DEFAULT 0,
    "LastViewedAt" TIMESTAMP WITH TIME ZONE NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_StudentLectureViewUsages_StudentId_LectureId"
    ON "StudentLectureViewUsages" ("StudentId", "LectureId");

CREATE INDEX IF NOT EXISTS "IX_StudentLectureViewUsages_TeacherId"
    ON "StudentLectureViewUsages" ("TeacherId");

-- If you run this script, also tell EF Core the migration is "applied" so
-- `dotnet ef database update` doesn't try to run it again later:
-- INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
-- VALUES ('20260901000000_AddLectureViewLimits', '9.0.0');
