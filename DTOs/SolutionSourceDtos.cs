namespace EduApi.DTOs;

/// <summary>POST SolutionSources (staff) body.</summary>
public record SolutionSourceRequest(int SchoolYear, string Name);

/// <summary>POST SolutionSources/{id}/edit (staff) body -- rename only (the school year is fixed).</summary>
public record SolutionSourceRenameRequest(string Name);
