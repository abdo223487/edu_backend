namespace EduApi.Services;

/// <summary>
/// Config for Meta's WhatsApp Cloud API, bound from the "WhatsApp" section of
/// appsettings.json.
/// </summary>
public class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    /// <summary>Whether attendance notifications should actually be sent.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Phone Number ID from Meta's WhatsApp Business API dashboard
    /// (NOT the phone number itself, NOT the WABA id).</summary>
    public string PhoneNumberId { get; set; } = string.Empty;

    /// <summary>Permanent (System User) access token — the temporary 24h token
    /// from the dashboard's Quickstart page will expire and break this.</summary>
    public string AccessToken { get; set; } = string.Empty;

    public string ApiVersion { get; set; } = "v21.0";

    /// <summary>
    /// Name of the approved Message Template in Meta Business Manager.
    /// Business-initiated messages MUST use an approved template. This
    /// project uses the APPROVED template "attendance_notification" with
    /// these exact 7 named body variables:
    ///   {{student_name}}
    ///   {{teacher_name}}
    ///   {{date}}             dd/MM/yyyy
    ///   {{time}}              HH:mm:ss
    ///   {{last_grade}}        "85/100 - اختبار الوحدة الأولى (12/07/2026)" or "لا يوجد"
    ///   {{last_homework}}     same format, or "لا يوجد"
    ///   {{notebook_status}}   "اسم المذكرة: مدفوعة | ..." or "لا يوجد مذكرات"
    ///
    /// Approved template body text (Arabic, category "Utility"):
    ///   "السلام عليكم ورحمة الله وبركاته تم تسجيل حضور الطالب/ة {{student_name}}
    ///    مع المدرس {{teacher_name}} بتاريخ {{date}} الساعة {{time}}.
    ///    آخر درجة: {{last_grade}}
    ///    آخر واجب: {{last_homework}}
    ///    حالة المذكرة: {{notebook_status}} شكرًا لكم."
    /// </summary>
    public string TemplateName { get; set; } = "attendance_notification";

    public string TemplateLanguageCode { get; set; } = "ar";

    /// <summary>
    /// Second approved template — sent once, right after a Student row is
    /// created, to the STUDENT's own phone (not the parent). 3 named body
    /// variables:
    ///   {{student_name}}
    ///   {{username}}
    ///   {{password}}
    ///
    /// Approved template body text (Arabic, category "Utility"):
    ///   "أهلاً بيك في AcademIQ يا {{student_name}} 🌟
    ///    اليوزر: {{username}}
    ///    الباسورد: {{password}}
    ///    ده هيبقى اكونتك لأي مدرس، وهتقدر تبدل بين المدرسين من خلال الأبليكيشن."
    /// </summary>
    public string WelcomeTemplateName { get; set; } = "student_welcome";
}
