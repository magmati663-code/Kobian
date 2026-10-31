namespace Kobian.UI.Shell;

/// <summary>Ekran odświeżany przy wejściu z nawigacji (dane z usługi są zawsze świeże po przełączeniu — R2.6.4).</summary>
public interface IRefreshablePage
{
    Task RefreshAsync(CancellationToken ct);
}
