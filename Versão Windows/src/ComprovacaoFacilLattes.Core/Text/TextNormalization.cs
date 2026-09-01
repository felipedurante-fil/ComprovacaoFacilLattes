using System.Globalization;
using System.Text;

namespace ComprovacaoFacilLattes.Core.Text;

public static class TextNormalization
{
    /// <summary>
    /// Remove acentos e converte para minúsculas — equivalente a
    /// <c>String.folding(options: [.diacriticInsensitive, .caseInsensitive])</c> do Swift
    /// seguido de <c>.lowercased()</c>. Preserva pontuação e espaçamento originais.
    /// </summary>
    public static string FoldDiacriticsLower(string s)
    {
        var lowered = s.ToLowerInvariant();
        var decomposed = lowered.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
