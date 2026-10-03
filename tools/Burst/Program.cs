using Burst;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    BurstOptions options;
    try
    {
        options = BurstOptions.Parse(args);
    }
    catch (BurstUsageException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }

    var globalErrors = new GlobalErrorTally();
    using var client = new ApiClient(options.BaseUrl, options.Concurrency, globalErrors);
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    var ct = cts.Token;

    BurstContext ctx;
    try
    {
        ctx = await ScenarioA_Setup.RunAsync(options, client, globalErrors, ct);
    }
    catch (BurstFatalException ex)
    {
        Console.Error.WriteLine($"FATAL during setup: {ex.Message}");
        return 1;
    }

    var sampler = new InvariantSampler(client, ctx.ShowId);
    ctx.Sampler = sampler;
    sampler.Start();

    await RunScenario(ctx, "B", () => ScenarioB_HotSeatStorm.RunAsync(ctx, ct));
    await RunScenario(ctx, "C", () => ScenarioC_Stampede.RunAsync(ctx, ct));

    await sampler.StopAsync();

    await RunScenario(ctx, "D", () => ScenarioD_Idempotency.RunAsync(ctx, ct));
    await RunScenario(ctx, "E", () => ScenarioE_PerUserLimit.RunAsync(ctx, ct));
    await RunScenario(ctx, "F", () => ScenarioF_Spoofing.RunAsync(ctx, ct));
    await RunScenario(ctx, "G", () => ScenarioG_CancelRebook.RunAsync(ctx, ct));
    await RunScenario(ctx, "H", () => ScenarioH_Reconciliation.RunAsync(ctx, sampler, ct));

    return Reporter.PrintAndGetExitCode(ctx);
}

// A failure in one scenario is recorded and the rest still run, so one bug doesn't hide every
// other scenario's result. Only ScenarioA's own failures (caught above) are fatal.
static async Task RunScenario(BurstContext ctx, string label, Func<Task> action)
{
    try
    {
        await action();
    }
    catch (Exception ex)
    {
        ctx.Checks.Add(new CheckResult($"Scenario {label}", false, $"unhandled exception: {ex.Message}"));
    }
}
