using System.ComponentModel.DataAnnotations.Schema;

namespace EduApi.Models;

/// <summary>
/// "سنتر اسايمنت" -- same idea as Assignment (title/year/groups/units/deadline),
/// but every question is a fixed 4-choice bubble (أ/ب/ج/د). The teacher only
/// ever picks which letter is correct from a dropdown -- no free-text choices,
/// no attached files/images. The student answers by tapping one of 4 bubbles
/// per question, like a real bubble/answer sheet.
///
/// Deliberately a separate table/controller from Assignment (not a variant of
/// it), so the existing Assignment feature (free-text choices, images,
/// True/False, Written types) stays completely untouched.
/// </summary>
public class AssignmentCenter
{
    public int Id { get; set; }
    public string Title { get; set; } = default!;
    public int? SchoolYear { get; set; }
    public DateTime Deadline { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string GroupIdsCsv { get; set; } = string.Empty;
    [NotMapped] public List<int> GroupIds
    {
        get => GroupIdsCsv.Length == 0 ? new() : GroupIdsCsv.Split(',').Select(int.Parse).ToList();
        set => GroupIdsCsv = string.Join(',', value);
    }

    public string UnitIdsCsv { get; set; } = string.Empty;
    [NotMapped] public List<int> UnitIds
    {
        get => UnitIdsCsv.Length == 0 ? new() : UnitIdsCsv.Split(',').Select(int.Parse).ToList();
        set => UnitIdsCsv = string.Join(',', value);
    }

    /// <summary>Same idea as Quiz/Assignment.AllowLateReview.</summary>
    public bool AllowLateReview { get; set; } = true;

    /// <summary>TENANT LAYER: which teacher (tenant) this assignment-center belongs to.</summary>
    public int TeacherId { get; set; }

    public ICollection<AssignmentCenterQuestion> Questions { get; set; } = new List<AssignmentCenterQuestion>();
}

public class AssignmentCenterGroupLink
{
    public int Id { get; set; }
    public int AssignmentCenterId { get; set; }
    public int GroupId { get; set; }
}

public class AssignmentCenterUnitLink
{
    public int Id { get; set; }
    public int AssignmentCenterId { get; set; }
    public int UnitId { get; set; }
}

/// <summary>
/// A single bubble-sheet question. Choices are always exactly the 4 fixed
/// letters below (AssignmentCenterChoices.Letters) -- never stored per
/// question, never editable -- so Answer is always one of "أ"/"ب"/"ج"/"د".
/// </summary>
public class AssignmentCenterQuestion
{
    public int Id { get; set; }
    public int AssignmentCenterId { get; set; }
    [ForeignKey(nameof(AssignmentCenterId))] public AssignmentCenter? AssignmentCenter { get; set; }
    public string Text { get; set; } = default!;
    /// <summary>One of "أ" / "ب" / "ج" / "د" -- see AssignmentCenterChoices.Letters.</summary>
    public string Answer { get; set; } = default!;
    public int Mark { get; set; }
}

/// <summary>A student's single submission of an AssignmentCenter.</summary>
public class AssignmentCenterSubmission
{
    public int Id { get; set; }
    // MULTI-TENANT SECURITY: same reasoning as AssignmentSubmission.TeacherId.
    public int TeacherId { get; set; }
    public int AssignmentCenterId { get; set; }
    public int StudentId { get; set; }
    public int Score { get; set; }
    public int TotalMarks { get; set; }
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public ICollection<AssignmentCenterAnswer> Answers { get; set; } = new List<AssignmentCenterAnswer>();
}

public class AssignmentCenterAnswer
{
    public int Id { get; set; }
    public int AssignmentCenterSubmissionId { get; set; }
    [ForeignKey(nameof(AssignmentCenterSubmissionId))] public AssignmentCenterSubmission? AssignmentCenterSubmission { get; set; }
    public int QuestionId { get; set; }
    /// <summary>One of "أ" / "ب" / "ج" / "د".</summary>
    public string Answer { get; set; } = default!;
    public int? MarkAwarded { get; set; }
}

/// <summary>The 4 fixed bubble letters, shared by teacher (create) and student (answer) sides.</summary>
public static class AssignmentCenterChoices
{
    public static readonly string[] Letters = { "أ", "ب", "ج", "د" };
    public static bool IsValid(string? letter) => letter != null && Letters.Contains(letter);
}
