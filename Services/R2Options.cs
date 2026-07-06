namespace EduApi.Services;

/// <summary>
/// Bound from the "R2" section in appsettings.json / environment variables.
/// See README section "Cloudflare R2 setup" for where each value comes from.
/// </summary>
public class R2Options
{
    public const string SectionName = "R2";

    /// <summary>Your Cloudflare account ID (dashboard URL or R2 landing page). Used to build the S3 endpoint.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>R2 API token's Access Key ID (R2 -> Manage API Tokens -> Create API Token).</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>R2 API token's Secret Access Key (shown ONCE at creation time — store it in a secret manager, not in source control).</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>The R2 bucket name that stores all tenant uploads.</summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>
    /// Public base URL for serving objects back to clients — either the
    /// bucket's r2.dev dev subdomain, or your own custom domain connected to
    /// the bucket (recommended for production). No trailing slash.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;
}
