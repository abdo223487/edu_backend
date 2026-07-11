-- Multi-tenant student subscription: run manually on Neon.
CREATE TABLE IF NOT EXISTS "StudentGroupMemberships" (
    "Id"          SERIAL PRIMARY KEY,
    "StudentId"   INTEGER NOT NULL REFERENCES "Students"("Id") ON DELETE CASCADE,
    "GroupId"     INTEGER NOT NULL REFERENCES "Groups"("Id") ON DELETE CASCADE,
    "CreatedAt"   TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT (now() AT TIME ZONE 'utc')
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_StudentGroupMemberships_StudentId_GroupId"
    ON "StudentGroupMemberships" ("StudentId", "GroupId");

CREATE INDEX IF NOT EXISTS "IX_StudentGroupMemberships_GroupId"
    ON "StudentGroupMemberships" ("GroupId");

-- Backfill: كل طالب موجود ياخد membership تلقائي لفصله الحالي (صفر تغيير سلوك)
INSERT INTO "StudentGroupMemberships" ("StudentId", "GroupId")
SELECT s."Id", s."GroupId"
FROM "Students" s
WHERE NOT EXISTS (
    SELECT 1 FROM "StudentGroupMemberships" m
    WHERE m."StudentId" = s."Id" AND m."GroupId" = s."GroupId"
);
