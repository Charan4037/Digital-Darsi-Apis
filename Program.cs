using System.Text;
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
var jwtKey = builder.Configuration["Jwt:Key"] ?? "BagistoApiSecretKey2024VeryLongKeyForSecurity123!";
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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context => Task.CompletedTask,
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token))
                    context.Token = token;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

// ─── Services ────────────────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<LocaleContext>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ProductService>();
builder.Services.AddScoped<CartService>();
builder.Services.AddScoped<CheckoutService>();
builder.Services.AddScoped<AccountService>();

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
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[DeliveryTypeSeeder] Bootstrap failed: {ex.Message}");
    }
}

app.UseCors();

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

app.UseAuthentication();
app.UseAuthorization();

// Serve product images from local storage folder
var storagePath = System.IO.Path.Combine(app.Environment.ContentRootPath, "storage");
if (Directory.Exists(storagePath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(storagePath),
        RequestPath = "/storage"
    });
}

// REST API Controllers
app.MapControllers();

// GraphQL endpoints matching Bagisto's URL structure
app.MapGraphQL("/api/graphql").AllowAnonymous();
app.MapGraphQL("/graphql").AllowAnonymous();

// Health check
app.MapGet("/", () => Results.Ok(new { status = "running", api = "BagistoApi .NET 8", graphql = "/api/graphql" }));

Console.WriteLine("═══════════════════════════════════════════");
Console.WriteLine("  BagistoApi .NET 8 Backend");
Console.WriteLine("  GraphQL:    http://0.0.0.0:8000/api/graphql");
Console.WriteLine("  Playground: http://0.0.0.0:8000/graphql");
Console.WriteLine("  Swagger:    http://0.0.0.0:8000/swagger");
Console.WriteLine("═══════════════════════════════════════════");

app.Run("http://0.0.0.0:8000");
