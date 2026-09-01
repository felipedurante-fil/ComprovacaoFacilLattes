namespace ComprovacaoFacilLattes.Core.Models;

/// <summary>
/// Armazenado, não recalculado automaticamente — precisa ser atualizado manualmente sempre que
/// os certificados de uma entrada mudam (isso já causou um bug real no app original).
/// </summary>
public enum EntryStatus
{
    None,
    Suggested,
    Confirmed
}
