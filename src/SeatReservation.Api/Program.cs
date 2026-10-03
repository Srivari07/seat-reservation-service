using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using SeatReservation.Api.Auth;
using SeatReservation.Api.Health;
using SeatReservation.Api.Infrastructure.Db;
using SeatReservation.Api.Infrastructure.Errors;
using SeatReservation.Api.Infrastructure.Logging;
using SeatReservation.Api.Infrastructure.Migrations;
using SeatReservation.Api.Reservations;
using SeatReservation.Api.Shows;
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

    builder.Services.AddSingleton(sp => DbGate.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
    // Singleton: it builds its connection string (MySqlConnectionStringBuilder parsing) once,
    // instead of on every request.
    builder.Services.AddSingleton(sp => new DbRunner(
        sp.GetRequiredService<IConfiguration>().GetConnectionString("Mysql"),
        sp.GetRequiredService<DbGate>(),
        sp.GetRequiredService<ILogger<DbRunner>>()));

    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<ShowMetadataCache>();
    // No scoped dependencies (DbRunner and ShowMetadataCache are both singletons already).
    builder.Services.AddSingleton<ShowService>();
    builder.Services.AddSingleton<ReservationService>();

    builder.Services.AddExceptionHandler<ApiExceptionHandler>();
    builder.Services.AddProblemDetails();

    // Minimal APIs only throw BadHttpRequestException (malformed JSON, wrong field types) when
    // this is true, and it defaults to IsDevelopment() - without it, ApiExceptionHandler would
    // never see the exception outside local dev, and the contract-shaped error body would only
    // work by accident in tests/dev. The error still reaches the client as 400 either way; this
    // only controls whether it gets a chance to be reshaped into {error,message,request_id}.
    builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

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

    // Resolved eagerly so an invalid DB_MAX_CONCURRENCY, a missing connection string, or a gate
    // larger than the pool refuses to start, instead of failing the first request that touches
    // the DB.
    app.Services.GetRequiredService<DbRunner>();

    app.UseMiddleware<RequestIdMiddleware>();
    app.UseSerilogRequestLogging();
    // Registered after (so it runs closer to the endpoint than) UseSerilogRequestLogging: an
    // exception thrown downstream reaches this middleware FIRST on its way back out and is
    // resolved into a normal 400/503 response here, so Serilog's own try/catch - further out -
    // never sees an in-flight exception and just logs the already-correct status code. The
    // reverse order would have Serilog's middleware catch-log the exception as a false 500
    // before rethrowing it down to this handler (misleading burst/alerting evidence).
    app.UseExceptionHandler();

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
    app.MapShowEndpoints();
    app.MapReservationEndpoints();

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
