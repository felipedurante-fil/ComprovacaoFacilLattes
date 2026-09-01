using PdfSharp.Fonts;

namespace ComprovacaoFacilLattes.Infrastructure.PdfReport;

/// <summary>
/// PdfSharp 6 (cross-platform, sem GDI+) não descobre fontes do sistema sozinho em
/// nenhuma plataforma — precisa de um <see cref="IFontResolver"/> explícito. Em vez de
/// depender da Arial estar instalada no Windows do usuário (quase sempre está, mas não
/// é garantido, e não pode ser embutida por licença), embute a DejaVu Sans (licença
/// livre, metricamente parecida) no assembly — o relatório fica autocontido, sem
/// depender de nenhuma fonte externa.
/// </summary>
internal sealed class EmbeddedFontResolver : IFontResolver
{
    private const string RegularFace = "DejaVuSans";
    private const string BoldFace = "DejaVuSans-Bold";

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? BoldFace : RegularFace);

    public byte[] GetFont(string faceName) =>
        faceName == BoldFace ? ReadEmbedded("DejaVuSans-Bold.ttf") : ReadEmbedded("DejaVuSans.ttf");

    private static byte[] ReadEmbedded(string fileName)
    {
        var asm = typeof(EmbeddedFontResolver).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static void EnsureRegistered()
    {
        GlobalFontSettings.FontResolver ??= new EmbeddedFontResolver();
    }
}
