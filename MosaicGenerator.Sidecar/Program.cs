// The mosaic engine's sidecar: a background process the GUI starts and talks to.
//
// The app writes one JSON request per line to our stdin, and we write one JSON message per
// line to stdout: a "response" to each request, plus events (progress, log lines, done...)
// while a mosaic is being generated. See README.md in this folder for all message types.

using System.Text;
using System.Text.Json;
using MosaicGenerator.Sidecar;

// stdout belongs to the protocol (see Output.cs). Send anything else that tries to print,
// e.g. a library writing to the console, to stderr instead, so it can't corrupt a message.
Console.SetOut(Console.Error);

var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

Output.Send(new ReadyEvent(Protocol.Version));

// The main loop: read a request, handle it, send the response. Generating a mosaic runs
// on a background thread (MosaicJob), so this loop keeps answering, e.g. to "cancel".
while (!Handlers.ShutdownRequested && stdin.ReadLine() is string line)
{
    if (!string.IsNullOrWhiteSpace(line))
        HandleRequest(line);
}

// We get here after a "shutdown" request, or when stdin is closed (the app exited without
// sending one). Either way, stop a running job cleanly so its partial output is removed.
MosaicJob.CancelAndWait();

static void HandleRequest(string line)
{
    int? id = null;
    try
    {
        using var doc  = JsonDocument.Parse(line);
        var       root = doc.RootElement;

        if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number)
            id = idElement.GetInt32();

        string type = root.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString() ?? ""
            : "";

        object? result = Handlers.Handle(type, root);
        Output.Send(Response.Success(id, result));
    }
    catch (Exception ex)
    {
        // Never crash on a bad request; tell the app what went wrong instead.
        Output.Send(Response.Failure(id, ex.Message));
    }
}
