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

        // MULTI-TENANT MEMBERSHIP: a Student can belong to Groups under MORE THAN
        // ONE teacher now (StudentGroupMembership). A Student row is visible under
        // the current tenant if EITHER their legacy single Group belongs to this
        // tenant OR they have a membership row for a Group under this tenant.
        // NOTE: this filter only applies when querying the Students DbSet itself
        // (or navigating via a Student navigation property). It does NOT cascade
        // to result tables (QuizResult/CenterQuizResult/HomeworkResult) queried
        // directly off their own DbSet by StudentId — those need their own
        // TeacherId + filter, added below.
        modelBuilder.Entity<Student>()
            .HasQueryFilter(s =>
                (s.Group != null && s.Group.TeacherId == _tenant.CurrentTenantId) ||
                s.GroupMemberships.Any(m => m.Group != null && m.Group.TeacherId == _tenant.CurrentTenantId));

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

        base.OnModelCreating(modelBuilder);
    }
}
