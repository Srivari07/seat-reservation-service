namespace Burst;

// Lets a batch of tasks be created first and released together, so their requests actually
// collide instead of trickling out in creation order.
public sealed class StartGate
{
    private readonly TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Wait => tcs.Task;

    public void Release() => tcs.TrySetResult();
}
