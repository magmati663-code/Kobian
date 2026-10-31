namespace Kobian.Agent.Hosting;

/// <summary>
/// Pojedyncza instancja Agenta (D2.15 pkt 3, AC-37.3, CE-12): wyłączne otwarcie <c>catalog.db.lock</c> obok katalogu
/// oraz — na Windows — nazwany mutex <see cref="AgentServiceProfile.InstanceMutexName"/>. Zajmowane PRZED otwarciem katalogu:
/// druga instancja kończy się kodem KB-7007 bez dotykania <c>catalog.db</c>.
/// PROWIZORYCZNIE Qb41: mutex z domyślnym ACL procesu; ACL „konto usługi + SYSTEM” i wykrycie obcego właściciela (SEC-22.4, squatting) — W-33.
/// Plik blokady nie jest usuwany przy zwolnieniu (usunięcie otwierałoby wyścig dwóch startów); zawiera wyłącznie PID.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly FileStream _lock;
    private readonly Mutex? _mutex;

    private SingleInstanceGuard(FileStream lockFile, Mutex? mutex)
    {
        _lock = lockFile;
        _mutex = mutex;
    }

    public static string LockPathFor(string catalogPath) => catalogPath + ".lock";

    /// <summary>Instancja albo null, gdy inny proces (lub inny strażnik w tym procesie) trzyma blokadę.</summary>
    /// <param name="useGlobalMutex">true dla usługi na Windows; testy w Linuksie — false (nazwany mutex poza Windows nie jest globalny).</param>
    public static SingleInstanceGuard? TryAcquire(string catalogPath, bool useGlobalMutex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        Mutex? mutex = null;
        if (useGlobalMutex)
        {
            try
            {
                mutex = new Mutex(initiallyOwned: false, AgentServiceProfile.InstanceMutexName);
            }
            catch (UnauthorizedAccessException)
            {
                return null; // SEC-22.4: nazwa zajęta przez obcy proces z ACL bez dostępu → traktujemy jak drugą instancję (KB-7007)
            }
            bool owned;
            try
            {
                owned = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                owned = true; // poprzednia instancja padła — przejmujemy (NFR-09: restart przez SCM)
            }
            if (!owned)
            {
                mutex.Dispose();
                return null;
            }
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(catalogPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        FileStream? fs = null;
        try
        {
            fs = new FileStream(LockPathFor(catalogPath), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            fs.SetLength(0);
            var pid = System.Text.Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            fs.Write(pid);
            fs.Flush(flushToDisk: false);
            return new SingleInstanceGuard(fs, mutex);
        }
        catch (IOException)
        {
            fs?.Dispose();
            Release(mutex);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            fs?.Dispose();
            Release(mutex);
            return null;
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
        Release(_mutex);
    }

    private static void Release(Mutex? m)
    {
        if (m is null) return;
        try
        {
            m.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // nie nasz (inny wątek) — wystarczy zamknąć uchwyt
        }
        m.Dispose();
    }
}
