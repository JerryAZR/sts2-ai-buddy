using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;

namespace AiBuddy.Observability;

/// <summary>
/// Append-only JSONL event stream for one game session. All observability
/// sources (wire tap, wait tracer, action trace, checksum log, mod decisions)
/// write here so events interleave into a single timeline.
/// Determinism is our debugger: seed + this stream = repro.
/// </summary>
public static class EventLog
{
    private static readonly object Lock = new();
    private static StreamWriter? _writer;
    private static long _seq;

    public static string? CurrentPath { get; private set; }

    public static void Open()
    {
        lock (Lock)
        {
            if (_writer != null)
            {
                return;
            }
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "sts2-ai-buddy", "logs");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            CurrentPath = Path.Combine(dir, $"session-{stamp}.jsonl");
            _writer = new StreamWriter(CurrentPath, append: false) { AutoFlush = true };
            WriteUnlocked("session", new { path = CurrentPath, utc = DateTime.UtcNow });
        }
    }

    /// <summary>Append one event. Source examples: wire, wait, action, checksum, mod.</summary>
    public static void Write(string source, string kind, object? data = null)
    {
        lock (Lock)
        {
            if (_writer == null)
            {
                return;
            }
            WriteUnlocked(source, new { kind, data });
        }
    }

    private static void WriteUnlocked(string source, object payload)
    {
        try
        {
            var envelope = new
            {
                seq = ++_seq,
                tick = Environment.TickCount64,
                src = source,
                payload,
            };
            _writer!.WriteLine(JsonSerializer.Serialize(envelope));
        }
        catch (Exception ex)
        {
            // Observability must never break the game. Degrade to the game log.
            Log.Warn($"[AiBuddy] EventLog write failed: {ex.Message}");
        }
    }
}
