namespace Burst;

public static class ConcurrentRunner
{
    // Starts one task per input (each awaiting the shared gate before doing any I/O), then releases
    // the gate once every task has been created. Actual in-flight concurrency is still bounded by
    // ApiClient's own semaphore; the gate only controls when requests are allowed to start.
    public static async Task<IReadOnlyList<TOut>> RunAsync<TIn, TOut>(
        IReadOnlyList<TIn> inputs,
        Func<TIn, CancellationToken, Task<TOut>> action,
        CancellationToken ct)
    {
        var gate = new StartGate();
        var tasks = new Task<TOut>[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            tasks[i] = Task.Run(async () =>
            {
                await gate.Wait;
                return await action(input, ct);
            }, ct);
        }

        gate.Release();
        return await Task.WhenAll(tasks);
    }
}
