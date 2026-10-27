-- Lecture Q&A: optional image on a question / reply.
-- Idempotent. Not required to run by hand: the migration 20261027190000_AddLectureCommentImage
-- applies the same statement automatically on server start.
ALTER TABLE "LectureComments" ADD COLUMN IF NOT EXISTS "ImageUrl" character varying(500) NULL;
