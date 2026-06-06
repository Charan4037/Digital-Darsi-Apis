using System.Text;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using BagistoApi.Data;
using BagistoApi.GraphQL.Mutations;
using BagistoApi.GraphQL.Queries;
using BagistoApi.Services;

var builder = WebApplication.CreateBuilder(args);

// ─── Database ────────────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("Bagisto")!;
builder.Services.AddDbContext<BagistoDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString),
        mysql => mysql.EnableRetryOnFailure(3)));

// ─── Authentication (JWT) ────────────────────────────────────────────────
// The signing key MUST be configured (Jwt:Key in appsettings) and at least
// 32 chars — symmetric HMAC-SHA256 keys shorter than that are unsafe and
// .NET will reject them at validation time anyway. We fail-fast on startup
// rather than letting the API silently issue tokens with a weak key.
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
{
    throw new InvalidOperationException(
        "Jwt:Key must be configured and at least 32 characters long. " +
        "Set it in appsettings.json or via the JWT__KEY environment variable.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "BagistoApi",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "BagistoApp",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            // Default is 5 minutes which is generous given access tokens
            // expire in 30 — clamp it so an expired token isn't accepted
            // for an extra 5 minutes.
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        options.Events = new JwtBearerEvents
        {
            // Surface "expired" so the Flutter client can treat 401 +
            // Token-Expired header as "refresh and retry".
            OnAuthenticationFailed = context =>
            {
                if (context.Exception is SecurityTokenExpiredException)
                    context.Response.Headers["Token-Expired"] = "true";
                return Task.CompletedTask;
            },
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token))
                    context.Token = token;
                return Task.CompletedTask;
            }
        };
    });
// Default-deny: any endpoint without an explicit [AllowAnonymous] requires
// an authenticated principal. This stops new controllers from accidentally
// shipping unauthenticated; intentionally-public endpoints must opt out.
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// ─── Firebase Admin (phone OTP login) ────────────────────────────────────
// FirebaseApp is a process-wide singleton. We init at most once and only if
// a service-account credentials file is configured — without this, the
// /firebase-login endpoint returns 503 instead of crashing the whole API.
//
// Configure via either:
//   "Firebase": { "ServiceAccountPath": "path/to/serviceAccount.json", "ProjectId": "..." }
//   GOOGLE_APPLICATION_CREDENTIALS env var (standard Google ADC)
var firebaseSaPath = builder.Configuration["Firebase:ServiceAccountPath"];
var firebaseProjectId = builder.Configuration["Firebase:ProjectId"];
if (FirebaseApp.DefaultInstance == null)
{
    try
    {
        GoogleCredential? cred = null;
        if (!string.IsNullOrWhiteSpace(firebaseSaPath) && File.Exists(firebaseSaPath))
            cred = GoogleCredential.FromFile(firebaseSaPath);
        else if (!string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS")))
            cred = GoogleCredential.GetApplicationDefault();

        if (cred != null)
        {
            FirebaseApp.Create(new AppOptions
            {
                Credential = cred,
                ProjectId = firebaseProjectId,
            });
            Console.WriteLine($"[Firebase] Initialized (projectId={firebaseProjectId ?? "auto"}).");
        }
        else
        {
            Console.WriteLine(
                "[Firebase] Not configured. /api/v1/customer/firebase-login will return 503.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Firebase] Init failed: {ex.Message}");
    }
}

// ─── Services ────────────────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<LocaleContext>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ProductService>();
builder.Services.AddScoped<CartService>();
builder.Services.AddScoped<CheckoutService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<NotificationService>();

// Named HTTP client used by the image migration to download source images.
// 30-second timeout per image; User-Agent identifies the requester.
builder.Services.AddHttpClient("ImageDownloader", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DigitalDarsiApi/1.0 ImageMigration");
});

// Firebase Storage service: downloads source images and uploads them to
// Firebase Storage. Registered as singleton because StorageClient is
// thread-safe and expensive to construct.
builder.Services.AddSingleton<FirebaseStorageService>();

// ─── GraphQL (HotChocolate) ──────────────────────────────────────────────
builder.Services
    .AddGraphQLServer()
    .AddQueryType(d => d.Name("Query"))
    .AddTypeExtension<ProductQueries>()
    .AddTypeExtension<CheckoutQueries>()
    .AddTypeExtension<AccountQueries>()
    .AddMutationType(d => d.Name("Mutation"))
    .AddTypeExtension<AuthMutations>()
    .AddTypeExtension<CartMutations>()
    .AddTypeExtension<CheckoutMutations>()
    .AddTypeExtension<AccountMutations>()
    .AddAuthorization()
    .ModifyRequestOptions(opt =>
    {
        opt.IncludeExceptionDetails = true;
    });

// ─── Controllers ────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        o.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

// ─── Swagger / OpenAPI ───────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "BagistoApi",
        Version = "v1",
        Description = "BagistoApi .NET 8 Backend – REST & GraphQL"
    });

    // JWT Bearer auth in Swagger UI
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Enter your JWT token"
    });
    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// ─── CORS ────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();

