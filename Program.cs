using System.Text;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;   
using Microsoft.IdentityModel.Tokens;
using Serilog;
using DOSApi.Data;
using DOSApi.GraphQL.Mutations;
using DOSApi.GraphQL.Queries;
using DOSApi.Services;
using DOSApi.Infrastructure;
using DOSApi.Middleware;

var builder = WebApplication.CreateBuilder(args);

// ─── Logging (Serilog) ───────────────────────────────────────────────────
// MUST be called BEFORE building the host to ensure all startup logs are captured
LoggingConfiguration.ConfigureSerilog(builder);

try
{
    Log.Information("Application starting...");

    // ─── Database ────────────────────────────────────────────────────────────
    var connectionString = builder.Configuration.GetConnectionString("DOS")!;
    builder.Services.AddDbContext<DOSDbContext>(options =>
        options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString),
            mysql => mysql.EnableRetryOnFailure(3))
               .AddInterceptors(new MySql51Interceptor()));

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
                ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "DOSApi",
                ValidAudience = builder.Configuration["Jwt:Audience"] ?? "DOSApp",
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
                Log.Information("Firebase initialized (projectId={FirebaseProjectId})", firebaseProjectId ?? "auto");
            }
            else
            {
                Log.Warning("Firebase not configured. /api/v1/customer/firebase-login will return 503.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Firebase initialization failed");
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
    builder.Services.AddScoped<ServiceAreaService>();
    builder.Services.AddScoped<ExtraChargeService>();
    builder.Services.AddScoped<DeliveryChargeService>();
    builder.Services.AddScoped<NotificationService>();
    builder.Services.AddScoped<VendorAggregationService>();
    builder.Services.AddScoped<OrderInvoiceService>();

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
            Title = "Digital Darsi API",
            Version = "v1",
            Description = "Digital Darsi .NET 8 Backend – REST & GraphQL\n\n" +
                          "**Admin endpoints** require the `X-Admin-Key` header. Click **Authorize** (top right) and enter the admin key.\n\n" +
                          "**Customer endpoints** require a JWT Bearer token obtained from the login endpoint."
        });

        // Use fully qualified type names as schema IDs to avoid conflicts when
        // different namespaces have classes with the same name
        options.CustomSchemaIds(type => type.FullName?.Replace("+", "."));

        // Guarantee unique operation IDs when multiple controllers share method names.
        // Minimal API endpoints (health check, GraphQL) don't have controller/action route values.
        options.CustomOperationIds(e =>
        {
            e.ActionDescriptor.RouteValues.TryGetValue("controller", out var controller);
            e.ActionDescriptor.RouteValues.TryGetValue("action", out var action);
            return controller != null ? $"{controller}_{action}" : null;
        });

        // Use full type name so nested request classes with the same simple name
        // (e.g. AuthController.RegisterRequest vs ShopCustomerController.RegisterRequest)
        // get distinct schema IDs instead of colliding.
        options.CustomSchemaIds(type => type.FullName?.Replace("+", "."));

        // Include XML comments so Swagger shows full descriptions and param docs
        var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
        var xmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, xmlFile);
        if (System.IO.File.Exists(xmlPath))
            options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);

        // JWT Bearer — for customer-facing endpoints
        options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Description = "Enter your JWT access token (customer login)"
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

        // Admin API key — for all /api/v1/admin/* endpoints
        options.AddSecurityDefinition("AdminKey", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Name = "X-Admin-Key",
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Description = "Admin API key — value from Admin:NotificationApiKey in appsettings"
        });
        options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
        {
            {
                new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                {
                    Reference = new Microsoft.OpenApi.Models.OpenApiReference
                    {
                        Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                        Id = "AdminKey"
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

    // ─── Startup bootstrap (schema tables only) ──────────────────────────────
    // Only ensures custom tables that don't exist in the original DOS schema.
    // All scraping/migration/sync seeders have been removed — data import is
    // complete and categories are managed going forward via the admin API.
    using (var scope = app.Services.CreateScope())
    {
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<DOSDbContext>();
            await DeliveryTypeSeeder.EnsureTableAndSeedAsync(db);
            await RefreshTokenSeeder.EnsureTableAsync(db);
            await DeviceTokenSeeder.EnsureTableAsync(db);
            await GuestDeviceTokenSeeder.EnsureTableAsync(db);
            await NotificationSeeder.EnsureTableAsync(db);
            await VendorCatalogSeeder.EnsureTableAndSeedAsync(db);
            await RbacSeeder.EnsureTableAndSeedAsync(db);
            await ServiceablePincodeSeeder.EnsureTableAndSeedAsync(db);
            await ExtraChargeSeeder.EnsureTableAsync(db);
            await DigitalDarsiSeeder.SeedFromStagingAsync(db);

            // Repair product variant pricing (one-time fix for 423 affected products)
            // DISABLED: This migration was incorrectly resetting parent product prices
            // await ProductVariantPriceRepairMigration.RepairAllProductsAsync(db);

            Log.Information("Seeder bootstrap completed successfully");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Seeder bootstrap failed");
        }
    }

    app.UseCors();

    // ─── Global Exception Handling Middleware ────────────────────────────────
    // Must be registered FIRST in the middleware pipeline to catch all exceptions
    app.UseMiddleware<GlobalExceptionHandlerMiddleware>();

    // The production host is shared Windows/IIS hosting with the WebDAV
    // Publishing module enabled at the server level, which claims PUT/DELETE
    // verbs before they ever reach Kestrel/this app (IIS returns a bare 405,
    // no auth middleware runs). The Flutter client works around this by
    // sending POST + `X-Http-Method-Override: PUT|DELETE`; this middleware
    // rewrites the request method back before routing so [HttpPut]/[HttpDelete]
    // actions still match normally. This app never calls UseRouting()
    // explicitly elsewhere — it relies on the minimal-hosting-model's implicit
    // auto-insertion, which runs BEFORE any middleware we add here, so without
    // an explicit UseRouting() call right after the override, routing sees the
    // original POST and 405s before the rewritten method ever takes effect.
    app.UseHttpMethodOverride();
    app.UseRouting();

    // ─── Swagger middleware ──────────────────────────────────────────────────
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "DOSApi v1");
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
                var db = scope.ServiceProvider.GetRequiredService<DOSDbContext>();
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

    // GraphQL endpoints matching DOS's URL structure
    app.MapGraphQL("/api/graphql").AllowAnonymous();
    app.MapGraphQL("/graphql").AllowAnonymous();

    // Health check
    app.MapGet("/", () => Results.Ok(new { status = "running", api = "DOSApi .NET 8", graphql = "/api/graphql" }))
       .AllowAnonymous();

    Log.Information("Application started successfully");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}



