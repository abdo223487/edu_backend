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

        // Student doesn't carry TeacherId directly — it's derived through its Group,
        // which itself is tenant-owned. This also automatically tenant-scopes every
        // screen built on top of Students (Analytics, quiz/homework results, etc.).
        modelBuilder.Entity<Student>()
            .HasQueryFilter(s => s.Group != null && s.Group.TeacherId == _tenant.CurrentTenantId);

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

        base.OnModelCreating(modelBuilder);
    }
}
