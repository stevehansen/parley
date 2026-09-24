namespace Parley;

/// <summary>The single-page web UI, embedded in the assembly so the hub needs no files beside it.</summary>
internal static class WebUi
{
    public static readonly string Html = Load();

    private static string Load()
    {
        using var stream = typeof(WebUi).Assembly.GetManifestResourceStream("Parley.Web.index.html")
            ?? throw new InvalidOperationException("Embedded web UI missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
