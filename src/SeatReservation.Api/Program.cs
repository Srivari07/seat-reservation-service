using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using SeatReservation.Api.Auth;
using SeatReservation.Api.Health;
using SeatReservation.Api.Infrastructure.Errors;
using SeatReservation.Api.Infrastructure.Logging;
using SeatReservation.Api.Infrastructure.Migrations;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    var port = Environment.GetEnvironmentVariable("PORT");
    if (!string.IsNullOrWhiteSpace(port))
    {
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
    }

    builder.Host.UseSerilog((context, services, loggerConfig) => loggerConfig
        .Enrich.FromLogContext()
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console(new CompactJsonFormatter()));

    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    });

    builder.Services.AddSingleton<MigrationsState>();
    builder.Services.AddSingleton<MigrationRunner>();
    builder.Services.AddHostedService<MigrationRunnerHostedService>();

    builder.Services.AddHealthChecks()
        .AddCheck("live", () => HealthCheckResult.Healthy(), tags: ["live"])
        .AddCheck<MySqlReadyHealthCheck>("ready", tags: ["ready"]);

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton<JwtTokenIssuer>();
    builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer();

    // Configured via IConfiguration (resolved lazily from DI, after the host is built) rather
    // than reading builder.Configuration directly above: WebApplicationFactory-based tests splice
    // their config overrides in at Build(), so an eager read here would see only the real process
    // environment and break under test.
    builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Configure<IConfiguration>((options, configuration) =>
        {
            var signingKey = configuration["JWT_SIGNING_KEY"]
                ?? throw new InvalidOperationException("JWT_SIGNING_KEY is required");

            // Keeps the "sub"/"role" claim names exactly as issued, instead of the handler's
            // default remapping to long ClaimTypes.* URIs, which ICurrentUser and the AdminOnly
            // policy below both rely on.
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(signingKey)),
            };
            options.Events = new JwtBearerEvents
            {
                OnChallenge = context =>
                {
                    context.HandleResponse();
                    return ApiError.Write(context.HttpContext, StatusCodes.Status401Unauthorized, "unauthorized", "Missing or invalid token.")
                        .ExecuteAsync(context.HttpContext);
                },
                OnForbidden = context =>
                    ApiError.Write(context.HttpContext, StatusCodes.Status403Forbidden, "forbidden", "You do not have permission to perform this action.")
                        .ExecuteAsync(context.HttpContext),
            };
        });

    builder.Services.AddAuthorizationBuilder()
        .AddPolicy("AdminOnly", policy => policy.RequireClaim("role", "admin"));

    var app = builder.Build();

    // Fail fast on real startup (07-deploy.md: "refuse to start without it"). Read from
    // app.Configuration, not builder.Configuration, so this still sees WebApplicationFactory's
    // test overrides, which are only merged in once Build() above has run. Decoding here too
    // (duplicating the AddOptions<JwtBearerOptions> callback above) matters: AddAuthentication's
    // default scheme runs on every request, not just protected ones, so a bad key left to fail
    // lazily there would 500 the whole API (I2), instead of refusing to start.
    var jwtSigningKey = app.Configuration["JWT_SIGNING_KEY"];
    if (string.IsNullOrWhiteSpace(jwtSigningKey))
    {
        throw new InvalidOperationException("JWT_SIGNING_KEY is required");
    }

    try
    {
        Convert.FromBase64String(jwtSigningKey);
    }
    catch (FormatException ex)
    {
        throw new InvalidOperationException("JWT_SIGNING_KEY must be valid base64.", ex);
    }

    app.UseMiddleware<RequestIdMiddleware>();
    app.UseSerilogRequestLogging();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("live"),
    });

    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
    });

    app.MapAuthEndpoints();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// Top-level statements generate an internal Program class; WebApplicationFactory<Program>
// in the test project needs it to be public.
public partial class Program;
