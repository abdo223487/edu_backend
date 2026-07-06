namespace EduApi.Common;

/// <summary>
/// The Flutter client always decodes list endpoints as a raw JSON array
/// (`jsonDecode(response.body)` cast to `List<dynamic>`), never as a wrapped
/// object with metadata. Pagination is therefore driven purely by the `p`
/// (page) query parameter with a fixed page size, and an empty array signals
/// "no more pages" (see `hasMore = false` when `data.isEmpty`).
/// </summary>
public static class PagingDefaults
{
    public const int PageSize = 20;
}

public class CurrentUser
{
    public required string Id { get; set; }
    public required string Role { get; set; } // "teacher" | "student" | "assistant"
    public int? GroupId { get; set; }
    public int? SchoolYear { get; set; }
}
