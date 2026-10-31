// kobian-restore — odtwarzanie offline bez usługi (D8.3, US-43, Q17). Logika: Kobian.Engine.Restore.OfflineRestore.
// Klucz: opakowany plik klucza + hasło + nagłówek kobian.repo (D3.11, BL-24); klucz deweloperski (ADR-0002) usunięto w it. 48.
using Kobian.Engine.Restore;
using Kobian.Providers.Storage.Local;
using Kobian.Providers.Storage.Secrets;

return await OfflineRestore.RunAsync(args, new LocalStorageProviderFactory(), StandaloneRemoteProviders.Create, Console.Out, Console.Error, CancellationToken.None).ConfigureAwait(false);
