using System.Text;
using System.Text.Json;

namespace MosaicGenerator.Sidecar;

/// <summary>
/// Writes messages to stdout, one JSON object per line. This is the only way the
/// sidecar talks to the app, so nothing else may write to stdout.
/// <para>
/// Thread-safe: responses come from the main loop while progress events come from the
/// background job, and a lock keeps their lines from interleaving.
/// </para>
/// </summary>
public static class Output
{
    private static readonly object Lock = new();

    // Our own UTF-8 (no BOM) writer, so file paths with characters like "é" survive
    // regardless of the console code page. Lines end in "\n" on every platform.
    private static readonly StreamWriter Stdout =
        new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

    public static void Send(object message)
    {
        // Serialize outside the lock; only the write itself needs to be exclusive.
        string line = JsonSerializer.Serialize(message, message.GetType(), Protocol.Json);
        lock (Lock)
            Stdout.WriteLine(line);
    }
}
