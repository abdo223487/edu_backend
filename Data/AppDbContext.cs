using EduApi.Common;
using EduApi.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace EduApi.Data;

public class AppDbContext : DbContext
{
    private readonly ITenantContext _tenant;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant, IHttpContextAccessor? httpContextAccessor = null) : base(options)
    {
        _tenant = tenant;
        _httpContextAccessor = httpContextAccessor;
    }

    public DbSet<Teacher> Teachers => Set<Teacher>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<OnlineLesson> OnlineLessons => Set<OnlineLesson>();
    public DbSet<StudentOnlineLessonUnlock> StudentOnlineLessonUnlocks => Set<StudentOnlineLessonUnlock>();
    public DbSet<StudentUnitSubscription> StudentUnitSubscriptions => Set<StudentUnitSubscription>();
    public DbSet<StudentGroupMembership> StudentGroupMemberships => Set<StudentGroupMembership>();
    public DbSet<StudentLectureUnlock> StudentLectureUnlocks => Set<StudentLectureUnlock>();
    public DbSet<Lecture> Lectures => Set<Lecture>();
    public DbSet<Material> Materials => Set<Material>();
    public DbSet<Notebook> Notebooks => Set<Notebook>();
    public DbSet<NotebookPayment> NotebookPayments => Set<NotebookPayment>();
    public DbSet<Billing> Billings => Set<Billing>();
    public DbSet<BillingPayment> BillingPayments => Set<BillingPayment>();
    public DbSet<Code> Codes => Set<Code>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Attendance> Attendances => Set<Attendance>();
    public DbSet<Dismissal> Dismissals => Set<Dismissal>();
    public DbSet<Quiz> Quizzes => Set<Quiz>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<QuizResult> QuizResults => Set<QuizResult>();
    public DbSet<QuizAnswer> QuizAnswers => Set<QuizAnswer>();
    public DbSet<QuizStudentOverride> QuizStudentOverrides => Set<QuizStudentOverride>();
    public DbSet<LectureExam> LectureExams => Set<LectureExam>();
    public DbSet<LectureExamQuestion> LectureExamQuestions => Set<LectureExamQuestion>();
    public DbSet<LectureExamResult> LectureExamResults => Set<LectureExamResult>();
    public DbSet<LectureExamAnswer> LectureExamAnswers => Set<LectureExamAnswer>();
    public DbSet<LectureExamStudentStart> LectureExamStudentStarts => Set<LectureExamStudentStart>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<AssignmentQuestion> AssignmentQuestions => Set<AssignmentQuestion>();
    public DbSet<AssignmentSubmission> AssignmentSubmissions => Set<AssignmentSubmission>();
    public DbSet<AssignmentAnswer> AssignmentAnswers => Set<AssignmentAnswer>();
    public DbSet<AssignmentStudentOverride> AssignmentStudentOverrides => Set<AssignmentStudentOverride>();
    public DbSet<AssignmentCenter> AssignmentCenters => Set<AssignmentCenter>();
    public DbSet<AssignmentCenterQuestion> AssignmentCenterQuestions => Set<AssignmentCenterQuestion>();
    public DbSet<AssignmentCenterSubmission> AssignmentCenterSubmissions => Set<AssignmentCenterSubmission>();
    public DbSet<AssignmentCenterAnswer> AssignmentCenterAnswers => Set<AssignmentCenterAnswer>();
    public DbSet<BankQuestion> BankQuestions => Set<BankQuestion>();
    public DbSet<BankAttempt> BankAttempts => Set<BankAttempt>();
    public DbSet<BankAttemptQuestion> BankAttemptQuestions => Set<BankAttemptQuestion>();
    public DbSet<CenterQuizResult> CenterQuizResults => Set<CenterQuizResult>();
    public DbSet<HomeworkResult> HomeworkResults => Set<HomeworkResult>();
    public DbSet<StateHistoryEntry> StateHistoryEntries => Set<StateHistoryEntry>();
    public DbSet<AppVersion> AppVersions => Set<AppVersion>();
    public DbSet<RequestErrorLog> RequestErrorLogs => Set<RequestErrorLog>();
    public DbSet<DeletedItemLog> DeletedItemLogs => Set<DeletedItemLog>();

    // Real join tables backing the *IdsCsv columns' filtering. See the
    // SaveChangesAsync override below for how these stay in sync with
    // Lecture/Assignment/Notification/Quiz.GroupIds (and Assignment.UnitIds).
    public DbSet<LectureGroupLink> LectureGroupLinks => Set<LectureGroupLink>();
    public DbSet<AssignmentGroupLink> AssignmentGroupLinks => Set<AssignmentGroupLink>();
    public DbSet<AssignmentUnitLink> AssignmentUnitLinks => Set<AssignmentUnitLink>();
    public DbSet<AssignmentCenterGroupLink> AssignmentCenterGroupLinks => Set<AssignmentCenterGroupLink>();
    public DbSet<AssignmentCenterUnitLink> AssignmentCenterUnitLinks => Set<AssignmentCenterUnitLink>();
    public DbSet<NotificationGroupLink> NotificationGroupLinks => Set<NotificationGroupLink>();
    public DbSet<QuizGroupLink> QuizGroupLinks => Set<QuizGroupLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Student>()
            .HasIndex(s => s.PhoneNumber);

        modelBuilder.Entity<Student>()
            .HasOne(s => s.Group)
            .WithMany(g => g.Students)
            .HasForeignKey(s => s.GroupId)
            .OnDelete(DeleteBehavior.Restrict);

        // ═══════════════════════════════════════════════════════════════
        // TENANT LAYER — global query filters
        // Every "tenant-owned" entity is automatically scoped to
        // ITenantContext.CurrentTenantId for every query (reads AND
        // writes/deletes go through the same filtered DbSet), with zero
        // per-controller filtering code required. When CurrentTenantId is
        // null (no auth / student missing X-TenantId header), every one of
        // these filters matches nothing — the safe default.
        //
        // Login/refresh explicitly opt out via IgnoreQueryFilters() where
        // they must look up a Student before any tenant is known yet.
        // ═══════════════════════════════════════════════════════════════
        modelBuilder.Entity<Group>().HasQueryFilter(g => g.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<Unit>().HasQueryFilter(u => u.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<OnlineLesson>().HasQueryFilter(o => o.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<Lecture>().HasQueryFilter(l => l.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<Material>().HasQueryFilter(m => m.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<Notebook>().HasQueryFilter(n => n.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<Billing>().HasQueryFilter(b => b.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<Code>().HasQueryFilter(c => c.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<Notification>().HasQueryFilter(n => n.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<Quiz>().HasQueryFilter(q => q.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<Assignment>().HasQueryFilter(a => a.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<AssignmentCenter>().HasQueryFilter(a => a.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<BankQuestion>().HasQueryFilter(bq => bq.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<BankAttempt>().HasQueryFilter(ba => ba.TeacherId == _tenant.CurrentTenantId);

        // PERFORMANCE: every entity above (and the three result tables + five
        // activity tables filtered below) is scoped to the current tenant via
        // HasQueryFilter, which means EVERY query against them carries an
        // implicit "WHERE TeacherId = @tenant" -- with no index, Postgres has
        // no choice but a full sequential scan on every single request. These
        // indexes turn that into an index scan and cost is negligible on
        // writes. See AddTeacherIdIndexesForTenantIsolation migration.
        modelBuilder.Entity<Group>().HasIndex(g => g.TeacherId);
        modelBuilder.Entity<Unit>().HasIndex(u => u.TeacherId);
        modelBuilder.Entity<Lecture>().HasIndex(l => l.TeacherId);
        modelBuilder.Entity<Material>().HasIndex(m => m.TeacherId);
        modelBuilder.Entity<Notebook>().HasIndex(n => n.TeacherId);
        modelBuilder.Entity<Billing>().HasIndex(b => b.TeacherId);
        modelBuilder.Entity<Code>().HasIndex(c => c.TeacherId);
        // Every attendance record now looks up Codes by TriggerLectureId
        // (IssueTriggeredCodesAsync) — same performance reasoning as the
        // TeacherId indexes above.
        modelBuilder.Entity<Code>().HasIndex(c => c.TriggerLectureId);
        // RACE-CONDITION FIX: same class of bug as the Attendance unique
        // index below. IssueTriggeredCodesAsync's "already issued?" AnyAsync
        // check (SourceCodeTemplateId + UsedByStudentId) is not atomic, so
        // two near-simultaneous attendance requests for the same student
        // (double scan, client retry, offline-sync replay) can both pass it
        // before either INSERT commits, minting two redeemed codes -- and
        // therefore double-granting whatever units/lectures/online-lessons
        // that template unlocks -- for the same student. Both columns are
        // nullable (a Code can be a plain non-template, unredeemed code, in
        // which case both are null), so this MUST be a filtered/partial
        // index -- Postgres treats every NULL as distinct, so an unfiltered
        // unique index here would silently allow unlimited rows where both
        // columns are null instead of actually enforcing "one issued code
        // per template per student".
        modelBuilder.Entity<Code>()
            .HasIndex(c => new { c.SourceCodeTemplateId, c.UsedByStudentId })
            .IsUnique()
            .HasFilter("\"SourceCodeTemplateId\" IS NOT NULL AND \"UsedByStudentId\" IS NOT NULL");
        // RACE-CONDITION FIX: CodeGenerator.GenerateUniqueAsync only checked
        // "is this random value already taken?" via AnyAsync before
        // returning it -- not atomic, so two near-simultaneous generate
        // calls (a teacher clicking Generate twice, or two attendance
        // records triggering a code mint at the same instant) could both
        // land on the same 8-char candidate and both pass the check before
        // either INSERT commits. Codes.Value is looked up directly during
        // redemption (StudentsController: c.Value == request.Code) with no
        // other disambiguator, so a duplicate here isn't just a wasted row
        // -- it can hand one student's code to whichever row redemption
        // happens to match first, silently misdirecting the unlock. No
        // nullability concern here (Value is always set), so this is a
        // plain unique index, no filter needed.
        modelBuilder.Entity<Code>().HasIndex(c => c.Value).IsUnique();
        modelBuilder.Entity<Notification>().HasIndex(n => n.TeacherId);
        modelBuilder.Entity<Quiz>().HasIndex(q => q.TeacherId);
        modelBuilder.Entity<Assignment>().HasIndex(a => a.TeacherId);
        modelBuilder.Entity<AssignmentCenter>().HasIndex(a => a.TeacherId);
        modelBuilder.Entity<BankQuestion>().HasIndex(bq => bq.TeacherId);
        modelBuilder.Entity<BankAttempt>().HasIndex(ba => ba.TeacherId);

        // MULTI-TENANT SECURITY FIX: these three results tables are queried by
        // StudentId ALONE in StudentsController (they don't join through Quiz),
        // so without their own TeacherId + filter, a student subscribed to more
        // than one teacher would have marks from every teacher mixed together
        // whichever tenant asked. The Student-level filter below does NOT cover
        // these because they're queried directly off their own DbSet, not via
        // Student.
        modelBuilder.Entity<QuizResult>().HasQueryFilter(qr => qr.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<QuizStudentOverride>().HasQueryFilter(o => o.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<AssignmentStudentOverride>().HasQueryFilter(o => o.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<LectureExam>().HasQueryFilter(le => le.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<LectureExamResult>().HasQueryFilter(lr => lr.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<LectureExamStudentStart>().HasQueryFilter(ls => ls.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<CenterQuizResult>().HasQueryFilter(cr => cr.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<HomeworkResult>().HasQueryFilter(hr => hr.TeacherId == _tenant.CurrentTenantId);

        // Composite (not just TeacherId alone): every read of these three
        // tables filters by TeacherId (query filter) AND StudentId (explicit,
        // "this student's result history") together -- see
        // AnalyticsController/StudentsController/QuizzesController. A single
        // composite index serves both the tenant-only filter (leftmost
        // column) and the common TeacherId+StudentId combination, instead of
        // needing two separate indexes.
        modelBuilder.Entity<QuizResult>().HasIndex(qr => new { qr.TeacherId, qr.StudentId });
        // At most one override row per (quiz, student) / (assignment, student) —
        // ForceReview/ReopenExpiresAt are upserted onto the same row, never
        // duplicated. Same race-condition guard as QuizResult below.
        modelBuilder.Entity<QuizStudentOverride>().HasIndex(o => new { o.QuizId, o.StudentId }).IsUnique();
        modelBuilder.Entity<QuizStudentOverride>().HasIndex(o => o.TeacherId);
        modelBuilder.Entity<AssignmentStudentOverride>().HasIndex(o => new { o.AssignmentId, o.StudentId }).IsUnique();
        modelBuilder.Entity<AssignmentStudentOverride>().HasIndex(o => o.TeacherId);
        // RACE-CONDITION FIX: QuizzesController.Grade's "already submitted?"
        // AnyAsync check was never atomic with its later INSERT, so two
        // near-simultaneous grade submissions (double-tap, client retry, or
        // -- worse -- a student deliberately re-submitting after seeing
        // correct answers from a first attempt) could both land as separate
        // QuizResult rows for the same student+quiz. The database itself
        // now refuses a second row outright, regardless of timing or
        // whether the app-level check was skipped/bypassed.
        modelBuilder.Entity<QuizResult>().HasIndex(qr => new { qr.QuizId, qr.StudentId }).IsUnique();
        // Same race-condition guard as QuizResult above.
        modelBuilder.Entity<LectureExamResult>().HasIndex(lr => new { lr.LectureExamId, lr.StudentId }).IsUnique();
        modelBuilder.Entity<LectureExamResult>().HasIndex(lr => lr.TeacherId);
        modelBuilder.Entity<LectureExamStudentStart>().HasIndex(ls => new { ls.LectureExamId, ls.StudentId }).IsUnique();
        modelBuilder.Entity<LectureExamStudentStart>().HasIndex(ls => ls.TeacherId);
        modelBuilder.Entity<LectureExam>().HasIndex(le => le.TeacherId);
        modelBuilder.Entity<LectureExam>().HasIndex(le => le.LectureId);
        modelBuilder.Entity<CenterQuizResult>().HasIndex(cr => new { cr.TeacherId, cr.StudentId });
        modelBuilder.Entity<HomeworkResult>().HasIndex(hr => new { hr.TeacherId, hr.StudentId });

        // MULTI-TENANT SECURITY HARDENING: these tables never had their own
        // TeacherId before -- every current call site happens to be safe
        // today because it joins against an already tenant-filtered table
        // (Lecture/Assignment/Notebook/Unit) before touching these, but
        // nothing enforced that at the database level. Same class of bug as
        // the QuizResult/CenterQuizResult/HomeworkResult fix above: any
        // future endpoint that queries these by StudentId alone would leak
        // rows across teachers. Adding TeacherId + a global filter here
        // closes that off structurally instead of relying on every call
        // site remembering to join correctly.
        modelBuilder.Entity<Attendance>().HasQueryFilter(a => a.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<Dismissal>().HasQueryFilter(d => d.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<AssignmentSubmission>().HasQueryFilter(s => s.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<AssignmentCenterSubmission>().HasQueryFilter(s => s.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<NotebookPayment>().HasQueryFilter(p => p.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<BillingPayment>().HasQueryFilter(p => p.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<StudentLectureUnlock>().HasQueryFilter(u => u.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<StudentOnlineLessonUnlock>().HasQueryFilter(u => u.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<StudentUnitSubscription>().HasQueryFilter(s => s.TeacherId == _tenant.CurrentTenantId);

        modelBuilder.Entity<Attendance>().HasIndex(a => a.TeacherId);
        // RACE-CONDITION FIX: the app-level "already recorded?" AnyAsync
        // check in AttendanceController is not atomic -- two near-
        // simultaneous requests for the same student+lecture (double QR
        // scan, client retry after a timeout, offline-sync replay) can both
        // pass that check before either one's SaveChangesAsync commits,
        // producing two Attendance rows for the same student in the same
        // lecture. This unique index makes the database itself the source
        // of truth and reject the second insert outright, regardless of
        // timing -- the AnyAsync check stays in place as a fast, friendly
        // first line of defense (clear error message), this index is what
        // actually guarantees no duplicate ever lands in the table.
        modelBuilder.Entity<Attendance>().HasIndex(a => new { a.LectureId, a.StudentId }).IsUnique();
        modelBuilder.Entity<AssignmentSubmission>().HasIndex(s => s.TeacherId);
        modelBuilder.Entity<AssignmentCenterSubmission>().HasIndex(s => s.TeacherId);
        // RACE-CONDITION FIX: same class of bug as the QuizResult fix above
        // -- AssignmentCentersController.Submit's AnyAsync check + later
        // INSERT aren't atomic either. Its app-level check DOES correctly
        // reject re-submission, but that check alone still can't stop two
        // truly-simultaneous requests from both passing it before either
        // commits. This index is the actual guarantee.
        modelBuilder.Entity<AssignmentCenterSubmission>()
            .HasIndex(s => new { s.AssignmentCenterId, s.StudentId }).IsUnique();
        modelBuilder.Entity<NotebookPayment>().HasIndex(p => p.TeacherId);
        modelBuilder.Entity<BillingPayment>().HasIndex(p => p.TeacherId);
        modelBuilder.Entity<StudentLectureUnlock>().HasIndex(u => u.TeacherId);
        modelBuilder.Entity<StudentUnitSubscription>().HasIndex(s => s.TeacherId);
        modelBuilder.Entity<StudentOnlineLessonUnlock>().HasIndex(u => u.TeacherId);

        // MULTI-TENANT MEMBERSHIP: a Student can belong to Groups under MORE THAN
        // ONE teacher now (StudentGroupMembership). A Student row is visible under
        // the current tenant if EITHER their legacy single Group belongs to this
        // tenant OR they have a membership row for a Group under this tenant.
        // NOTE: this filter only applies when querying the Students DbSet itself
        // (or navigating via a Student navigation property). It does NOT cascade
        // to result tables (QuizResult/CenterQuizResult/HomeworkResult) queried
        // directly off their own DbSet by StudentId — those need their own
        // TeacherId + filter, added below.
        //
        // BUGFIX: a student can also be tied to a SECOND teacher purely through
        // Students/codes (code redemption) — that flow only ever wrote
        // StudentUnitSubscription / StudentLectureUnlock rows for the redeeming
        // teacher, it never created a StudentGroupMembership row. Such a student
        // was fully functional for that teacher everywhere else (units, lectures,
        // quizzes...) but was INVISIBLE to this filter for that tenant, because
        // neither the legacy Group nor GroupMemberships matched — so any endpoint
        // that first does `_db.Students...FirstOrDefaultAsync(s => s.Id == id)`
        // for that teacher (e.g. GET Students/attendance, or scanning the
        // student's QR for POST Attendance) 404'd with "Student not found",
        // while the teacher who originally added them via Students/Create or
        // Students/link (and so does have a GroupMembership row) worked fine.
        // Now a student redeemed into a tenant via a unit subscription or a
        // lecture unlock also counts as "visible" under that tenant.
        // NOTE: every branch below is written as an explicit Set<T>() correlated
        // EXISTS subquery — including the legacy Group branch — and NONE of them
        // touch a Student navigation property. This is not a style choice: it was
        // the actual root cause of "works for the first teacher, 404s for every
        // other teacher". `s.Group != null && s.Group.TeacherId == tenant` gets
        // translated by EF Core into an INNER JOIN against Groups (itself already
        // filtered to the current tenant) on s.GroupId == g.Id. Because it's an
        // INNER JOIN — not a LEFT JOIN — a student whose LEGACY GroupId belongs to
        // a DIFFERENT tenant gets dropped from the result set by the JOIN itself,
        // before the OR'd EXISTS clauses (GroupMemberships / UnitSubscriptions /
        // LectureUnlocks) ever get a chance to run. Confirmed from the generated
        // SQL:
        //   FROM "Students" AS s
        //   INNER JOIN (SELECT ... FROM "Groups" WHERE "TeacherId" = @tenant) AS g0
        //       ON s."GroupId" = g0."Id"
        //   WHERE (g0."TeacherId" = @tenant OR EXISTS(...) OR EXISTS(...) OR EXISTS(...))
        // Rewriting the legacy check as its own Set<Group>().Any(...) EXISTS
        // subquery (instead of s.Group.TeacherId) keeps it a plain OR'd EXISTS,
        // same as the other three branches, with no JOIN involved at all.
        modelBuilder.Entity<Student>()
            .HasQueryFilter(s =>
                Set<Group>().Any(g => g.Id == s.GroupId && g.TeacherId == _tenant.CurrentTenantId) ||
                Set<StudentGroupMembership>().Any(m => m.StudentId == s.Id && m.Group != null && m.Group.TeacherId == _tenant.CurrentTenantId) ||
                Set<StudentUnitSubscription>().Any(u => u.StudentId == s.Id && u.TeacherId == _tenant.CurrentTenantId) ||
                Set<StudentLectureUnlock>().Any(u => u.StudentId == s.Id && u.TeacherId == _tenant.CurrentTenantId) ||
                Set<StudentOnlineLessonUnlock>().Any(u => u.StudentId == s.Id && u.TeacherId == _tenant.CurrentTenantId));

        modelBuilder.Entity<StudentGroupMembership>()
            .HasIndex(m => new { m.StudentId, m.GroupId }).IsUnique();

        modelBuilder.Entity<StudentGroupMembership>()
            .HasOne(m => m.Student)
            .WithMany(s => s.GroupMemberships)
            .HasForeignKey(m => m.StudentId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<StudentGroupMembership>()
            .HasOne(m => m.Group)
            .WithMany()
            .HasForeignKey(m => m.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        // Same tenant scoping as Group itself — a staff member of teacher X should
        // never see membership rows tying students to teacher Y's groups.
        modelBuilder.Entity<StudentGroupMembership>()
            .HasQueryFilter(m => m.Group != null && m.Group.TeacherId == _tenant.CurrentTenantId);

        modelBuilder.Entity<Lesson>()
            .HasOne(l => l.Unit)
            .WithMany(u => u.Lessons)
            .HasForeignKey(l => l.UnitId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Question>()
            .HasOne(q => q.Quiz)
            .WithMany(q => q.Questions)
            .HasForeignKey(q => q.QuizId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<LectureExamQuestion>()
            .HasOne(q => q.LectureExam)
            .WithMany(le => le.Questions)
            .HasForeignKey(q => q.LectureExamId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<LectureExamAnswer>()
            .HasOne(a => a.LectureExamResult)
            .WithMany(r => r.Answers)
            .HasForeignKey(a => a.LectureExamResultId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<QuizAnswer>()
            .HasOne(a => a.QuizResult)
            .WithMany(r => r.Answers)
            .HasForeignKey(a => a.QuizResultId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AssignmentQuestion>()
            .HasOne(q => q.Assignment)
            .WithMany(a => a.Questions)
            .HasForeignKey(q => q.AssignmentId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AssignmentAnswer>()
            .HasOne(a => a.AssignmentSubmission)
            .WithMany(s => s.Answers)
            .HasForeignKey(a => a.AssignmentSubmissionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AssignmentSubmission>()
            .HasIndex(s => new { s.AssignmentId, s.StudentId })
            .IsUnique();

        modelBuilder.Entity<AssignmentCenterQuestion>()
            .HasOne(q => q.AssignmentCenter)
            .WithMany(a => a.Questions)
            .HasForeignKey(q => q.AssignmentCenterId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AssignmentCenterAnswer>()
            .HasOne(a => a.AssignmentCenterSubmission)
            .WithMany(s => s.Answers)
            .HasForeignKey(a => a.AssignmentCenterSubmissionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AssignmentCenterSubmission>()
            .HasIndex(s => new { s.AssignmentCenterId, s.StudentId })
            .IsUnique();

        modelBuilder.Entity<StudentLectureUnlock>()
            .HasIndex(u => new { u.StudentId, u.LectureId })
            .IsUnique();

        modelBuilder.Entity<StudentOnlineLessonUnlock>()
            .HasIndex(u => new { u.StudentId, u.OnlineLessonId })
            .IsUnique();

        modelBuilder.Entity<BankAttemptQuestion>()
            .HasOne(q => q.Attempt)
            .WithMany(a => a.Questions)
            .HasForeignKey(q => q.AttemptId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<BankAttemptQuestion>()
            .HasOne(q => q.BankQuestion)
            .WithMany()
            .HasForeignKey(q => q.BankQuestionId)
            .OnDelete(DeleteBehavior.Restrict);

        // ═══════════════════════════════════════════════════════════════
        // CSV → real join table (see SaveChangesAsync override below).
        // GroupIdsCsv/UnitIdsCsv stay as the source of truth for writes
        // (every existing create/update endpoint keeps working unchanged),
        // but reads now filter through these indexed, exact-match tables
        // instead of a substring Contains() on the CSV string -- which was
        // both unindexable (forced a full scan every time) and capable of
        // false positives (group 1 matching inside "10,21").
        // FK + Cascade means deleting the parent automatically cleans up its
        // link rows with zero extra code.
        // ═══════════════════════════════════════════════════════════════
        modelBuilder.Entity<LectureGroupLink>()
            .HasOne<Lecture>().WithMany().HasForeignKey(x => x.LectureId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LectureGroupLink>().HasIndex(x => new { x.LectureId, x.GroupId }).IsUnique();
        modelBuilder.Entity<LectureGroupLink>().HasIndex(x => x.GroupId);

        modelBuilder.Entity<AssignmentGroupLink>()
            .HasOne<Assignment>().WithMany().HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AssignmentGroupLink>().HasIndex(x => new { x.AssignmentId, x.GroupId }).IsUnique();
        modelBuilder.Entity<AssignmentGroupLink>().HasIndex(x => x.GroupId);

        modelBuilder.Entity<AssignmentUnitLink>()
            .HasOne<Assignment>().WithMany().HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AssignmentUnitLink>().HasIndex(x => new { x.AssignmentId, x.UnitId }).IsUnique();
        modelBuilder.Entity<AssignmentUnitLink>().HasIndex(x => x.UnitId);

        modelBuilder.Entity<AssignmentCenterGroupLink>()
            .HasOne<AssignmentCenter>().WithMany().HasForeignKey(x => x.AssignmentCenterId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AssignmentCenterGroupLink>().HasIndex(x => new { x.AssignmentCenterId, x.GroupId }).IsUnique();
        modelBuilder.Entity<AssignmentCenterGroupLink>().HasIndex(x => x.GroupId);

        modelBuilder.Entity<AssignmentCenterUnitLink>()
            .HasOne<AssignmentCenter>().WithMany().HasForeignKey(x => x.AssignmentCenterId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AssignmentCenterUnitLink>().HasIndex(x => new { x.AssignmentCenterId, x.UnitId }).IsUnique();
        modelBuilder.Entity<AssignmentCenterUnitLink>().HasIndex(x => x.UnitId);

        modelBuilder.Entity<NotificationGroupLink>()
            .HasOne<Notification>().WithMany().HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NotificationGroupLink>().HasIndex(x => new { x.NotificationId, x.GroupId }).IsUnique();
        modelBuilder.Entity<NotificationGroupLink>().HasIndex(x => x.GroupId);

        modelBuilder.Entity<QuizGroupLink>()
            .HasOne<Quiz>().WithMany().HasForeignKey(x => x.QuizId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<QuizGroupLink>().HasIndex(x => new { x.QuizId, x.GroupId }).IsUnique();
        modelBuilder.Entity<QuizGroupLink>().HasIndex(x => x.GroupId);

        // SUPERADMIN LOGS FEATURE: this is exactly how LogsController queries
        // it (a given teacher's logs, optionally filtered by role and always
        // ordered by/filtered on time), so index all four together.
        modelBuilder.Entity<RequestErrorLog>()
            .HasIndex(x => new { x.TenantId, x.Role, x.StatusCode, x.CreatedAtUtc });

        // SUPERADMIN DELETED-ITEMS FEATURE: DeletedItemsController groups by
        // teacher + hour, so index those together too.
        modelBuilder.Entity<DeletedItemLog>()
            .HasIndex(x => new { x.TenantId, x.DeletedAtUtc });

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Keeps LectureGroupLinks/AssignmentGroupLinks/AssignmentUnitLinks/
    /// NotificationGroupLinks/QuizGroupLinks in sync with the *IdsCsv
    /// properties every time a Lecture/Assignment/Notification/Quiz is
    /// created or updated -- so every existing controller that sets
    /// entity.GroupIds = [...] keeps working exactly as before, with zero
    /// changes to any write endpoint. Two-pass: the parent entities are
    /// saved first so newly-Added rows get their DB-generated Id, then the
    /// link rows (which need that Id as their FK) are synced and saved.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var touched = ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Select(e => e.Entity)
            .Where(e => e is Lecture or Assignment or AssignmentCenter or Notification or Quiz)
            .ToList();

        // SUPERADMIN DELETED-ITEMS FEATURE: must run BEFORE base.SaveChangesAsync
        // below -- once the delete actually happens, EntityState.Deleted entries
        // go Detached and their values are gone.
        var pendingDeletions = CaptureDeletedEntitySnapshots();

        var result = await base.SaveChangesAsync(cancellationToken);

        if (touched.Count > 0)
        {
            foreach (var entity in touched)
            {
                switch (entity)
                {
                    case Lecture l:
                        await LectureGroupLinks.Where(x => x.LectureId == l.Id).ExecuteDeleteAsync(cancellationToken);
                        if (l.GroupIds.Count > 0)
                            LectureGroupLinks.AddRange(l.GroupIds.Distinct().Select(gid => new LectureGroupLink { LectureId = l.Id, GroupId = gid }));
                        break;

                    case Assignment a:
                        await AssignmentGroupLinks.Where(x => x.AssignmentId == a.Id).ExecuteDeleteAsync(cancellationToken);
                        if (a.GroupIds.Count > 0)
                            AssignmentGroupLinks.AddRange(a.GroupIds.Distinct().Select(gid => new AssignmentGroupLink { AssignmentId = a.Id, GroupId = gid }));

                        await AssignmentUnitLinks.Where(x => x.AssignmentId == a.Id).ExecuteDeleteAsync(cancellationToken);
                        if (a.UnitIds.Count > 0)
                            AssignmentUnitLinks.AddRange(a.UnitIds.Distinct().Select(uid => new AssignmentUnitLink { AssignmentId = a.Id, UnitId = uid }));
                        break;

                    case AssignmentCenter ac:
                        await AssignmentCenterGroupLinks.Where(x => x.AssignmentCenterId == ac.Id).ExecuteDeleteAsync(cancellationToken);
                        if (ac.GroupIds.Count > 0)
                            AssignmentCenterGroupLinks.AddRange(ac.GroupIds.Distinct().Select(gid => new AssignmentCenterGroupLink { AssignmentCenterId = ac.Id, GroupId = gid }));

                        await AssignmentCenterUnitLinks.Where(x => x.AssignmentCenterId == ac.Id).ExecuteDeleteAsync(cancellationToken);
                        if (ac.UnitIds.Count > 0)
                            AssignmentCenterUnitLinks.AddRange(ac.UnitIds.Distinct().Select(uid => new AssignmentCenterUnitLink { AssignmentCenterId = ac.Id, UnitId = uid }));
                        break;

                    case Notification n:
                        await NotificationGroupLinks.Where(x => x.NotificationId == n.Id).ExecuteDeleteAsync(cancellationToken);
                        if (n.GroupIds.Count > 0)
                            NotificationGroupLinks.AddRange(n.GroupIds.Distinct().Select(gid => new NotificationGroupLink { NotificationId = n.Id, GroupId = gid }));
                        break;

                    case Quiz q:
                        await QuizGroupLinks.Where(x => x.QuizId == q.Id).ExecuteDeleteAsync(cancellationToken);
                        if (q.GroupIds.Count > 0)
                            QuizGroupLinks.AddRange(q.GroupIds.Distinct().Select(gid => new QuizGroupLink { QuizId = q.Id, GroupId = gid }));
                        break;
                }
            }

            await base.SaveChangesAsync(cancellationToken);
        }

        if (pendingDeletions.Count > 0)
        {
            DeletedItemLogs.AddRange(pendingDeletions);
            await base.SaveChangesAsync(cancellationToken);
        }

        return result;
    }

    /// <summary>Sync counterpart of the above -- DbSeeder and a couple of other
    /// call sites use the sync SaveChanges(), which routes through this single
    /// overload (SaveChanges() with no args calls SaveChanges(true) internally),
    /// so overriding just this one covers all of them.</summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var pendingDeletions = CaptureDeletedEntitySnapshots();

        var result = base.SaveChanges(acceptAllChangesOnSuccess);

        if (pendingDeletions.Count > 0)
        {
            DeletedItemLogs.AddRange(pendingDeletions);
            base.SaveChanges(acceptAllChangesOnSuccess);
        }

        return result;
    }

    /// <summary>
    /// SUPERADMIN "الممسوحات" (Deleted Items) FEATURE: right before any actual
    /// DELETE hits the database, snapshot every tracked entity in
    /// EntityState.Deleted that carries a "TeacherId" property (i.e. genuine
    /// tenant-owned content -- Group/Unit/Lecture/Quiz/Assignment/etc; NOT the
    /// two audit-log tables themselves, which use "TenantId" instead, or any
    /// join table without a TeacherId of its own) into an in-memory
    /// DeletedItemLog, so a SuperAdmin can later see exactly what a teacher
    /// deleted and restore it with its original Id intact.
    ///
    /// Deliberately reflection/metadata-based (not a hardcoded entity list) so
    /// any NEW entity that gains a TeacherId property down the line is
    /// automatically covered too, with zero changes needed in whichever
    /// controller deletes it. Trade-off: entities whose tenant is only
    /// resolvable indirectly (e.g. Student, via GroupMemberships, has no
    /// TeacherId of its own) are NOT captured by this generic mechanism.
    /// </summary>
    private List<DeletedItemLog> CaptureDeletedEntitySnapshots()
    {
        var role = "Unknown";
        int? userId = null;
        var httpUser = _httpContextAccessor?.HttpContext?.User;
        if (httpUser?.Identity?.IsAuthenticated == true)
        {
            role = httpUser.FindFirstValue(ClaimTypes.Role) ?? "Unknown";
            userId = httpUser.GetUserId();
        }

        var logs = new List<DeletedItemLog>();

        foreach (var entry in ChangeTracker.Entries().Where(e => e.State == EntityState.Deleted).ToList())
        {
            if (entry.Entity is RequestErrorLog or DeletedItemLog) continue;

            var teacherIdProp = entry.Metadata.FindProperty("TeacherId");
            if (teacherIdProp == null) continue; // not a tenant-owned content entity -- skip

            var pkProp = entry.Metadata.FindPrimaryKey()?.Properties.FirstOrDefault();
            if (pkProp == null || entry.OriginalValues[pkProp] is not int entityId) continue;

            // Snapshot every scalar (EF-mapped, non-navigation) property's
            // ORIGINAL value -- navigation properties are deliberately
            // excluded: they're separate rows/entities with their own rules,
            // and would risk circular references on serialization anyway.
            var snapshot = new Dictionary<string, object?>();
            foreach (var prop in entry.Metadata.GetProperties())
                snapshot[prop.Name] = entry.OriginalValues[prop];

            var displayNameProp = entry.Metadata.FindProperty("Name") ?? entry.Metadata.FindProperty("Title");
            var displayName = displayNameProp != null ? entry.OriginalValues[displayNameProp]?.ToString() : null;

            logs.Add(new DeletedItemLog
            {
                TenantId = entry.OriginalValues[teacherIdProp] as int?,
                EntityType = entry.Metadata.ClrType.Name,
                EntityId = entityId,
                DisplayName = displayName,
                SnapshotJson = JsonSerializer.Serialize(snapshot),
                DeletedByRole = role,
                DeletedByUserId = userId,
                DeletedAtUtc = DateTime.UtcNow
            });
        }

        return logs;
    }
}
