using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ComprovacaoFacilLattes.Tests.TestSetup;

/// <summary>
/// O NuGet `Tesseract` só empacota binários nativos win-x64/x86 — correto para o
/// artefato final do port (o alvo é Windows). Para poder rodar os testes de OCR aqui
/// no Mac durante o desenvolvimento, isso cria links simbólicos com os nomes que o
/// loader do wrapper espera, apontando para o Tesseract/Leptonica instalados via
/// Homebrew. Só age em macOS quando o Homebrew os tem instalados; não afeta em nada o
/// código de produção nem o build/publish Windows (fica só no projeto de testes).
/// </summary>
internal static class MacOsTesseractNativeLibSetup
{
    [ModuleInitializer]
    internal static void Setup()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;

        // InteropDotNet.LibraryLoader busca em subpastas por arquitetura (mesma
        // estrutura x86/x64 usada pelas DLLs do Windows) além da pasta base.
        foreach (var subdir in new[] { "", "x64", "x86" })
        {
            LinkIfMissing(subdir, "libleptonica-1.82.0.dylib", "/opt/homebrew/lib/libleptonica.dylib");
            LinkIfMissing(subdir, "libtesseract50.dylib", "/opt/homebrew/lib/libtesseract.dylib");
        }
    }

    private static void LinkIfMissing(string subdir, string linkName, string target)
    {
        if (!File.Exists(target)) return;
        var dir = subdir.Length == 0 ? AppContext.BaseDirectory : Path.Combine(AppContext.BaseDirectory, subdir);
        Directory.CreateDirectory(dir);
        var linkPath = Path.Combine(dir, linkName);
        if (File.Exists(linkPath) || Directory.Exists(linkPath)) return;
        try { File.CreateSymbolicLink(linkPath, target); } catch { /* melhor esforço */ }
    }
}
