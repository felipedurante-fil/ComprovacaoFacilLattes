namespace ComprovacaoFacilLattes.App.Services;

public interface IDialogService
{
    Task<string?> PickPdfFileAsync(string title);
    Task<string?> PickZipFileAsync(string title);
    Task<string?> PickAnyFileAsync(string title);
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickSaveLocationAsync(string title, string suggestedName, string extension);
}
