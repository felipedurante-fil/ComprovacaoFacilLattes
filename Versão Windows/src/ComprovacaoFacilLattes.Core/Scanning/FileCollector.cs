namespace ComprovacaoFacilLattes.Core.Scanning;

public static class FileCollector
{
    private static readonly HashSet<string> ValidExtensions = new()
    {
        "pdf", "jpg", "jpeg", "png", "tiff", "tif", "heic",
    };

    /// <summary>
    /// Coleta recursivamente TODAS as camadas de subpastas (sem limite de profundidade),
    /// tolerando pastas/arquivos inacessíveis (ex.: item do OneDrive ainda não baixado) —
    /// continua o escaneamento em vez de parar no meio.
    /// </summary>
    public static List<string> CollectFiles(string rootPath)
    {
        var results = new List<string>();
        CollectRecursive(rootPath, results);
        results.Sort(StringComparer.Ordinal);
        return results;
    }

    private static void CollectRecursive(string dir, List<string> results)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch { return; }

        foreach (var entry in entries)
        {
            try
            {
                var attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.Hidden) != 0) continue;
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    CollectRecursive(entry, results);
                }
                else
                {
                    var ext = Path.GetExtension(entry).TrimStart('.').ToLowerInvariant();
                    if (ValidExtensions.Contains(ext)) results.Add(entry);
                }
            }
            catch
            {
                // ignora entradas inacessíveis e continua o escaneamento
            }
        }
    }
}
