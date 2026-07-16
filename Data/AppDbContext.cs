using EduApi.Common;
using EduApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Data;

public class AppDbContext : DbContext
{
    private readonly ITenantContext _tenant;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant) : base(options)
    {
        _tenant = tenant;
    }

    public DbSet<Teacher> Teachers => Set<Teacher>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<StudentUnitSubscription> StudentUnitSubscriptions => Set<StudentUnitSubscription>();
    public DbSet<StudentGroupMembership> StudentGroupMemberships => Set<StudentGroupMembership>();
    public DbSet<StudentLectureUnlock> StudentLectureUnlocks => Set<StudentLectureUnlock>();
    public DbSet<Lecture> Lectures => Set<Lecture>();
    public DbSet<Material> Materials => Set<Material>();
    public DbSet<Notebook> Notebooks => Set<Notebook>();
    public DbSet<NotebookPayment> NotebookPayments => Set<NotebookPayment>();
    public DbSet<Code> Codes => Set<Code>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Attendance> Attendances => Set<Attendance>();
    public DbSet<Quiz> Quizzes => Set<Quiz>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<QuizResult> QuizResults => Set<QuizResult>();
    public DbSet<QuizAnswer> QuizAnswers => Set<QuizAnswer>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<AssignmentQuestion> AssignmentQuestions => Set<AssignmentQuestion>();
    public DbSet<AssignmentSubmission> AssignmentSubmissions => Set<AssignmentSubmission>();
    public DbSet<AssignmentAnswer> AssignmentAnswers => Set<AssignmentAnswer>();
    public DbSet<BankQuestion> BankQuestions => Set<BankQuestion>();
    public DbSet<BankAttempt> BankAttempts => Set<BankAttempt>();
    public DbSet<BankAttemptQuestion> BankAttemptQuestions => Set<BankAttemptQuestion>();
    public DbSet<CenterQuizResult> CenterQuizResults => Set<CenterQuizResult>();
    public DbSet<HomeworkResult> HomeworkResults => Set<HomeworkResult>();
    public DbSet<StateHistoryEntry> StateHistoryEntries => Set<StateHistoryEntry>();
    public DbSet<AppVersion> AppVersions => Set<AppVersion>();

    // Real join tables backing the *IdsCsv columns' filtering. See the
    // SaveChangesAsync override below for how these stay in sync with
    // Lecture/Assignment/Notification/Quiz.GroupIds (and Assignment.UnitIds).
    public DbSet<LectureGroupLink> LectureGroupLinks => Set<LectureGroupLink>();
    public DbSet<AssignmentGroupLink> AssignmentGroupLinks => Set<AssignmentGroupLink>();
    public DbSet<AssignmentUnitLink> AssignmentUnitLinks => Set<AssignmentUnitLink>();
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
        modelBuilder.Entity<Lecture>().HasQueryFilter(l => l.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<Material>().HasQueryFilter(m => m.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<Notebook>().HasQueryFilter(n => n.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<Code>().HasQueryFilter(c => c.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<Notification>().HasQueryFilter(n => n.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<Quiz>().HasQueryFilter(q => q.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<Assignment>().HasQueryFilter(a => a.TeacherId == _tenant.CurrentTenantId);
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
        modelBuilder.Entity<Code>().HasIndex(c => c.TeacherId);
        modelBuilder.Entity<Notification>().HasIndex(n => n.TeacherId);
        modelBuilder.Entity<Quiz>().HasIndex(q => q.TeacherId);
        modelBuilder.Entity<Assignment>().HasIndex(a => a.TeacherId);
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
        modelBuilder.Entity<AssignmentSubmission>().HasQueryFilter(s => s.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<NotebookPayment>().HasQueryFilter(p => p.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<StudentLectureUnlock>().HasQueryFilter(u => u.TeacherId == _tenant.CurrentTenantId);
        modelBuilder.Entity<StudentUnitSubscription>().HasQueryFilter(s => s.TeacherId == _tenant.CurrentTenantId);

        modelBuilder.Entity<Attendance>().HasIndex(a => a.TeacherId);
        modelBuilder.Entity<AssignmentSubmission>().HasIndex(s => s.TeacherId);
        modelBuilder.Entity<NotebookPayment>().HasIndex(p => p.TeacherId);
        modelBuilder.Entity<StudentLectureUnlock>().HasIndex(u => u.TeacherId);
        modelBuilder.Entity<StudentUnitSubscription>().HasIndex(s => s.TeacherId);

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
        modelBuilder.Entity<Student>()
            .HasQueryFilter(s =>
                (s.Group != null && s.Group.TeacherId == _tenant.CurrentTenantId) ||
                s.GroupMemberships.Any(m => m.Group != null && m.Group.TeacherId == _tenant.CurrentTenantId) ||
                s.UnitSubscriptions.Any(u => u.TeacherId == _tenant.CurrentTenantId) ||
                Set<StudentLectureUnlock>().Any(u => u.StudentId == s.Id && u.TeacherId == _tenant.CurrentTenantId));

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

        modelBuilder.Entity<StudentLectureUnlock>()
            .HasIndex(u => new { u.StudentId, u.LectureId })
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

        modelBuilder.Entity<NotificationGroupLink>()
            .HasOne<Notification>().WithMany().HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NotificationGroupLink>().HasIndex(x => new { x.NotificationId, x.GroupId }).IsUnique();
        modelBuilder.Entity<NotificationGroupLink>().HasIndex(x => x.GroupId);

        modelBuilder.Entity<QuizGroupLink>()
            .HasOne<Quiz>().WithMany().HasForeignKey(x => x.QuizId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<QuizGroupLink>().HasIndex(x => new { x.QuizId, x.GroupId }).IsUnique();
        modelBuilder.Entity<QuizGroupLink>().HasIndex(x => x.GroupId);

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
            .Where(e => e is Lecture or Assignment or Notification or Quiz)
            .ToList();

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

        return result;
    }
}
