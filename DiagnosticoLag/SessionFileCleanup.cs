namespace DiagnosticoLag;

internal enum SessionCleanupRange
{
    OlderThan7Days,
    OlderThan30Days,
    OlderThan90Days,
    All
}

internal static class SessionFileCleanup
{
    public static IReadOnlyList<FileInfo> GetFiles(
        IEnumerable<string> directories,
        SessionCleanupRange range,
        DateTime now)
    {
        var files = directories
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory)
                .Where(IsSessionFile)
                .Select(path => new FileInfo(path)))
            .ToList();

        if (range == SessionCleanupRange.All)
        {
            return files;
        }

        var age = range switch
        {
            SessionCleanupRange.OlderThan7Days => TimeSpan.FromDays(7),
            SessionCleanupRange.OlderThan30Days => TimeSpan.FromDays(30),
            SessionCleanupRange.OlderThan90Days => TimeSpan.FromDays(90),
            _ => throw new ArgumentOutOfRangeException(nameof(range))
        };
        var threshold = now - age;
        return files.Where(file => file.LastWriteTime < threshold).ToList();
    }

    private static bool IsSessionFile(string path) =>
        string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase);
}
