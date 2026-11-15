namespace MusicBot.Services;

public sealed record ActivityEntry(long Id, DateTimeOffset Timestamp, string Source, string Kind, string User, string Message, bool Success);

public static class ActivityJournal
{
    private static readonly object Sync = new();
    private static readonly Queue<ActivityEntry> Entries = new();
    private static long _next;
    public static void Record(string source, string kind, string user, string message, bool success = true)
    {
        lock (Sync)
        {
            Entries.Enqueue(new(++_next, DateTimeOffset.UtcNow, source, kind, user, message, success));
            while (Entries.Count > 300) Entries.Dequeue();
        }
    }
    public static ActivityEntry[] Snapshot() { lock (Sync) return Entries.Reverse().ToArray(); }
}
