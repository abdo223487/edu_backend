namespace EduApi.DTOs;

/// <summary>
/// multipart/form-data body shared by every create/edit/reply endpoint of
/// ContactMessagesController. Text is required by the endpoints (validated there);
/// Image is optional. RemoveImage = true clears the existing image when no new
/// Image is supplied (ignored on create).
///
/// SolutionSourceId / PageNumber / QuestionNumber are OPTIONAL and only read by
/// create + edit of the opening question (ignored by replies). On EDIT they
/// REPLACE the stored values: omitting them clears the source/page/question.
/// PageNumber / QuestionNumber are only kept when a SolutionSourceId is given.
/// </summary>
public class ContactMessageForm
{
    public string? Text { get; set; }
    public IFormFile? Image { get; set; }
    public bool RemoveImage { get; set; }

    public int? SolutionSourceId { get; set; }
    public string? PageNumber { get; set; }
    public string? QuestionNumber { get; set; }
}
