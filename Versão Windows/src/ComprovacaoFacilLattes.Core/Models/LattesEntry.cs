using System.ComponentModel.DataAnnotations.Schema;

namespace ComprovacaoFacilLattes.Core.Models;

/// <summary>Uma linha comprovável do currículo — um artigo, uma banca, uma disciplina…</summary>
public class LattesEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Texto bruto extraído (para depuração/matching).</summary>
    public string RawText { get; set; } = "";

    /// <summary>Título de exibição extraído — pode ser o título do trabalho, o nome do candidato numa banca, etc., dependendo do tipo.</summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// Rótulo curto do tipo: "Artigo", "Banca", "Orientação", "Vínculo institucional",
    /// "Atividade administrativa", "Disciplina ministrada", "Organização de evento", "Formação",
    /// "Prêmio/Título", "Projeto", "Evento", "Apresentação", "Corpo editorial", "Mídia",
    /// "Produção técnica", "Livro/Capítulo", "Trabalho em evento", "Documento" (seção manual).
    /// </summary>
    public string Kind { get; set; } = "";

    /// <summary>Ano principal (0 = não identificado).</summary>
    public int Year { get; set; }

    public string Authors { get; set; } = "";

    /// <summary>Revista/instituição/local, dependendo do tipo.</summary>
    public string Venue { get; set; } = "";

    public string Doi { get; set; } = "";
    public string Isbn { get; set; } = "";
    public string Issn { get; set; } = "";

    /// <summary>Número de edital, ex. "41/2024".</summary>
    public string Edital { get; set; } = "";

    /// <summary>Portarias associadas, formato "nº/ano" separadas por espaço, ex. "2891/2022 3706/2023".</summary>
    public string Portaria { get; set; } = "";

    /// <summary>
    /// Ano final de um período (vínculos/atividades); 0 = sem período OU período aberto ("Atual")
    /// — cuidado: essa dualidade de significado do 0 é tratada caso a caso pelos consumidores.
    /// </summary>
    public int EndYear { get; set; }

    public int Order { get; set; }

    public EntryStatus CertificateStatus { get; set; } = EntryStatus.None;

    /// <summary>
    /// <c>"{year}_{title.lowercased().trimmed.prefix(60)}"</c> — chave usada para tentar
    /// re-vincular certificados automaticamente após um re-parse (ex.: ao importar um Lattes
    /// atualizado). É FRÁGIL: qualquer mudança no algoritmo de extração de título muda o hash e
    /// quebra o link — por isso existe um fallback por similaridade de texto quando o hash não bate.
    /// </summary>
    public string HashKey { get; set; } = "";

    public Guid? SectionId { get; set; }
    public LattesSection? Section { get; set; }

    public List<Certificate> Certificates { get; set; } = new();

    public LattesEntry() { }

    public LattesEntry(string rawText, string title, string kind = "", int year = 0,
        string authors = "", string venue = "", int order = 0)
    {
        RawText = rawText;
        Title = title;
        Kind = kind;
        Year = year;
        Authors = authors;
        Venue = venue;
        Order = order;
        HashKey = MakeHash(year, title);
    }

    public static string MakeHash(int year, string title)
    {
        var clean = title.ToLowerInvariant().Trim();
        if (clean.Length > 60) clean = clean[..60];
        return $"{year}_{clean}";
    }

    [NotMapped]
    public string YearString => Year > 0 ? Year.ToString() : "";

    /// <summary>Todos os certificados da entrada, na ordem definida pelo usuário.</summary>
    [NotMapped]
    public IEnumerable<Certificate> SortedCertificates =>
        Certificates.OrderBy(c => c.Order).ThenBy(c => c.ImportDate);

    [NotMapped]
    public IEnumerable<Certificate> ConfirmedCertificates => SortedCertificates.Where(c => c.IsConfirmed);

    /// <summary>Próxima posição livre (para anexar um novo comprovante ao final).</summary>
    public int NextCertificateOrder() => (Certificates.Count == 0 ? -1 : Certificates.Max(c => c.Order)) + 1;

    /// <summary>
    /// Título descritivo que identifica a entrada na interface e nos relatórios.
    /// Ex.: "Artigo — Título — Revista (2024)" ou "Banca — Fulano (2023)".
    /// </summary>
    [NotMapped]
    public string DisplayTitle
    {
        get
        {
            var core = (string.IsNullOrEmpty(Title) ? RawText : Title).Trim().Replace("\n", " ");
            var shortCore = core.Length > 150 ? core[..150] + "…" : core;

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Kind)) parts.Add(Kind);
            if (!string.IsNullOrEmpty(shortCore)) parts.Add(shortCore);
            if (!string.IsNullOrEmpty(Venue)
                && shortCore.IndexOf(Venue, StringComparison.OrdinalIgnoreCase) < 0
                && Venue.Length < 80)
            {
                parts.Add(Venue);
            }

            var result = string.Join(" — ", parts);
            if (Year > 0) result += $" ({Year})";
            return result;
        }
    }
}
