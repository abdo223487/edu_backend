namespace EduApi.Services.Interfaces;

public interface IFileStorageService
{
    /// <summary>
    /// Saves an uploaded file to disk under the CURRENT request's tenant folder
    /// (uploads/{tenantId}/{subFolder}/...) and returns a publicly reachable URL.
    /// Tenant is resolved automatically from ITenantContext — callers never pass it.
    /// </summary>
    Task<string> SaveAsync(IFormFile file, string subFolder);

    /// <summary>
    /// Same as <see cref="SaveAsync(IFormFile, string)"/> but with the tenant
    /// folder/prefix given explicitly instead of resolved from ITenantContext.
    /// Needed for the one case where a file must be uploaded for a tenant that
    /// doesn't have request-scoped tenant context yet -- SuperAdmin creating a
    /// brand-new root Teacher (with a photo) in the same call that creates
    /// that Teacher row, before any JWT/X-TenantId for it could possibly exist.
    /// Every other caller should keep using the tenant-context overload above.
    /// </summary>
    Task<string> SaveAsync(IFormFile file, string subFolder, int tenantId);

    /// <summary>
    /// Deletes a previously-uploaded file given the URL that SaveAsync returned
    /// for it (exactly as stored in the database). Safe to call with null/empty/
    /// external URLs — those are simply ignored rather than throwing, since a
    /// failed cleanup of an old file should never block or fail the request
    /// that's replacing it.
    /// </summary>
    Task DeleteAsync(string? fileUrl);

    /// <summary>
    /// Generates a short-lived presigned PUT URL that lets the CLIENT upload a
    /// file straight to R2 without the bytes ever passing through this API
    /// server (used for large recorded-lecture videos so we don't tie up an
    /// ASP.NET request/worker for the whole upload). Returns both the
    /// UploadUrl the client should PUT the raw file bytes to (with the exact
    /// same Content-Type header used here) and the PublicUrl that object will
    /// be reachable at afterwards — the same PublicUrl shape SaveAsync would
    /// have returned had the server done the upload itself, so it can be
    /// stored/used identically (e.g. passed back as Lecture.YoutubeLink).
    /// Tenant is resolved from ITenantContext, same isolation as SaveAsync.
    /// </summary>
    (string UploadUrl, string PublicUrl) GetPresignedUploadUrl(string subFolder, string fileExtension, string contentType);
}
