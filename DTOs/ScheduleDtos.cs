namespace EduApi.DTOs;

// GET Schedule?schoolYear=X&year=Y&month=M returns only the dates in that
// month that actually have a lesson set -- everything else is implicitly
// empty, so the Flutter calendar just puts a dot on the dates it gets back.
public record ClassScheduleEntryDto(DateOnly Date, string? Text);

// POST Schedule/save upserts (or, if Text is empty, deletes) a SINGLE date --
// that's how the calendar UI works: tap a day, type/clear the lesson, save
// just that day. Not a whole-month/whole-week save.
public record SaveScheduleEntryRequest(int SchoolYear, DateOnly Date, string? Text);
