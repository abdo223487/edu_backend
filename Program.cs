using System.Text;
using EduApi.Auth;
using EduApi.Common;
using EduApi.Data;
using EduApi.Middleware;
using EduApi.Services;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// ---------- Services ----------
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // The Flutter client declares many IDs as `String` (e.g. `final String studentId`)
        // and may send them as JSON strings in request bodies (e.g. {"studentId": "5"}).
        // Allow numeric strings to bind to int/long properties so those requests don't
        // fail with a 400 due to a strict type mismatch.
        options.JsonSerializerOptions.NumberHandling =
            System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString;
    });
builder.Services.AddHttpContextAccessor();

// TENANT LAYER: resolves the current request's tenant (Teacher.Id) from the JWT
// (staff) or the X-TenantId header (students). Consumed by AppDbContext's global
// query filters. Scoped so it's computed fresh per-request from HttpContext.User.
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
// Previously: options.UseInMemoryDatabase("EduApiDb")
// Now connected to a persistent PostgreSQL database (Neon).

builder.Services.AddSingleton<TokenService>();

// ---------- Cloudflare R2 (S3-compatible) file storage ----------
// To go back to local-disk storage for local dev/testing, comment the 3 lines
// below and uncomment: builder.Services.AddScoped<IFileStorageService, FileStorageService>();
builder.Services.Configure<R2Options>(builder.Configuration.GetSection(R2Options.SectionName));
builder.Services.AddSingleton<Amazon.S3.IAmazonS3>(sp =>
{
    var r2 = builder.Configuration.GetSection(R2Options.SectionName).Get<R2Options>()
             ?? throw new InvalidOperationException("Missing 'R2' configuration section.");

    var config = new Amazon.S3.AmazonS3Config
    {
        ServiceURL = $"https://{r2.AccountId}.r2.cloudflarestorage.com",
        ForcePathStyle = true, // required by R2's S3-compatible API

        // AWSSDK.S3 3.7.400+ defaults to signing uploads with a streaming
        // "aws-chunked" body (optionally + a trailing checksum). Cloudflare R2
        // doesn't implement EITHER streaming-signed-payload variant and
        // rejects every upload with "not implemented" — force the SDK back to
        // classic single-shot SigV4 signing (whole payload hashed up front,
        // no chunking), which R2 accepts.
        RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED,
    };
    return new Amazon.S3.AmazonS3Client(
        new Amazon.Runtime.BasicAWSCredentials(r2.AccessKey, r2.SecretKey), config);
});
builder.Services.AddScoped<IFileStorageService, R2FileStorageService>();
// builder.Services.AddScoped<IFileStorageService, FileStorageService>(); // local-disk fallback

var jwtSection = builder.Configuration.GetSection("Jwt");
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    // Without this, .NET silently remaps the short "role" claim to the long
    // ClaimTypes.Role URI on inbound tokens, which breaks RoleClaimType =
    // "role" below (no claim is left with that exact type -> IsInRole()
    // always returns false -> every [Authorize(Roles=...)] endpoint 403s
    // even with a perfectly valid token).
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSection["Issuer"],
        ValidAudience = jwtSection["Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSection["Key"]!)),
        RoleClaimType = "role"
    };
});

builder.Services.AddAuthorization();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "EduApi", Version = "v1" });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// ---------- Seed demo data ----------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    DbSeeder.Seed(db);
}

// ---------- Pipeline ----------
app.UseMiddleware<ExceptionMiddleware>();

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();
app.UseStaticFiles(); // no longer serves /uploads/** (now on Cloudflare R2) — kept for any other static assets
app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
