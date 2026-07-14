using System.Text;
using EduApi.Auth;
using EduApi.Common;
using EduApi.Data;
using EduApi.Middleware;
using EduApi.Services;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.ResponseCompression;
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

// Used by Students/import/google-sheet to download the sheet's xlsx export.
builder.Services.AddHttpClient("GoogleSheets", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Used by GreenApiWhatsAppService to call Green API (green-api.com) when
// attendance is recorded (see GreenApiOptions for config details). BaseAddress
// isn't set here since each Green API instance has its own apiUrl host —
// GreenApiWhatsAppService builds the full URL itself from GreenApiOptions.ApiUrl.
builder.Services.AddHttpClient("GreenApi", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.Configure<GreenApiOptions>(builder.Configuration.GetSection(GreenApiOptions.SectionName));
builder.Services.AddScoped<IWhatsAppService, GreenApiWhatsAppService>();

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

// ---------- Redis cache ----------
// Used to cache expensive/frequently-hit GET list endpoints (Students,
// Groups) per-tenant, so repeated requests don't re-run the same EF Core
// query every time. Connecting lazily + abortOnConnectFail=false means a
// Redis outage or bad connection string never crashes the app at startup —
// RedisCacheService just falls back to "no cache" (hits the DB directly)
// whenever the connection isn't up. Nothing here writes anything critical;
// Redis is a pure performance layer, safe to lose entirely.
builder.Services.Configure<RedisOptions>(builder.Configuration.GetSection(RedisOptions.SectionName));
builder.Services.AddSingleton(sp =>
{
    var redisOptions = builder.Configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>();
    var logger = sp.GetRequiredService<ILogger<Program>>();

    if (redisOptions == null || !redisOptions.Enabled || string.IsNullOrWhiteSpace(redisOptions.ConnectionString))
    {
        logger.LogInformation("Redis caching disabled (Redis:Enabled=false or no ConnectionString); running without a cache.");
        return new RedisConnectionHolder(null);
    }

    try
    {
        var config = ParseRedisConnectionString(redisOptions.ConnectionString);
        config.AbortOnConnectFail = false; // keep retrying in the background instead of throwing
        config.ConnectTimeout = 5000;
        var multiplexer = StackExchange.Redis.ConnectionMultiplexer.Connect(config);
        return new RedisConnectionHolder(multiplexer);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Could not connect to Redis at startup; running without a cache.");
        return new RedisConnectionHolder(null);
    }
});
builder.Services.AddSingleton<ICacheService, RedisCacheService>();

// Providers like Upstash hand out a "redis://user:pass@host:port" or
// "rediss://..." (note the extra 's' = TLS) URI, which StackExchange.Redis's
// own ConfigurationOptions.Parse does NOT understand (it expects its own
// "host:port,password=..,ssl=true" syntax). Render's Key Value gives the
// plain "host:port" form instead, which Parse handles natively. Support
// both so whichever the person pastes in just works.
static StackExchange.Redis.ConfigurationOptions ParseRedisConnectionString(string raw)
{
    if (raw.StartsWith("redis://", StringComparison.OrdinalIgnoreCase) ||
        raw.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
    {
        var uri = new Uri(raw);
        var config = new StackExchange.Redis.ConfigurationOptions
        {
            Ssl = raw.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase)
        };
        config.EndPoints.Add(uri.Host, uri.Port);

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            // Redis 6+ ACL username (Upstash's is literally "default") — only
            // set it if it's not empty/"default"-as-a-no-op; StackExchange.Redis
            // is fine either way, but skip an empty User to avoid AUTH quirks.
            if (!string.IsNullOrEmpty(parts[0])) config.User = parts[0];
            if (parts.Length > 1) config.Password = parts[1];
        }

        return config;
    }

    // Plain StackExchange.Redis syntax, e.g. "red-abc123:6379" (Render) or
    // "host:port,password=xxx,ssl=true".
    return StackExchange.Redis.ConfigurationOptions.Parse(raw);
}

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

// PERFORMANCE: this is a JSON API -- every list/detail endpoint we just
// optimized still sends its response uncompressed by default. Compression
// shrinks JSON payloads dramatically (often 70-90% for repetitive field
// names like "groupName"/"lastQuizMarks" across many rows), which matters
// more for mobile clients on slow connections than server-side query time
// does. EnableForHttps is safe here: this isn't a page that reflects a
// secret token back into the body (the classic BREACH-attack scenario),
// it's a plain data API behind bearer-token auth.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/json" });
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);

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

// ---------- Apply pending migrations ----------
// BUGFIX: nothing in this project ever called Database.Migrate() — every
// migration added to the codebase (e.g. AddStudentLectureUnlocks) had to be
// applied to the live Postgres database manually via the CLI, and that step
// was missed at least once, which meant the app's code referenced a table
// ("StudentLectureUnlocks") that never actually existed in the real database
// -> every query touching it failed with Postgres error 42P01 ("relation
// does not exist"), even though the C# code was completely correct.
//
// Safer version of "just call Migrate() unconditionally": a bare Migrate()
// call would turn a bad DB connection/permission problem into the ENTIRE app
// refusing to start (crash-loop), instead of just the one feature that
// actually needs the missing table. So here we:
//   1) only attempt it if EF actually reports pending migrations (cheap,
//      read-only check — avoids taking any DB lock on every single restart
//      when there's nothing to do),
//   2) log exactly which migrations are pending before applying them, so
//      this is visible in the deploy logs instead of a silent no-op,
//   3) catch and log a failure instead of throwing — the app still starts
//      and every OTHER feature keeps working; only whatever depends on the
//      missing table will fail, with a clear log line pointing at why.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var pending = db.Database.GetPendingMigrations().ToList();
        if (pending.Count > 0)
        {
            logger.LogWarning("Applying {Count} pending EF Core migration(s): {Migrations}",
                pending.Count, string.Join(", ", pending));
            db.Database.Migrate();
            logger.LogInformation("Migrations applied successfully.");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex,
            "Failed to apply pending EF Core migrations. The app will still start, " +
            "but any feature depending on a missing table/column will fail until this is resolved manually.");
    }
}

// ---------- Seed demo data ----------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    DbSeeder.Seed(db);
}

// ---------- Pipeline ----------
app.UseMiddleware<ExceptionMiddleware>();

// Must run before anything writes to the response body.
app.UseResponseCompression();

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();
app.UseStaticFiles(); // no longer serves /uploads/** (now on Cloudflare R2) — kept for any other static assets
app.UseCors();

app.UseAuthentication();

// SUPERADMIN CONTROL: must run after UseAuthentication (needs context.User) and
// before UseAuthorization, so a suspended tenant's request is rejected before
// it ever reaches a controller action -- see TenantSuspensionMiddleware.
app.UseMiddleware<Middleware.TenantSuspensionMiddleware>();

app.UseAuthorization();

app.MapControllers();

app.Run();