// Ensure the delivery_types table exists and is seeded with the 3 delivery
// options (Express / Normal / Free). Safe to run on every startup.
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<BagistoDbContext>();
        await DeliveryTypeSeeder.EnsureTableAndSeedAsync(db);
        await RefreshTokenSeeder.EnsureTableAsync(db);
        await DeviceTokenSeeder.EnsureTableAsync(db);
        await GuestDeviceTokenSeeder.EnsureTableAsync(db);

        // Ensure the storefront category tree is properly nested (sub-categories
        // linked under their real parents). Idempotent and runs in every
        // environment, so dev and prod stay consistent on the same database.
        // No-ops when the catalogue is already correctly linked.
        var relink = await DigitalDarsiSeeder.RelinkCategoryHierarchyAsync(db);
        if (relink.Updated > 0)
            Console.WriteLine($"[Startup] Category hierarchy: relinked {relink.Updated} categories.");

        // Hide category pages not in the storefront navigation menus, and
        // order the rest the way the website menu lists them. Idempotent.
        var prune = await DigitalDarsiSeeder.PruneOrphanCategoriesAsync(db);
        if (prune.Hidden > 0 || prune.Reordered > 0)
            Console.WriteLine($"[Startup] Category menu sync: hid {prune.Hidden} orphan(s), "
                + $"reordered {prune.Reordered} to website order.");

        // Backfill category logos that came through as the bare site URL
        // with a representative product image. Idempotent.
        var imgFix = await DigitalDarsiSeeder.BackfillCategoryImagesAsync(db);
        if (imgFix > 0)
            Console.WriteLine($"[Startup] Category images: backfilled {imgFix} from products.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Startup] Seeder bootstrap failed: {ex.Message}");
    }
}

// ─── Image migration (background, runs once on startup) ──────────────────
// Downloads every product image and category logo/banner fresh from the
// original source URLs and uploads them to Firebase Storage. Idempotent:
// images already pointing at Firebase Storage are silently skipped, so
// this is safe to run on every startup even after migration is complete.
_ = Task.Run(async () =>
{
    try
    {
        using var migScope = app.Services.CreateScope();
        var migDb      = migScope.ServiceProvider.GetRequiredService<BagistoDbContext>();
        var migStorage = migScope.ServiceProvider.GetRequiredService<FirebaseStorageService>();

        Console.WriteLine("[ImageMigration] Starting...");
        var report = await ImageMigrationRunner.RunAsync(migDb, migStorage, Console.Out);
        Console.WriteLine(
            $"[ImageMigration] Complete — " +
            $"products: {report.ProductImages.Migrated} migrated / " +
            $"{report.ProductImages.Skipped} skipped / " +
            $"{report.ProductImages.Failed} failed | " +
            $"categories: {report.CategoryImages.Migrated} migrated / " +
            $"{report.CategoryImages.Skipped} skipped / " +
            $"{report.CategoryImages.Failed} failed");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ImageMigration] Failed: {ex.Message}");
    }
});

app.UseCors();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        var ex = feature?.Error;
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        var msg = app.Environment.IsDevelopment() && ex != null
            ? ex.ToString()
            : ex?.Message ?? "Internal server error";
        await context.Response.WriteAsJsonAsync(new { error = msg });
    });
});

// ─── Swagger middleware ──────────────────────────────────────────────────
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "BagistoApi v1");
    options.RoutePrefix = "swagger";
});

// Storefront Key validation middleware
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";

    if (path.StartsWith("/api/graphql") && context.Request.Method == "POST")
    {
        var storefrontKey = context.Request.Headers["X-STOREFRONT-KEY"].FirstOrDefault();
        var expectedKey = app.Configuration["App:StorefrontKey"];

        if (!string.IsNullOrEmpty(storefrontKey) && storefrontKey != expectedKey)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BagistoDbContext>();
            var valid = await db.StorefrontKeys.AnyAsync(k => k.Key == storefrontKey && k.IsActive);
            if (!valid)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Invalid storefront key." });
                return;
            }
        }
    }

    await next();
});

// Serve product images from local storage folder — must come BEFORE
// UseAuthentication/UseAuthorization so the FallbackPolicy (RequireAuthenticatedUser)
// doesn't block unauthenticated access to public static assets.
var storagePath = System.IO.Path.Combine(app.Environment.ContentRootPath, "storage");
if (Directory.Exists(storagePath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(storagePath),
        RequestPath = "/storage"
    });
}

app.UseAuthentication();
app.UseAuthorization();

// REST API Controllers
app.MapControllers();

// GraphQL endpoints matching Bagisto's URL structure
app.MapGraphQL("/api/graphql").AllowAnonymous();
app.MapGraphQL("/graphql").AllowAnonymous();

// Health check
app.MapGet("/", () => Results.Ok(new { status = "running", api = "BagistoApi .NET 8", graphql = "/api/graphql" }))
   .AllowAnonymous();

Console.WriteLine("═══════════════════════════════════════════");
Console.WriteLine("  BagistoApi .NET 8 Backend");
Console.WriteLine("  GraphQL:    http://0.0.0.0:8000/api/graphql");
Console.WriteLine("  Playground: http://0.0.0.0:8000/graphql");
Console.WriteLine("  Swagger:    http://0.0.0.0:8000/swagger");
Console.WriteLine("═══════════════════════════════════════════");

app.Run("http://0.0.0.0:8000");
