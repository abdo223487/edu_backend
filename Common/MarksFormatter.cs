using System.Globalization;

namespace EduApi.Common;

/// <summary>
/// CenterQuizResult.Marks / HomeworkResult.Marks are decimal(5,1) so a
/// teacher can enter half marks (9.5). That column always carries one
/// decimal digit once read back from Postgres (9 is stored/read as 9.0m),
/// so a plain ToString/interpolation would print "9.0" for a whole mark.
/// Use this wherever a Marks value is embedded directly into a display
/// string (e.g. "9/10") so a whole mark still reads "9" and a half mark
/// still reads "9.5".
/// </summary>
public static class MarksFormatter
{
    public static string Format(decimal marks) => marks.ToString("0.#", CultureInfo.InvariantCulture);
}
