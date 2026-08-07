using System.Text.Json;
using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/DeletedItems. SuperAdmin-only. Backs the "الممسوحات" (Deleted
/// Items) button on SuperAdminDashboard: pick a teacher -> see everything
/// that teacher (or their assistants) hard-deleted, grouped into hourly
/// cards like the Logs feature, and restore any of it -- one row at a time,
/// or the whole hour in one go -- back with its ORIGINAL Id.
///
/// Snapshots themselves are captured generically in AppDbContext (see
/// CaptureDeletedEntitySnapshots) with zero changes needed in whichever
/// controller performs the actual delete.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.SuperAdmin)]
public class DeletedItemsController : ControllerBase
{
    private readonly AppDbContext _db;
    public DeletedItemsController(AppDbContext db)
    {
        _db = db;
    }

    // BUGFIX: the Flutter date picker sends a plain calendar date chosen
    // against the DEVICE's local clock (Egypt, UTC+2), but DeletedAtUtc is
    // stored in UTC. Comparing the local date directly against UTC
    // timestamps meant anything deleted late at night local time (which is
    // still "yesterday" in UTC) silently landed one day earlier than where
    // the SuperAdmin was looking -- it looked like it never got captured at
    // all. Converting the requested local day to its real UTC start/end
    // fixes that. Egypt has used a fixed UTC+2 offset (no DST) since 2014;
    // update this if that policy ever changes again.
    private static readonly TimeSpan EgyptUtcOffset = TimeSpan.FromHours(2);

    private static (DateTime startUtc, DateTime endUtc) LocalDayToUtcRange(DateTime localDate)
    {
        var startUtc = localDate.Date - EgyptUtcOffset;
        return (startUtc, startUtc.AddDays(1));
    }

    /// <summary>GET DeletedItems/teachers/{teacherId}/buckets?date=yyyy-MM-dd
    /// Hourly cards of everything deleted for this teacher's tenant on the
    /// given day (defaults to today), not-yet-restored only.</summary>
    [HttpGet("teachers/{teacherId:int}/buckets")]
    public async Task<IActionResult> GetBuckets(int teacherId, [FromQuery] DateTime? date = null)
    {
        var teacherExists = await _db.Teachers.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(t => t.Id == teacherId);
        if (!teacherExists) return NotFound(new { message = "Teacher not found." });

        var localDay = (date ?? DateTime.UtcNow + EgyptUtcOffset).Date;
        var (dayStartUtc, dayEndUtc) = LocalDayToUtcRange(localDay);

        var dayLogs = await _db.DeletedItemLogs.AsNoTracking()
            .Where(l => l.TenantId == teacherId && !l.IsRestored
                        && l.DeletedAtUtc >= dayStartUtc && l.DeletedAtUtc < dayEndUtc)
            .Select(l => new { l.DeletedAtUtc })
            .ToListAsync();

        var buckets = dayLogs
            // Group by the HOUR IN LOCAL TIME too, so a card labeled "٩:٠٠ -
            // ١٠:٠٠" actually matches what the SuperAdmin would call that hour.
            .GroupBy(l => (l.DeletedAtUtc + EgyptUtcOffset).Hour)
            .Select(g => new
            {
                hour = g.Key,
                hourLabel = $"{g.Key:00}:00 - {(g.Key + 1) % 24:00}:00",
                count = g.Count()
            })
            .OrderBy(b => b.hour)
            .ToList();

        return Ok(new
        {
            teacherId,
            date = localDay.ToString("yyyy-MM-dd"),
            buckets
        });
    }

    /// <summary>GET DeletedItems/teachers/{teacherId}/buckets/details?date=..&hour=..
    /// Every deleted row inside one hourly bucket -- entity type, display
    /// name, who deleted it, when, and (see BuildAffectedTypes) what else
    /// among this teacher's still-not-restored deleted items appears to
    /// depend on it (so a SuperAdmin knows what "comes along" if they restore
    /// it, and what to restore first). Each restorable individually.</summary>
    [HttpGet("teachers/{teacherId:int}/buckets/details")]
    public async Task<IActionResult> GetBucketDetails(int teacherId, [FromQuery] DateTime date, [FromQuery] int hour)
    {
        if (hour is < 0 or > 23) return BadRequest(new { message = "hour must be between 0 and 23." });

        // hour here is a LOCAL hour (see GetBuckets above) -- convert the
        // whole [date 00:00local, date+1 00:00local) window's worth of UTC
        // start, then offset by `hour` local hours, same fix as GetBuckets.
        var (dayStartUtc, _) = LocalDayToUtcRange(date.Date);
        var bucketStart = dayStartUtc.AddHours(hour);
        var bucketEnd = bucketStart.AddHours(1);

        var items = await _db.DeletedItemLogs.AsNoTracking()
            .Where(l => l.TenantId == teacherId && !l.IsRestored
                        && l.DeletedAtUtc >= bucketStart && l.DeletedAtUtc < bucketEnd)
            .OrderByDescending(l => l.DeletedAtUtc)
            .ToListAsync();

        if (items.Count == 0) return Ok(Array.Empty<object>());

        // Everything else still deleted for this teacher (any date/hour) is the
        // pool we search for "does this reference the item above" -- a cascade
        // delete typically fires in the same request/transaction as its parent,
        // so in practice this is almost always the same bucket, but we don't
        // limit to it in case the parent and a child landed a minute apart
        // across an hour boundary.
        var allUnrestoredForTeacher = await _db.DeletedItemLogs.AsNoTracking()
            .Where(l => l.TenantId == teacherId && !l.IsRestored)
            .Select(l => new UnrestoredLogRef(l.Id, l.EntityType, l.EntityId, l.DisplayName, l.SnapshotJson))
            .ToListAsync();

        var result = items.Select(l => new
        {
            l.Id,
            l.EntityType,
            l.EntityId,
            l.DisplayName,
            l.DeletedByRole,
            l.DeletedByUserId,
            deletedAtUtc = l.DeletedAtUtc,
            affects = BuildAffectedTypes(l, allUnrestoredForTeacher)
        });

        return Ok(result);
    }

    /// <summary>
    /// Generic "what depends on this deleted item" check: looks for other
    /// still-deleted rows (of this same teacher) whose snapshot has a
    /// "{item.EntityType}Id" field pointing at item.EntityId -- e.g. for a
    /// deleted Group, any other deleted row with GroupId == that group's Id
    /// (a Lecture, a Quiz, a Student's old membership, etc). Grouped by
    /// entity type with a couple of example names, so the UI can say
    /// "restoring this Group will also need: 3 Lectures (X, Y, Z), 1 Quiz".
    /// Best-effort/heuristic (only catches direct single-Id FK columns, not
    /// many-to-many link tables) -- see class docs on the general trade-off.
    /// </summary>
    private static List<object> BuildAffectedTypes(
        DeletedItemLog parent,
        IEnumerable<UnrestoredLogRef> allUnrestored)
    {
        var fkPropertyName = $"{parent.EntityType}Id";

        var dependents = new List<(string EntityType, string? DisplayName)>();
        foreach (var candidate in allUnrestored)
        {
            if (candidate.Id == parent.Id) continue;
            Dictionary<string, JsonElement>? snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(candidate.SnapshotJson);
            }
            catch { continue; }
            if (snapshot == null) continue;

            if (snapshot.TryGetValue(fkPropertyName, out var fk)
                && fk.ValueKind == JsonValueKind.Number
                && fk.TryGetInt32(out var fkValue)
                && fkValue == parent.EntityId)
            {
                dependents.Add((candidate.EntityType, candidate.DisplayName));
            }
        }

        return dependents
            .GroupBy(d => d.EntityType)
            .Select(g => (object)new
            {
                entityType = g.Key,
                count = g.Count(),
                sampleNames = g.Where(x => x.DisplayName != null).Select(x => x.DisplayName).Take(3).ToList()
            })
            .ToList();
    }

    /// <summary>Lightweight projection used only by BuildAffectedTypes -- a plain
    /// type instead of an anonymous one so it can be passed around as a
    /// parameter (and to avoid needing the Microsoft.CSharp `dynamic` binder,
    /// which this project doesn't reference).</summary>
    private sealed record UnrestoredLogRef(int Id, string EntityType, int EntityId, string? DisplayName, string SnapshotJson);

    /// <summary>POST DeletedItems/{id}/restore -- restores a single deleted row
    /// with its original Id, exactly as it was.</summary>
    [HttpPost("{id:int}/restore")]
    public async Task<IActionResult> RestoreOne(int id)
    {
        var log = await _db.DeletedItemLogs.FirstOrDefaultAsync(l => l.Id == id);
        if (log == null) return NotFound(new { message = "Deleted item not found." });
        if (log.IsRestored) return BadRequest(new { message = "This item was already restored." });

        var (ok, error) = await TryRestoreAsync(log);
        if (!ok) return BadRequest(new { message = FriendlyRestoreError(error) });

        return Ok(new { message = "Restored successfully.", entityType = log.EntityType, entityId = log.EntityId });
    }

    /// <summary>
    /// POST DeletedItems/teachers/{teacherId}/buckets/restore?date=..&hour=..
    /// Restores every not-yet-restored row in one hourly bucket. Order isn't
    /// guaranteed to match original FK dependencies (a child row might need
    /// its parent restored first), so this retries in passes: whatever
    /// succeeds in a pass unblocks whatever depended on it in the next one.
    /// Returns which rows were restored and which failed (with why), so nothing
    /// fails silently.
    /// </summary>
    [HttpPost("teachers/{teacherId:int}/buckets/restore")]
    public async Task<IActionResult> RestoreBucket(int teacherId, [FromQuery] DateTime date, [FromQuery] int hour)
    {
        if (hour is < 0 or > 23) return BadRequest(new { message = "hour must be between 0 and 23." });

        var (dayStartUtc, _) = LocalDayToUtcRange(date.Date);
        var bucketStart = dayStartUtc.AddHours(hour);
        var bucketEnd = bucketStart.AddHours(1);

        var logs = await _db.DeletedItemLogs
            .Where(l => l.TenantId == teacherId && !l.IsRestored
                        && l.DeletedAtUtc >= bucketStart && l.DeletedAtUtc < bucketEnd)
            .ToListAsync();

        var pending = logs;
        var restored = new List<object>();
        var lastErrors = new Dictionary<int, string>();

        // Up to one retry pass per remaining item -- generously bounds the
        // number of passes for typical parent/child depths in this schema.
        for (var pass = 0; pass < Math.Max(1, pending.Count) && pending.Count > 0; pass++)
        {
            var stillPending = new List<DeletedItemLog>();
            var progressedThisPass = false;

            foreach (var log in pending)
            {
                var (ok, error) = await TryRestoreAsync(log);
                if (ok)
                {
                    restored.Add(new { log.Id, log.EntityType, log.EntityId, log.DisplayName });
                    progressedThisPass = true;
                }
                else
                {
                    lastErrors[log.Id] = FriendlyRestoreError(error);
                    stillPending.Add(log);
                }
            }

            pending = stillPending;
            if (!progressedThisPass) break; // no point retrying further -- nothing is unblocking
        }

        var failed = pending.Select(l => new { l.Id, l.EntityType, l.EntityId, l.DisplayName, error = lastErrors.GetValueOrDefault(l.Id) });

        return Ok(new
        {
            restoredCount = restored.Count,
            failedCount = pending.Count,
            restored,
            failed
        });
    }

    /// <summary>
    /// Turns a raw Postgres/EF exception message into something a SuperAdmin
    /// can actually act on. The overwhelmingly common failure here is a
    /// foreign-key violation because a row this one depends on (its parent)
    /// hasn't been restored yet -- so that's called out explicitly instead of
    /// showing a raw "violates foreign key constraint ..." string.
    /// </summary>
    private static string FriendlyRestoreError(string? rawError)
    {
        if (string.IsNullOrEmpty(rawError)) return "حصل خطأ غير معروف أثناء الاسترجاع.";

        if (rawError.Contains("foreign key", StringComparison.OrdinalIgnoreCase)
            || rawError.Contains("violates", StringComparison.OrdinalIgnoreCase))
        {
            return "لسه مينفعش يترجع -- محتاج عنصر تاني (أب له) يترجع الأول. جرّب تاني بعد ما ترجّع باقي عناصر الساعة دي.";
        }

        if (rawError.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            || rawError.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return "فيه عنصر بنفس الرقم موجود بالفعل -- يمكن اترجع قبل كده.";
        }

        if (rawError.StartsWith("Unknown entity type") || rawError.Contains("is not a mapped entity"))
        {
            return "نوع العنصر ده مش معروف للسيستم دلوقتي (يمكن اتغيّر اسمه في تحديث لاحق).";
        }

        return rawError; // fall back to the raw message rather than hide it entirely
    }

    /// <summary>
    /// Deserializes the snapshot back into a real entity of the original CLR
    /// type, with its original primary key, and saves it -- one row, one
    /// SaveChangesAsync, so a failure (almost always: FK violation because a
    /// parent hasn't been restored yet) only rolls back THIS row and leaves
    /// the DbContext clean for the next attempt, instead of taking a whole
    /// batch down with it.
    /// </summary>
    private async Task<(bool ok, string? error)> TryRestoreAsync(DeletedItemLog log)
    {
        object? instance = null;
        try
        {
            var entityType = typeof(Roles).Assembly.GetTypes()
                .FirstOrDefault(t => t.Namespace == "EduApi.Models" && t.Name == log.EntityType);
            if (entityType == null) return (false, $"Unknown entity type '{log.EntityType}'.");

            var efType = _db.Model.FindEntityType(entityType);
            if (efType == null) return (false, $"'{log.EntityType}' is not a mapped entity.");

            var snapshot = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(log.SnapshotJson);
            if (snapshot == null) return (false, "Snapshot data is corrupted.");

            instance = Activator.CreateInstance(entityType);
            if (instance == null) return (false, "Could not create an instance of the entity.");

            foreach (var prop in efType.GetProperties())
            {
                if (!snapshot.TryGetValue(prop.Name, out var jsonValue)) continue;
                var clrProp = entityType.GetProperty(prop.Name);
                if (clrProp == null || !clrProp.CanWrite) continue;

                var value = ConvertJsonElement(jsonValue, clrProp.PropertyType);
                clrProp.SetValue(instance, value);
            }

            _db.Add(instance);
            log.IsRestored = true;
            log.RestoredAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            return (true, null);
        }
        catch (Exception ex)
        {
            // Undo the in-memory state of this attempt so the DbContext is
            // clean for the next item/pass -- SaveChangesAsync failing does
            // NOT automatically revert tracked states or property values.
            if (instance != null)
            {
                var entry = _db.Entry(instance);
                if (entry.State != EntityState.Detached) entry.State = EntityState.Detached;
            }
            log.IsRestored = false;
            log.RestoredAtUtc = null;
            _db.Entry(log).State = EntityState.Unchanged;

            // Most common cause: a parent row this one depends on (FK) hasn't
            // been restored yet -- the retry loop in RestoreBucket handles that.
            return (false, ex.Message);
        }
    }

    private static object? ConvertJsonElement(JsonElement el, Type targetType)
    {
        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined) return null;

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying == typeof(int)) return el.GetInt32();
        if (underlying == typeof(long)) return el.GetInt64();
        if (underlying == typeof(bool)) return el.GetBoolean();
        if (underlying == typeof(decimal)) return el.GetDecimal();
        if (underlying == typeof(double)) return el.GetDouble();
        if (underlying == typeof(float)) return el.GetSingle();
        if (underlying == typeof(DateTime)) return el.GetDateTime();
        if (underlying == typeof(Guid)) return el.GetGuid();
        if (underlying.IsEnum) return Enum.Parse(underlying, el.GetString()!);
        if (underlying == typeof(string)) return el.GetString();

        // Fallback: let System.Text.Json figure it out generically.
        return JsonSerializer.Deserialize(el.GetRawText(), targetType);
    }
}
