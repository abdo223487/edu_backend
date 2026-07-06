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
    /// Deletes a previously-uploaded file given the URL that SaveAsync returned
    /// for it (exactly as stored in the database). Safe to call with null/empty/
    /// external URLs — those are simply ignored rather than throwing, since a
    /// failed cleanup of an old file should never block or fail the request
    /// that's replacing it.
    /// </summary>
    Task DeleteAsync(string? fileUrl);
}
