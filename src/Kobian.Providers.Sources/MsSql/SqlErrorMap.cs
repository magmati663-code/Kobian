using Kobian.Domain;

namespace Kobian.Providers.Sources.MsSql;

/// <summary>Mapowanie numerów błędów SQL Server na kody KB-3xxx (R2.5.7). Czysta funkcja — bez zależności od SqlClient.</summary>
public static class SqlErrorMap
{
    public static (string Code, ErrorClass Class) Map(int sqlErrorNumber) => sqlErrorNumber switch
    {
        -2 or 26 or 40 or 53 or 10054 or 10060 or 10061 or 233 => (KbCode.SqlUnreachable, ErrorClass.Transient),
        18456 or 18452 or 4060 => (KbCode.SqlLoginFailed, ErrorClass.Permanent),
        262 => (KbCode.SqlBackupPermissionMissing, ErrorClass.Permanent),
        3043 => (KbCode.SqlPageChecksumError, ErrorClass.Integrity),
        3201 or 3241 => (KbCode.SqlVerifyFailed, ErrorClass.Integrity),
        // TLS (it. 71, SEC-5.6): SEC_E_UNTRUSTED_ROOT, CERT_E_CN_NO_MATCH/SEC_E_WRONG_PRINCIPAL, CERT_E_UNTRUSTEDROOT oraz numer
        // zastępczy SqlClientMsSqlSession.TlsHandshakeFailed — certyfikat niezgodny z pinem/niezaufany. // PROWIZORYCZNIE Qb29
        -2146893019 or -2146893022 or -2146762487 => (KbCode.SqlTlsPinMismatch, ErrorClass.Security),
        _ => (KbCode.Internal, ErrorClass.Permanent),
    };

    /// <summary>Zbiorczo dla listy błędów z jednego wyjątku (SqlException.Errors): pierwszy rozpoznany kod wygrywa,
    /// z priorytetem integralności (3043 obok 3013 „BACKUP DATABASE is terminating abnormally”).</summary>
    public static (string Code, ErrorClass Class) MapMany(IEnumerable<int> numbers)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        var mapped = numbers.Select(Map).Where(m => m.Code != KbCode.Internal).ToList();
        if (mapped.Count == 0) return (KbCode.Internal, ErrorClass.Permanent);
        // Bezpieczeństwo (TLS) przed integralnością, integralność przed resztą.
        return mapped.OrderByDescending(m => m.Class == ErrorClass.Security).ThenByDescending(m => m.Class == ErrorClass.Integrity).First();
    }
}
