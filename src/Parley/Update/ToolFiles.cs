using Microsoft.Win32.SafeHandles;

namespace Parley.Update;

/// <summary>
/// Lets <c>dotnet tool update</c> succeed on Windows while AI sessions keep running their shims.
///
/// A running shim keeps its program files open, and Windows won't delete open files, so the
/// update's uninstall of the old version fails. It will <em>move</em> them though, and the
/// shim keeps running from the new location. So before updating, the files that are in use
/// are moved to <c>~/.dotnet/tools/.parley-old/</c> (same volume, so a move is a rename); the
/// rest stays for dotnet to read and remove. When the update fails, the files go back.
/// The moved copies are deleted once their shims have exited (<see cref="DeleteLeftovers"/>).
/// </summary>
internal static class ToolFiles
{
    public sealed record Move(string From, string To);

    /// <param name="toolShim">The tool's command shim, <c>~/.dotnet/tools/parley.exe</c>.</param>
    public static List<Move> MoveInUseAside(string toolShim)
    {
        var tools = Path.GetDirectoryName(toolShim)!;
        var trash = Path.Combine(LeftoversRoot(tools), Guid.NewGuid().ToString("N"));
        var store = Path.Combine(tools, ".store", UpdateChecker.PackageId.ToLowerInvariant());
        var files = new List<string> { toolShim };
        if (Directory.Exists(store)) files.AddRange(Directory.EnumerateFiles(store, "*", SearchOption.AllDirectories));

        var moves = new List<Move>();
        foreach (var file in files.Where(InUse))
        {
            var to = Path.Combine(trash, Path.GetRelativePath(tools, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(file, to);
            moves.Add(new Move(file, to));
        }
        return moves;
    }

    /// <summary>Undoes <see cref="MoveInUseAside"/> after a failed update.</summary>
    public static void Restore(IEnumerable<Move> moves)
    {
        foreach (var m in moves)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(m.From)!);
            File.Move(m.To, m.From, overwrite: true);
        }
    }

    /// <summary>Deletes moved-aside files whose shims have exited; the rest waits for next time.</summary>
    public static void DeleteLeftovers(string toolShim)
    {
        var root = LeftoversRoot(Path.GetDirectoryName(toolShim)!);
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        try { Directory.Delete(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string LeftoversRoot(string toolsDir) => Path.Combine(toolsDir, ".parley-old");

    /// <summary>Open elsewhere — an exclusive open fails with a sharing violation.</summary>
    private static bool InUse(string file)
    {
        try
        {
            using SafeFileHandle _ = File.OpenHandle(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // a running .exe can't be opened for writing at all
        }
    }
}
