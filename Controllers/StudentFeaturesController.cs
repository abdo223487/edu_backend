using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/StudentFeatures  (student only; always for the X-TenantId teacher)
///  GET StudentFeatures/me   { disabled: [keys of student-facing features the teacher's plan does NOT include] }
///
/// The student app hides a button (e.g. "محرر أكواد") when its key is in "disabled".
/// The SuperAdmin controls this through the plan editor / the teacher's features page —
/// the keys come from Common/TeacherFeatures.cs (Catalog + StudentFacing).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.Student)]
public class StudentFeaturesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public StudentFeaturesController(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var tenantId = _tenant.CurrentTenantId;
        if (tenantId == null) return BadRequest(new { message = "اختار المدرس الأول." });

        var disabled = await TenantFeatureLookup.GetDisabledAsync(_db, tenantId.Value, ct);
        return Ok(new
        {
            disabled = disabled.Where(TeacherFeatures.StudentFacing.Contains).ToArray(),
        });
    }
}
