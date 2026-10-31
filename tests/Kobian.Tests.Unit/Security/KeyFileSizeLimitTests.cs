using Kobian.Crypto;
using Kobian.Domain;
using Xunit;

namespace Kobian.Tests.Unit.Security;

/// <summary>SEC-3.3: plik klucza większy niż limit jest odrzucany przed wczytaniem do pamięci.</summary>
public sealed class KeyFileSizeLimitTests
{
    [Fact, Trait("SEC", "SEC-3.3")]
    public void Oversized_key_file_is_rejected_with_unwrap_failure()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kobian-keysize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var key = Path.Combine(dir, "big.key");
            File.WriteAllText(key, new string(' ', KeyFiles.MaxFileBytes + 1));
            var ex = Assert.Throws<KobianException>(() => KeyFileLoader.Load(key, null, null));
            Assert.Equal(KbCode.KeyUnwrapFailed, ex.Code);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
