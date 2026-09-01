using ComprovacaoFacilLattes.Core.Persistence;

namespace ComprovacaoFacilLattes.App.Services;

/// <summary>Cria contextos de banco de dados curtos (padrão recomendado do EF Core — criar, usar, descartar) já garantindo que o schema exista.</summary>
public static class AppDb
{
    private static readonly HashSet<string> InitializedPaths = new();

    /// <summary>Usado só pelos testes automatizados para apontar para um banco temporário em vez do banco real do usuário.</summary>
    public static string? DatabasePathOverride { get; set; }

    public static AppDbContext Create()
    {
        var path = DatabasePathOverride ?? AppPaths.DatabasePath;
        var ctx = new AppDbContext(path);
        if (InitializedPaths.Add(path)) ctx.Database.EnsureCreated();
        return ctx;
    }
}
