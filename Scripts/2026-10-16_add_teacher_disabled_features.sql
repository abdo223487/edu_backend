-- Dashboard feature switches: the SuperAdmin can hide buttons on a teacher's
-- dashboard. Stored as a comma-separated list of feature keys
-- (see Common/TeacherFeatures.cs). NULL = everything visible.
--
-- Safe to run multiple times. Matches the AddTeacherDisabledFeatures migration
-- exactly -- run ONE of the two against a given database, never both.

ALTER TABLE "Teachers" ADD COLUMN IF NOT EXISTS "DisabledFeatures" text NULL;
