-- Class schedule as a real calendar ("مواعيد الحصص"): one row per specific
-- date that has a lesson. Replaces the old weekly ClassScheduleDays table
-- (left untouched/unused in the database, harmless to keep).
-- Safe to run multiple times (IF NOT EXISTS everywhere).

CREATE TABLE IF NOT EXISTS "ClassScheduleEntries" (
    "Id" SERIAL PRIMARY KEY,
    "SchoolYear" integer NOT NULL,
    "Date" date NOT NULL,
    "Text" text NULL,
    "TeacherId" integer NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_ClassScheduleEntries_TeacherId" ON "ClassScheduleEntries" ("TeacherId");

CREATE UNIQUE INDEX IF NOT EXISTS "IX_ClassScheduleEntries_TeacherId_SchoolYear_Date"
    ON "ClassScheduleEntries" ("TeacherId", "SchoolYear", "Date");
