namespace Burst;

// Thrown when the burst run cannot meaningfully continue (setup failed, a seat/user pool ran out).
// Caught once at the top of Program.cs and reported as a fatal, non-zero exit.
public sealed class BurstFatalException(string message) : Exception(message);
