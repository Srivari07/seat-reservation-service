using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Prometheus;
using Prometheus.HttpMetrics;
using SeatReservation.Api.Auth;
using SeatReservation.Api.Health;
using SeatReservation.Api.Infrastructure.Db;
using SeatReservation.Api.Infrastructure.Errors;
using SeatReservation.Api.Infrastructure.Logging;
using SeatReservation.Api.Infrastructure.Metrics;
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
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
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
    // Own CollectorRegistry per app instance, not Metrics.DefaultRegistry: see AppMetrics for why
    // (a WebApplicationFactory-based test boots many hosts in one process).
    builder.Services.AddSingleton<AppMetrics>();
    // Singleton: it builds its connection string (MySqlConnectionStringBuilder parsing) once,
    // instead of on every request.
    builder.Services.AddSingleton(sp => new DbRunner(
        sp.GetRequiredService<IConfiguration>().GetConnectionString("Mysql"),
        sp.GetRequiredService<DbGate>(),
        sp.GetRequiredService<ILogger<DbRunner>>(),
        sp.GetRequiredService<AppMetrics>()));
    builder.Services.AddSingleton<ShowGaugeCollector>();

    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<ShowMetadataCache>();
    // No scoped dependencies (DbRunner and ShowMetadataCache are both singletons already).
    builder.Services.AddSingleton<ShowService>();
    builder.Services.AddSingleton<ReservationService>();
    builder.Services.AddSingleton<CancelService>();

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
                // A valid signature isn't enough: "sub" becomes user_id in SQL, so it must be a
                // well-formed user id (I6, D-10). Failing here makes the request unauthenticated,
                // which OnChallenge below turns into 401 unauthorized.
                OnTokenValidated = context =>
                {
                    if (!UserIds.IsValid(context.Principal?.FindFirst("sub")?.Value))
                    {
                        context.Fail($"The token's sub claim must match {UserIds.FormatDescription}.");
                    }

                    return Task.CompletedTask;
                },
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

    // D-15. Declares the Bearer scheme so Swagger UI's "Authorize" button can send a token from
    // POST /auth/token. Describes routes only; no secrets end up in the document.
    builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
        };
        document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }];
        return Task.CompletedTask;
    }));

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

    var appMetrics = app.Services.GetRequiredService<AppMetrics>();
    appMetrics.Registry.AddBeforeCollectCallback(app.Services.GetRequiredService<ShowGaugeCollector>().CollectAsync);

    app.UseMiddleware<RequestIdMiddleware>();
    app.UseSerilogRequestLogging();
    // UseExceptionHandler clears the resolved routing endpoint before ApiExceptionHandler runs, so
    // prometheus-net's default "endpoint" label (read via HttpContext.GetEndpoint()) would come back
    // "" for every handled error (409 contention, 503 db_unavailable, 400 malformed body) - exactly
    // the slowest, most contention-heavy requests, leaving per-endpoint latency/error breakdowns
    // silently missing them. IExceptionHandlerFeature.Endpoint still has the original endpoint, so a
    // custom label (which suppresses the library's own default for the same name) falls back to it.
    string EndpointLabel(HttpContext context) =>
        ((context.Features.Get<IExceptionHandlerFeature>()?.Endpoint ?? context.GetEndpoint()) as RouteEndpoint)
            ?.RoutePattern.RawText ?? "";

    // Before UseExceptionHandler (wraps it), so it records the final mapped status code (503/409/
    // ...) rather than a transient unhandled exception. Points at AppMetrics' own registry, not
    // Metrics.DefaultRegistry - see AppMetrics for why. Restricted to exactly the methods this API
    // serves (case-sensitive): UseHttpMetrics otherwise labels every request with its raw,
    // unauthenticated method string, so an attacker sending arbitrary (or oddly-cased) methods can
    // grow the series count without bound.
    app.UseWhen(
        context => context.Request.Method is "GET" or "POST" or "HEAD",
        branch => branch.UseHttpMetrics(options =>
        {
            options.InProgress.Registry = appMetrics.Registry;
            options.InProgress.CustomLabels.Add(new HttpCustomLabel("endpoint", EndpointLabel));
            options.RequestCount.Registry = appMetrics.Registry;
            options.RequestCount.CustomLabels.Add(new HttpCustomLabel("endpoint", EndpointLabel));
            options.RequestDuration.Registry = appMetrics.Registry;
            options.RequestDuration.CustomLabels.Add(new HttpCustomLabel("endpoint", EndpointLabel));
        }));
    // Registered after (so it runs closer to the endpoint than) UseSerilogRequestLogging: an
    // exception thrown downstream reaches this middleware FIRST on its way back out and is
    // resolved into a normal 400/503 response here, so Serilog's own try/catch - further out -
    // never sees an in-flight exception and just logs the already-correct status code. The
    // reverse order would have Serilog's middleware catch-log the exception as a false 500
    // before rethrowing it down to this handler (misleading burst/alerting evidence).
    app.UseExceptionHandler();

    // Served in every environment, not just Development: graders explore the live URL (D-15).
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "v1"));

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
    app.MapMetrics(settings => settings.Registry = appMetrics.Registry);
    app.MapOpenApi();

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
