using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace EduApi.Auth;

public class TokenService
{
    private readonly IConfiguration _config;

    public TokenService(IConfiguration config)
    {
        _config = config;
    }

    /// <summary>
    /// Builds the access token. The client (services.dart) decodes the JWT payload
    /// client-side and reads: role (string OR array), groupId, schoolYear, exp.
    /// `unitIds` is a NEW comma-separated claim (only set for students) listing the
    /// UnitIds they're subscribed to (StudentUnitSubscription), so UnitsController
    /// can filter "GET Units" down to only what the student is actually subscribed
    /// to, instead of every unit in their school year.
    /// </summary>
    public string CreateAccessToken(
        string subjectId,
        string username,
        IEnumerable<string> roles,
        int? groupId = null,
        int? schoolYear = null,
        int? tenantId = null,
        IEnumerable<int>? unitIds = null)
    {
        var jwtSection = _config.GetSection("Jwt");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSection["Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subjectId),
            new(ClaimTypes.Name, username),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim("role", role));
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (groupId.HasValue)
            claims.Add(new Claim("groupId", groupId.Value.ToString()));

        if (schoolYear.HasValue)
            claims.Add(new Claim("schoolYear", schoolYear.Value.ToString()));

        // TENANT LAYER: embedded only for staff (Teacher/Assistant/AssistantAdmin).
        // Students resolve their tenant from the X-TenantId header instead (they
        // may not have chosen/stored one yet at the time they log in).
        if (tenantId.HasValue)
            claims.Add(new Claim("tenantId", tenantId.Value.ToString()));

        var unitIdList = unitIds?.ToList();
        if (unitIdList is { Count: > 0 })
            claims.Add(new Claim("unitIds", string.Join(",", unitIdList)));

        var minutes = double.Parse(jwtSection["AccessTokenMinutes"] ?? "20");

        var token = new JwtSecurityToken(
            issuer: jwtSection["Issuer"],
            audience: jwtSection["Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(minutes),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string CreateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(bytes);
    }
}
