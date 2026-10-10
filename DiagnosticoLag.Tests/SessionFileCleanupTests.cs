namespace DiagnosticoLag.Tests;

public sealed class SessionFileCleanupTests
{
    [Fact]
    public void SelectsOnlySupportedFilesOlderThanSelectedRange()
    {
        var directory = CreateDirectory();
        try
        {
            var oldLog = CreateFile(directory, "old.txt", DateTime.Now.AddDays(-8));
            CreateFile(directory, "recent.csv", DateTime.Now.AddDays(-1));
            CreateFile(directory, "ignored.json", DateTime.Now.AddDays(-20));

            var files = SessionFileCleanup.GetFiles([directory], SessionCleanupRange.OlderThan7Days, DateTime.Now);

            Assert.Equal([oldLog], files.Select(file => file.FullName));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AllRangeIncludesTxtAndCsvAcrossDuplicateDirectories()
    {
        var directory = CreateDirectory();
        try
        {
            var log = CreateFile(directory, "session.txt", DateTime.Now);
            var csv = CreateFile(directory, "session.csv", DateTime.Now);

            var files = SessionFileCleanup.GetFiles([directory, directory], SessionCleanupRange.All, DateTime.Now);

            Assert.Equal(
                new[] { log, csv }.OrderBy(path => path).ToArray(),
                files.Select(file => file.FullName).OrderBy(path => path).ToArray());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "diagnostico-lag-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreateFile(string directory, string name, DateTime lastWriteTime)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, name);
        File.SetLastWriteTime(path, lastWriteTime);
        return path;
    }
}
