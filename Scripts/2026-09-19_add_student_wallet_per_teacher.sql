-- BUGFIX (cross-tenant wallet leak): Student.WalletBalance was a single
-- column shared by every teacher a student is linked to -- points a teacher
-- added were visible and spendable under any OTHER teacher too.
--
-- Creates StudentWallets: one row per (StudentId, TeacherId), and backfills
-- each student's existing WalletBalance into the row for their LEGACY
-- Group's teacher (the only teacher a pre-multi-tenant balance could
-- actually be attributed to).
--
-- Student.WalletBalance itself is left in place, unused -- not dropped --
-- so this script is non-destructive and safe to run alongside a rolling
-- deploy. WalletController no longer reads or writes it.
--
-- Safe to run multiple times (IF NOT EXISTS everywhere); the backfill INSERT
-- is NOT re-run-safe on its own though -- see the guard below.

CREATE TABLE IF NOT EXISTS "StudentWallets" (
    "Id" SERIAL PRIMARY KEY,
    "StudentId" integer NOT NULL,
    "TeacherId" integer NOT NULL,
    "Balance" numeric(10,2) NOT NULL DEFAULT 0,
    CONSTRAINT "FK_StudentWallets_Students_StudentId"
        FOREIGN KEY ("StudentId") REFERENCES "Students" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_StudentWallets_StudentId_TeacherId"
    ON "StudentWallets" ("StudentId", "TeacherId");

CREATE INDEX IF NOT EXISTS "IX_StudentWallets_TeacherId" ON "StudentWallets" ("TeacherId");

-- ONE-TIME BACKFILL -- guarded so re-running this script doesn't insert
-- duplicate rows (the unique index above would reject them anyway, but this
-- keeps the script idempotent/re-runnable without erroring).
INSERT INTO "StudentWallets" ("StudentId", "TeacherId", "Balance")
SELECT s."Id", g."TeacherId", s."WalletBalance"
FROM "Students" s
JOIN "Groups" g ON g."Id" = s."GroupId"
WHERE s."WalletBalance" <> 0
  AND NOT EXISTS (
      SELECT 1 FROM "StudentWallets" w
      WHERE w."StudentId" = s."Id" AND w."TeacherId" = g."TeacherId"
  );
