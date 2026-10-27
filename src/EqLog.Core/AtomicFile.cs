namespace EqLog.Core;

public static class AtomicFile
{
    /// <summary>Writes to a temp file and then replaces the target, so a crash mid-write can't leave a truncated file.</summary>
    public static void WriteAllText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}