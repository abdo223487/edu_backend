-- FEATURE NOTES: text the SuperAdmin writes next to a feature so teachers can
-- read it in full (e.g. students -> "up to 500 students").
--  * FeatureNotes                  : GLOBAL note per feature key
--  * FeaturePlans.FeatureNotesJson : per-PLAN notes ({ key: text }), win over the global one
--
-- Requires 2026-10-17_add_plan_swaps.sql (or its migration) first.
-- Safe to run multiple times. Matches the AddFeatureNotes migration exactly --
-- run ONE of the two against a given database, never both.

CREATE TABLE IF NOT EXISTS "FeatureNotes" (
    "Key"  text NOT NULL PRIMARY KEY,
    "Note" text NOT NULL
);

ALTER TABLE "FeaturePlans" ADD COLUMN IF NOT EXISTS "FeatureNotesJson" text NULL;
