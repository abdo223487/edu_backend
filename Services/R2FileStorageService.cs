using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using EduApi.Common;
using EduApi.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace EduApi.Services;

/// <summary>
/// Cloudflare R2 implementation of IFileStorageService. R2 exposes an
/// S3-compatible API, so we use AWSSDK.S3 pointed at R2's endpoint instead of
/// AWS — no AWS account involved anywhere in this class.
///
/// TENANT ISOLATION (same policy as the local-disk implementation):
/// Every object key is namespaced as "{tenantId}/{subFolder}/{guid}{ext}" so
/// tenants can never collide or overwrite each other's files, and a whole
/// tenant's files can be listed/deleted by prefix if ever needed.
///
/// SWAPPING: this class fully implements IFileStorageService, so it's a
/// drop-in replacement for FileStorageService — only the DI registration in
/// Program.cs needs to change (already done below). No controller changes.
/// </summary>
public class R2FileStorageService : IFileStorageService
{
    private readonly IAmazonS3 _s3;
    private readonly R2Options _options;
    private readonly ITenantContext _tenant;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".pdf", ".doc", ".docx",
        ".ppt", ".pptx", ".xls", ".xlsx", ".mp4", ".mp3", ".zip"
    };

    public R2FileStorageService(IAmazonS3 s3, IOptions<R2Options> options, ITenantContext tenant)
    {
        _s3 = s3;
        _options = options.Value;
        _tenant = tenant;
    }

    public Task<string> SaveAsync(IFormFile file, string subFolder)
    {
        var tenantId = _tenant.CurrentTenantId;
        if (tenantId == null)
        {
            throw new InvalidOperationException(
                "File upload rejected: no tenant could be resolved for the current request.");
        }

        return SaveAsync(file, subFolder, tenantId.Value);
    }

    public async Task<string> SaveAsync(IFormFile file, string subFolder, int tenantId)
    {
        if (file == null || file.Length == 0)
            throw new ArgumentException("No file was provided or the file is empty.", nameof(file));

        var safeSubFolder = SanitizeSegment(subFolder);
        var extension = Path.GetExtension(file.FileName);
        var safeExtension = IsSafeExtension(extension) ? extension : string.Empty;

        // Object key = the "path" inside the bucket. No leading slash for S3-style keys.
        var key = $"{tenantId}/{safeSubFolder}/{Guid.NewGuid():N}{safeExtension}";

        await using var stream = file.OpenReadStream();
        var request = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = key,
            InputStream = stream,
            ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            AutoCloseStream = true,
            // Cloudflare R2 doesn't implement the SDK's default "aws-chunked"
            // streaming-signed-payload upload mode (STREAMING-AWS4-HMAC-SHA256-PAYLOAD),
            // which is why every PutObject call was failing with "not implemented".
            // This must be set per-request (it isn't available on AmazonS3Config in
            // AWSSDK.S3 3.7.400+) to force classic single-shot SigV4 signing instead.
            UseChunkEncoding = false,
            // R2 buckets are private by default and ignore ACLs unless you've
            // enabled the "public bucket" feature — public access is instead
            // controlled at the bucket level (r2.dev subdomain or a connected
            // custom domain), which is why we don't set CannedACL here.
        };

        await _s3.PutObjectAsync(request);

        return $"{_options.PublicBaseUrl.TrimEnd('/')}/{key}";
    }

    public (string UploadUrl, string PublicUrl) GetPresignedUploadUrl(string subFolder, string fileExtension, string contentType)
    {
        var tenantId = _tenant.CurrentTenantId;
        if (tenantId == null)
        {
            throw new InvalidOperationException(
                "Presigned upload rejected: no tenant could be resolved for the current request.");
        }

        var safeSubFolder = SanitizeSegment(subFolder);
        var safeExtension = IsSafeExtension(fileExtension) ? fileExtension : string.Empty;
        var key = $"{tenantId.Value}/{safeSubFolder}/{Guid.NewGuid():N}{safeExtension}";

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.BucketName,
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.AddMinutes(30),
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
        };

        var uploadUrl = _s3.GetPreSignedURL(request);
        var publicUrl = $"{_options.PublicBaseUrl.TrimEnd('/')}/{key}";
        return (uploadUrl, publicUrl);
    }

    public async Task DeleteAsync(string? fileUrl)
    {
        var key = TryExtractKey(fileUrl);
        if (key == null) return; // null/empty/external URL (e.g. a manually-set ImageUrl) — nothing to do

        try
        {
            await _s3.DeleteObjectAsync(_options.BucketName, key);
        }
        catch (AmazonS3Exception)
        {
            // Best-effort cleanup: if the object is already gone or R2 hiccups,
            // we don't want that to fail the caller's update/edit request —
            // the important part (DB already points to the new file) is done.
        }
    }

    /// <summary>
    /// Recovers the R2 object key ("{tenantId}/{subFolder}/{guid}.ext") from a
    /// full public URL we previously generated in SaveAsync. Returns null for
    /// anything that isn't one of our own PublicBaseUrl URLs, so we never try
    /// to delete an externally-hosted image (e.g. one set via a raw ImageUrl
    /// field instead of a file upload).
    /// </summary>
    private string? TryExtractKey(string? fileUrl)
    {
        if (string.IsNullOrWhiteSpace(fileUrl)) return null;

        var prefix = _options.PublicBaseUrl.TrimEnd('/') + "/";
        if (!fileUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var key = fileUrl[prefix.Length..];
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    private static string SanitizeSegment(string segment)
    {
        var cleaned = new string(segment.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(cleaned))
            throw new ArgumentException("Invalid storage subfolder.", nameof(segment));
        return cleaned;
    }

    private static bool IsSafeExtension(string extension) =>
        !string.IsNullOrEmpty(extension) && AllowedExtensions.Contains(extension);
}
