namespace EduApi.DTOs;

/// <summary>
/// multipart/form-data body shared by every create/edit/reply endpoint of
/// ContactMessagesController. Text is required by the endpoints (validated there);
/// Image is optional. RemoveImage = true clears the existing image when no new
/// Image is supplied (ignored on create).
/// </summary>
public class ContactMessageForm
{
    public string? Text { get; set; }
    public IFormFile? Image { get; set; }
    public bool RemoveImage { get; set; }
}
