using EduApi.Common;
using EduApi.Services.Interfaces;

namespace EduApi.Services;

/// <summary>
/// Educational/demo implementation: stores files under
/// wwwroot/uploads/{tenantId}/{subFolder}/ and serves them back as static
/// files. Swap for S3/Azure Blob/Google Drive in production without changing
/// any controller code — only this class needs to change.
///
/// TENANT ISOLATION
/// ─────────────────────────────────────────────────────────────────────────
/// Every file is namespaced by the current request's tenant (see
/// ITenantContext / TenantContext.cs — same source of truth the EF Core
/// global query filters use). This keeps each teacher's uploaded materials,
/// unit images, teacher photos, and quiz-question images physically
/// separated on disk, so:
///   - There's no risk of one tenant's files colliding/overwriting another's
///     even if two tenants upload a file with the exact same name.
///   - Bulk operations (backup, export, delete) for a single tenant are a
///     single folder copy/delete instead of a filtered scan.
///   - If tenant data ever needs to be purged (GDPR-style request, account
///     closure), `wwwroot/uploads/{tenantId}` can be deleted wholesale.
///
/// If the tenant cannot be resolved (which should not happen for any
/// authenticated upload endpoint today — all of them are staff-only actions
/// with a tenant embedded in the JWT), we fail loudly instead of silently
/// writing to a shared/ambiguous folder, since that would be a silent
/// tenant-isolation bug rather than a legitimate use case.
/// </summary>
public class FileStorageService : IFileStorageService
{
    private readonly IWebHostEnvironment _env;
    private readonly IHttpContextAccessor _http;
    private readonly ITenantContext _tenant;

    public FileStorageService(IWebHostEnvironment env, IHttpContextAccessor http, ITenantContext tenant)
    {
        _env = env;
        _http = http;
        _tenant = tenant;
    }

    public Task<string> SaveAsync(IFormFile file, string subFolder)
    {
        var tenantId = _tenant.CurrentTenantId;
        if (tenantId == null)
        {
            // Every current upload endpoint is staff-only and always has a
            // resolvable tenant (JWT claim). Reaching this means either an
            // unauthenticated caller slipped through, or a new endpoint was
            // wired up without tenant context — both are bugs worth
            // surfacing immediately rather than masking with a shared folder.
            throw new InvalidOperationException(
                "File upload rejected: no tenant could be resolved for the current request.");
        }

        return SaveAsync(file, subFolder, tenantId.Value);
    }

    public async Task<string> SaveAsync(IFormFile file, string subFolder, int tenantId)
    {
        if (file == null || file.Length == 0)
            throw new ArgumentException("No file was provided or the file is empty.", nameof(file));

        // Defense in depth: subFolder is always a hardcoded literal from our
        // own controllers today ("materials", "teachers", "units",
        // "quiz-questions"), but sanitize anyway so this stays safe even if
        // that ever changes.
        var safeSubFolder = SanitizeSegment(subFolder);

        var root = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        var folder = Path.Combine(root, "uploads", tenantId.ToString(), safeSubFolder);
        Directory.CreateDirectory(folder);

        // Keep the original extension (needed for content-type sniffing on the
        // client/browser side) but never trust the original file name itself.
        var extension = Path.GetExtension(file.FileName);
        var safeExtension = IsSafeExtension(extension) ? extension : string.Empty;
        var fileName = $"{Guid.NewGuid():N}{safeExtension}";
        var fullPath = Path.Combine(folder, fileName);

        await using (var stream = new FileStream(fullPath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var request = _http.HttpContext?.Request;
        var baseUrl = request != null ? $"{request.Scheme}://{request.Host}" : string.Empty;
        return $"{baseUrl}/uploads/{tenantId}/{safeSubFolder}/{fileName}";
    }

    // Direct-to-storage presigned uploads only make sense for a real
    // object-storage backend (R2/S3) that clients can PUT to over the
    // internet. The local-disk implementation is dev/demo-only, so there's
    // no equivalent — callers should already be selecting R2 in any
    // environment where this endpoint gets used.
    public (string UploadUrl, string PublicUrl) GetPresignedUploadUrl(string subFolder, string fileExtension, string contentType) =>
        throw new NotSupportedException(
            "Direct client uploads require the R2 file storage backend; the local-disk FileStorageService does not support presigned URLs.");

    public Task DeleteAsync(string? fileUrl)
    {
        var relativePath = TryExtractRelativePath(fileUrl);
        if (relativePath == null) return Task.CompletedTask;

        var root = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        var fullPath = Path.Combine(root, relativePath);

        try
        {
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }
        catch (IOException)
        {
            // Best-effort cleanup — a locked/already-gone file should never fail
            // the caller's update/edit request.
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Recovers the on-disk relative path ("uploads/{tenantId}/{subFolder}/{file}")
    /// from a full URL we previously generated in SaveAsync. Returns null for
    /// anything that doesn't look like one of our own "/uploads/..." URLs.
    /// </summary>
    private static string? TryExtractRelativePath(string? fileUrl)
    {
        if (string.IsNullOrWhiteSpace(fileUrl)) return null;

        var marker = "/uploads/";
        var index = fileUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        var relative = fileUrl[(index + 1)..]; // keep "uploads/..." without leading slash
        return relative.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>Strips anything that isn't a plain folder-name character, blocking path traversal.</summary>
    private static string SanitizeSegment(string segment)
    {
        var cleaned = new string(segment.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(cleaned))
            throw new ArgumentException("Invalid storage subfolder.", nameof(segment));
        return cleaned;
    }

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".pdf", ".doc", ".docx",
        ".ppt", ".pptx", ".xls", ".xlsx", ".mp4", ".mp3", ".zip"
    };

    private static bool IsSafeExtension(string extension) =>
        !string.IsNullOrEmpty(extension) && AllowedExtensions.Contains(extension);
}
