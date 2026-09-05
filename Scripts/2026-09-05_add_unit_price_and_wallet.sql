-- Adds:
--   1) an optional Price on Units (courses)
--   2) a points wallet per Student (cached WalletBalance + full WalletTransactions ledger)
--
-- Safe to run multiple times (IF NOT EXISTS everywhere).

-- 1) Optional course price -----------------------------------------------
ALTER TABLE "Units"
    ADD COLUMN IF NOT EXISTS "Price" numeric(10,2) NULL;

-- 2) Cached wallet balance on Student --------------------------------------
ALTER TABLE "Students"
    ADD COLUMN IF NOT EXISTS "WalletBalance" numeric(10,2) NOT NULL DEFAULT 0;

-- 3) Wallet transactions ledger --------------------------------------------
CREATE TABLE IF NOT EXISTS "WalletTransactions" (
    "Id" SERIAL PRIMARY KEY,
    "StudentId" integer NOT NULL,
    "Amount" numeric(10,2) NOT NULL,
    "BalanceAfter" numeric(10,2) NOT NULL,
    "Type" text NOT NULL DEFAULT 'manual',
    "Note" text NULL,
    "RelatedUnitId" integer NULL,
    "CreatedByStaffId" integer NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT (now() at time zone 'utc'),
    "TeacherId" integer NOT NULL,
    CONSTRAINT "FK_WalletTransactions_Students_StudentId"
        FOREIGN KEY ("StudentId") REFERENCES "Students" ("Id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS "IX_WalletTransactions_TeacherId" ON "WalletTransactions" ("TeacherId");
CREATE INDEX IF NOT EXISTS "IX_WalletTransactions_StudentId" ON "WalletTransactions" ("StudentId");
