-- PLAN SWAPS: a teacher may swap features 1-for-1 inside his plan, up to the
-- plan's limit.
--  * FeaturePlans.SwapLimit : swaps the plan allows (0 = none)
--  * Teachers.SwapIn        : feature keys pulled INTO the plan (comma-separated)
--  * Teachers.SwapOut       : plan feature keys given up in exchange
--
-- Requires 2026-10-16_add_feature_plans_and_preview_accounts.sql (or its migration) first.
-- Safe to run multiple times. Matches the AddPlanSwaps migration exactly --
-- run ONE of the two against a given database, never both.

ALTER TABLE "FeaturePlans" ADD COLUMN IF NOT EXISTS "SwapLimit" integer NOT NULL DEFAULT 0;
ALTER TABLE "Teachers" ADD COLUMN IF NOT EXISTS "SwapIn" text NULL;
ALTER TABLE "Teachers" ADD COLUMN IF NOT EXISTS "SwapOut" text NULL;
