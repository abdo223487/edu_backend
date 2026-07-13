using EduApi.Data;
using EduApi.Models;

namespace EduApi.Common;

public static class DbSeeder
{
    public static void Seed(AppDbContext db)
    {
        if (db.Teachers.Any()) return; // already seeded

        // ─── SuperAdmin bootstrap account ──────────────────────────────────
        // Username/password "admin"/"admin" is reserved: POST Teachers with
        // exactly these credentials always creates/promotes a SuperAdmin (see
        // TeachersController.Create), so it's seeded directly here too rather
        // than reusing "admin" for a demo tenant-root teacher (renamed below).
        db.Teachers.Add(new Teacher
        {
            UserName = "admin",
            Name = "Super Admin",
            Role = Roles.SuperAdmin,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin"),
            TenantOwnerId = null
        });

        // ─── Tenant #1 ──────────────────────────────────────────────────
        var teacher = new Teacher
        {
            UserName = "manager1",
            Name = "Admin Teacher",
            Role = Roles.AssistantAdmin,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin@123"),
            Subject = "Mathematics"
            // TenantOwnerId = null -> this account IS the tenant root (its own Id).
            // ImageUrl left null on purpose -> GetAll() falls back to a generated
            // placeholder avatar automatically, so SelectTeacherPage still won't crash.
        };
        db.Teachers.Add(teacher);
        db.SaveChanges(); // need teacher.Id before stamping TeacherId below

        var group = new Group { Name = "Group A", SchoolYear = 2026, TeacherId = teacher.Id };
        db.Groups.Add(group);
        db.SaveChanges();

        var student = new Student
        {
            Name = "Demo Student",
            PhoneNumber = "01000000000",
            UserName = "student1",
            GroupId = group.Id,
            SchoolYear = 2026,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Student@123")
        };
        db.Students.Add(student);

        // ── Demo Units (tenant #1, schoolYear 2026) + a partial subscription ──
        // "student1" is subscribed to only unitA/unitB, NOT unitC — demonstrates
        // GET Units returning a filtered subset for a student, not the whole catalog.
        var unitA = new Unit { Name = "Algebra Basics", SchoolYear = 2026, TeacherId = teacher.Id };
        var unitB = new Unit { Name = "Geometry", SchoolYear = 2026, TeacherId = teacher.Id };
        var unitC = new Unit { Name = "Trigonometry", SchoolYear = 2026, TeacherId = teacher.Id };
        db.Units.AddRange(unitA, unitB, unitC);
        db.SaveChanges(); // need their Ids before subscribing

        db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { TeacherId = teacher.Id, StudentId = student.Id, UnitId = unitA.Id });
        db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { TeacherId = teacher.Id, StudentId = student.Id, UnitId = unitB.Id });
        // unitC intentionally left out.

        // An Assistant working FOR teacher #1's tenant (TenantOwnerId set, not a root).
        var assistant = new Teacher
        {
            UserName = "assistant1",
            Name = "Assistant of Admin Teacher",
            Role = Roles.Assistant,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Assistant@123"),
            TenantOwnerId = teacher.Id
        };
        db.Teachers.Add(assistant);

        // ─── Tenant #2 (separate teacher, separate data — proves isolation) ───
        var teacher2 = new Teacher
        {
            UserName = "teacher2",
            Name = "Second Teacher",
            Role = Roles.Teacher,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Teacher2@123"),
            Subject = "Physics"
        };
        db.Teachers.Add(teacher2);
        db.SaveChanges();

        var group2 = new Group { Name = "Group A", SchoolYear = 2026, TeacherId = teacher2.Id };
        db.Groups.Add(group2);
        db.SaveChanges();

        db.Students.Add(new Student
        {
            Name = "Other Tenant's Student",
            PhoneNumber = "01000000001",
            UserName = "student2",
            GroupId = group2.Id,
            SchoolYear = 2026,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Student2@123")
        });

        // AppVersions are app-wide (not tenant-scoped).
        db.AppVersions.Add(new AppVersion { Platform = "android", Version = "1.0.0", DownloadUrl = "" });
        db.AppVersions.Add(new AppVersion { Platform = "ios", Version = "1.0.0", DownloadUrl = "" });

        db.SaveChanges();
    }
}
