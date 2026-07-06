namespace EduApi.DTOs;

// POST Auth/login  body: { "username": "...", "password": "..." }
public record LoginRequest(string Username, string Password);

// POST Auth/refresh  body: { "refreshToken": "..." }
public record RefreshRequest(string RefreshToken);

// Response for both login and refresh:
// { "token": "...", "refreshToken": "..." }
// The role/groupId/schoolYear are read by the client from the JWT payload itself,
// not from separate response fields.
public record AuthResponse(string Token, string RefreshToken);
