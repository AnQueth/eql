using System.Text.RegularExpressions;

namespace EqLog.Core.IO;

public sealed record LogFileInfo(string Path, string CharacterName, string Server, DateTime LastWriteTime, long Length);

public static partial class LogFileLocator
{
    public const string DefaultDirectory = @"C:\Users\Public\Daybreak Game Company\Installed Games\EverQuest Legends\Logs";

    public const string SearchPattern = "eqlog*.txt";

    public static IReadOnlyList<LogFileInfo> FindLogs(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return new DirectoryInfo(directory)
            .EnumerateFiles(SearchPattern)
            .Select(Describe)
            .OrderByDescending(f => f.LastWriteTime)
            .ToList();
    }

    /// <summary>The most recently written log file (i.e. the character currently being played).</summary>
    public static LogFileInfo? FindNewest(string directory)
    {
        return FindLogs(directory).FirstOrDefault();
    }

    public static LogFileInfo Describe(string path)
    {
        return Describe(new FileInfo(path));
    }

    private static LogFileInfo Describe(FileInfo fi)
    {
        var m = FileNameRegex().Match(fi.Name);
        string name = m.Success ? m.Groups["name"].Value : string.Empty;
        string server = m.Success ? m.Groups["server"].Value : string.Empty;
        return new LogFileInfo(fi.FullName, name, server, fi.Exists ? fi.LastWriteTime : DateTime.MinValue, fi.Exists ? fi.Length : 0);
    }

    [GeneratedRegex(@"^eqlog_(?<name>[^_]+)_(?<server>.+)\.txt$", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameRegex();
}
