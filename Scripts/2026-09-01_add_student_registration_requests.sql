-- Self-registration flow: run manually on Neon (same convention as
-- 2026-07-09_add_student_group_memberships.sql).
CREATE TABLE IF NOT EXISTS "StudentRegistrationRequests" (
    "Id"                 SERIAL PRIMARY KEY,
    "Name"               TEXT NOT NULL,
    "PhoneNumber"        TEXT NOT NULL,
    "ParentPhoneNumber"  TEXT NOT NULL,
    "UserName"           TEXT NOT NULL,
    "PasswordHash"       TEXT NOT NULL,
    "TeacherId"          INTEGER NOT NULL REFERENCES "Teachers"("Id") ON DELETE CASCADE,
    "SchoolYear"         INTEGER NOT NULL,
    "GroupId"            INTEGER NOT NULL REFERENCES "Groups"("Id") ON DELETE CASCADE,
    "AccessCode"         TEXT NOT NULL,
    "Status"             TEXT NOT NULL DEFAULT 'Pending',
    "RejectionReason"    TEXT NULL,
    "CreatedStudentId"   INTEGER NULL REFERENCES "Students"("Id") ON DELETE SET NULL,
    "CreatedAt"          TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
    "ReviewedAt"         TIMESTAMP WITHOUT TIME ZONE NULL
);

CREATE INDEX IF NOT EXISTS "IX_StudentRegistrationRequests_TeacherId"
    ON "StudentRegistrationRequests" ("TeacherId");

CREATE INDEX IF NOT EXISTS "IX_StudentRegistrationRequests_Id_AccessCode"
    ON "StudentRegistrationRequests" ("Id", "AccessCode");

CREATE INDEX IF NOT EXISTS "IX_StudentRegistrationRequests_Status"
    ON "StudentRegistrationRequests" ("Status");
