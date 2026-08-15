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
    /// these exact 7 named body variables. NOTE: {{last_grade}} and
    /// {{last_homework}} are numeric marks ONLY ("17/20") -- never a quiz
    /// or homework title/name (no "اختبار الوحدة", no "سنتر كويز", etc.).
    ///   {{student_name}}
    ///   {{teacher_name}}
    ///   {{date}}             dd/MM/yyyy
    ///   {{time}}              HH:mm:ss
    ///   {{last_grade}}        "17/20" or "لا يوجد"
    ///   {{last_homework}}     same format, or "لا يوجد"
    ///   {{notebook_line}}     the ENTIRE last line, fully backend-composed.
    ///                         Always ends with the same encouraging closing
    ///                         sentence (with the teacher's name baked in);
    ///                         when the student has a notebook, a
    ///                         "📒 حالة المذكرة: ..." line (with emojis) is
    ///                         prepended before the closing sentence. Never
    ///                         blank/empty -- there's always at least the
    ///                         closing sentence.
    ///
    /// IMPORTANT: the approved template body must have NO static text
    /// wrapping this variable -- it must be the entire last line by itself,
    /// e.g.:
    ///   "السلام عليكم ورحمة الله وبركاته 🤍
    ///
    ///    تم تسجيل حضور الطالب/ة {{student_name}}
    ///    مع المدرس {{teacher_name}}
    ///
    ///    بتاريخ {{date}} 📅
    ///    الساعة {{time}} 🕒
    ///
    ///    آخر درجة: {{last_grade}} 📊
    ///    آخر واجب: {{last_homework}} 📚
    ///
    ///    {{notebook_line}}"
    /// (NOT "حالة المذكرة: {{notebook_line}} شكرًا لكم." -- {{notebook_line}}
    /// already carries its own full closing text, so wrapping it in more
    /// static text would duplicate/garble the message.)
    /// </summary>
    public string TemplateName { get; set; } = "attendance_notification";

    public string TemplateLanguageCode { get; set; } = "ar_EG";

    /// <summary>
    /// Second approved template — sent once, right after a Student row is
    /// created (via manual Create, Excel import, Google Sheet import, or the
    /// mapped import), to the STUDENT's own phone. 4 named body variables
    /// (the store links are sent as VARIABLES, not static text, so they can
    /// be changed here in config without ever touching the approved
    /// template again):
    ///   {{username}}
    ///   {{password}}
    ///   {{android_link}}
    ///   {{ios_link}}
    ///
    /// Approved template body text (Arabic, category "Utility"):
    ///   "السلام عليكم ورحمة الله وبركاته ازيك يا حبيبي
    ///    يشرفنا انضمامك لينا على Ednova وبنتمنالك تجربة سعيدة ومتميزة.
    ///    تقدر تنزل الأبليكيشن:
    ///    لو انت اندرويد: {{android_link}}
    ///    لو انت iOS: {{ios_link}}
    ///    وبعد ما تخلص تقدر تستخدم:
    ///    {{username}}
    ///    {{password}}
    ///    وفي الآخر، نتمنى ليك رحلة تعليمية متميزة."
    ///
    /// NOTE: deliberately no student name and no emojis on the
    /// username/password lines, per product requirement — those two lines
    /// must be sent bare, one under the other.
    /// </summary>
    public string WelcomeTemplateName { get; set; } = "student_welcome";

    /// <summary>Play Store link sent as the {{android_link}} variable in the welcome template/message.</summary>
    public string AndroidStoreUrl { get; set; } = "https://play.google.com/store/apps/details?id=com.AcademIQv2.app";

    /// <summary>App Store link sent as the {{ios_link}} variable in the welcome template/message.</summary>
    public string IosStoreUrl { get; set; } = "https://apps.apple.com/app/ednova/id6796147882";

    /// <summary>
    /// Third approved template — sent to EVERY parent of EVERY student in a
    /// Group when the teacher marks a lesson as finished ("الانصراف"). 5
    /// named body variables:
    ///   {{teacher_name}}
    ///   {{lesson_title}}
    ///   {{group_name}}
    ///   {{date}}              dd/MM/yyyy
    ///   {{time}}              HH:mm
    ///
    /// Approved template body text (Arabic, category "Utility"):
    ///   "السلام عليكم ورحمة الله وبركاته،
    ///    نحيط سيادتكم علمًا بانتهاء حصة الاستاذ / {{teacher_name}}،
    ///    والتي كانت بعنوان/ {{lesson_title}}
    ///    لطلاب مجموعة/ {{group_name}}
    ///    وذلك يوم/ {{date}}
    ///    الساعة/ {{time}}"
    /// </summary>
    public string DismissalTemplateName { get; set; } = "dismissal_notification";

    /// <summary>
    /// Fourth approved template — sent to the parent right after a student
    /// submits an ONLINE QUIZ with the teacher (QuizzesController.Grade). 4
    /// named body variables:
    ///   {{student_name}}
    ///   {{exam_title}}
    ///   {{teacher_name}}
    ///   {{score}}          "17/20" (already formatted "score/total")
    ///
    /// Approved template body text (Arabic, category "Utility"):
    ///   "السلام عليكم ورحمة الله وبركاته ولي أمر الطالب/ {{student_name}}
    ///    نحيط علم سيادتكم أن الطالب قد أنهى امتحان {{exam_title}}
    ///    مع المدرس/ {{teacher_name}} بنجاح، وقد حصل على {{score}}
    ///    شكرًا لكم."
    /// </summary>
    public string QuizResultTemplateName { get; set; } = "quiz_result_notification";

    /// <summary>
    /// Fifth approved template — same shape as QuizResultTemplateName, sent
    /// after a student submits an ASSIGNMENT/"واجب" (AssignmentsController.Submit).
    /// Same 4 named body variables ({{student_name}}, {{exam_title}},
    /// {{teacher_name}}, {{score}}).
    ///
    /// Approved template body text (Arabic, category "Utility"):
    ///   "السلام عليكم ورحمة الله وبركاته ولي أمر الطالب/ {{student_name}}
    ///    نحيط علم سيادتكم أن الطالب قد أنهى واجب {{exam_title}}
    ///    مع المدرس/ {{teacher_name}} بنجاح، وقد حصل على {{score}}
    ///    شكرًا لكم."
    /// </summary>
    public string AssignmentResultTemplateName { get; set; } = "assignment_result_notification";
}
