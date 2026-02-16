namespace ActorConsensus.Contracts;

/// <summary>
/// Thread-safe logger that prefixes output with the framework name and timestamp.
/// Colour-codes output by event type for readability.
/// </summary>
public sealed class ConsensusLog(string frameworkName)
{
    private readonly Lock _lock = new();

    public void Election(int nodeId, string message)
        => Print(ConsoleColor.Yellow, "ELECTION", nodeId, message);

    public void Leader(int nodeId, string message)
        => Print(ConsoleColor.Green, "LEADER", nodeId, message);

    public void Work(int nodeId, string message)
        => Print(ConsoleColor.Cyan, "WORK", nodeId, message);

    public void Lifecycle(int nodeId, string message)
        => Print(ConsoleColor.Magenta, "LIFECYCLE", nodeId, message);

    public void Heartbeat(int nodeId, string message)
        => Print(ConsoleColor.DarkGray, "HEARTBEAT", nodeId, message);

    public void Error(int nodeId, string message)
        => Print(ConsoleColor.Red, "ERROR", nodeId, message);

    public void Info(string message)
    {
        lock (_lock)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"  [{frameworkName}] {message}");
            Console.ForegroundColor = prev;
        }
    }

    private void Print(ConsoleColor color, string category, int nodeId, string message)
    {
        lock (_lock)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(
                $"  [{frameworkName}] [{category,-10}] Node-{nodeId}: {message}"
            );
            Console.ForegroundColor = prev;
        }
    }
}
