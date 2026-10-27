namespace EduApi.Services;

/// <summary>
/// Config for Firebase Cloud Messaging, bound from the "Fcm" section.
/// DO NOT put the service-account JSON in appsettings.json in source control --
/// set it as an environment variable instead:
///   Fcm__Enabled=true
///   Fcm__ServiceAccountJson={ ...whole service-account JSON... }   (raw JSON or base64 of it)
/// or point Fcm__ServiceAccountPath at a mounted secret file.
/// </summary>
public class FcmOptions
{
    public const string SectionName = "Fcm";

    public bool Enabled { get; set; } = false;
    public string ServiceAccountJson { get; set; } = string.Empty;
    public string ServiceAccountPath { get; set; } = string.Empty;
}
