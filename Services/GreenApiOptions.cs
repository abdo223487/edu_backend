namespace EduApi.Services;

/// <summary>
/// Config for Green API (green-api.com), bound from the "GreenApi" section
/// of appsettings.json. Green API bridges a normal WhatsApp number (linked
/// via QR code, same as WhatsApp Web) to a simple REST API — unlike Meta's
/// official Cloud API, it sends free-form text messages directly with NO
/// pre-approved message template required.
/// </summary>
public class GreenApiOptions
{
    public const string SectionName = "GreenApi";

    /// <summary>Whether attendance notifications should actually be sent.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>From the instance page on console.green-api.com (e.g. "710722681004").</summary>
    public string IdInstance { get; set; } = string.Empty;

    /// <summary>From the instance page — regenerate it if it was ever shared/exposed.</summary>
    public string ApiTokenInstance { get; set; } = string.Empty;

    /// <summary>The instance's "apiUrl" shown on its console page (per-instance host,
    /// e.g. "https://7107.api.greenapi.com") — NOT a fixed shared domain.</summary>
    public string ApiUrl { get; set; } = "https://api.greenapi.com";

    /// <summary>Play Store link sent in the welcome message to a newly added student.</summary>
    public string AndroidStoreUrl { get; set; } = "https://play.google.com/store/apps/details?id=com.AcademIQv2.app";

    /// <summary>App Store link sent in the welcome message to a newly added student.</summary>
    public string IosStoreUrl { get; set; } = "https://apps.apple.com/app/ednova/id6796147882";
}
