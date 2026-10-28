using System.Text.RegularExpressions;

namespace EqLog.Core.IO;

public sealed record LogFileInfo(string Path, string CharacterName, string Server, DateTime LastWriteTime, long Length);

public static partial class LogFileLocator
{
    public const string DefaultDirectory = @"C:\Users\Public\Daybreak Game Company\Installed Games\EverQuest Legends\Logs";

    public const string SearchPattern = "eqlog*.txt";

    /// <summary>Usual log folders for EverQuest Legends and EverQuest Live (Daybreak launcher, Steam, older Sony installs).</summary>
    public static IReadOnlyList<string> CandidateDirectories { get; } =
    [
        DefaultDirectory,
        @"C:\Users\Public\Daybreak Game Company\Installed Games\EverQuest\Logs",
        @"C:\Program Files (x86)\Steam\steamapps\common\Everquest F2P\Logs",
        @"C:\Program Files\Steam\steamapps\common\Everquest F2P\Logs",
        @"C:\Program Files (x86)\Sony\EverQuest\Logs",
        @"C:\EverQuest\Logs",
    ];

    /// <summary>The candidate folder holding the most recently written eqlog file, or null when none has logs.</summary>
    public static string? DetectDirectory(IEnumerable<string>? candidates = null)
    {
        return (candidates ?? CandidateDirectories)
            .Select(TryFindNewest)
            .OfType<LogFileInfo>()
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => Path.GetDirectoryName(f.Path))
            .FirstOrDefault();
    }

    /// <summary>
    /// The logs folder for a folder the user picked: the folder itself, or its "Logs" subfolder when they chose the game
    /// install folder and only that subfolder has eqlog files.
    /// </summary>
    public static string ResolveChosenDirectory(string folder)
    {
        string logs = Path.Combine(folder, "Logs");
        if (FindLogs(folder).Count == 0 && Directory.Exists(logs))
        {
            return logs;
        }
        return folder;
    }

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

    private static LogFileInfo? TryFindNewest(string directory)
    {
        try
        {
            return FindNewest(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
