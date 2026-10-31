using Kobian.Providers.Storage.Secrets;
using Kobian.Agent.Jobs;
using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Sources.MsSql;
using Kobian.Providers.Sources.MsSql.SqlClient;
using Kobian.Providers.Storage.Sftp;

namespace Kobian.Agent.Secrets;

/// <summary>
/// Fabryka sesji MSSQL dla zadań „mssql” (BL-40 cz. 2): uwierzytelnienie Windows konta usługi albo login SQL z magazynu sekretów Agenta
/// (<c>file:&lt;nazwa&gt;</c>, ten sam format i te same kontrole co SFTP — wpis „backup”: <c>user</c> + <c>password</c>; PROWIZORYCZNIE Qb16/Qb29).
/// Hasło czytane przy każdym otwarciu połączenia i zerowane po przekazaniu do SqlCredential; nie jest trzymane w sesji.
/// </summary>
public static class AgentMsSql
{
    public static Func<MsSqlSourceDefinition, CancellationToken, ValueTask<IMsSqlSession>> CreateFactory(string secretsDir)
    {
        var secrets = new FileSftpCredentialProvider(secretsDir);
        return (def, ct) => CreateAsync(def, secrets, ct);
    }

    /// <summary>Fabryka sesji z samych parametrów połączenia (discovery kreatora S1-db — BL-40 cz. 3; bez definicji zadania).</summary>
    public static Func<MsSqlConnectionOptions, string?, CancellationToken, ValueTask<IMsSqlSession>> CreateConnectionFactory(string secretsDir)
    {
        var secrets = new FileSftpCredentialProvider(secretsDir);
        return (connection, secretRef, ct) => CreateAsync(connection, secretRef, secrets, ct);
    }

    internal static ValueTask<IMsSqlSession> CreateAsync(MsSqlSourceDefinition def, ISftpCredentialProvider secrets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(def);
        return CreateAsync(def.Connection, def.SecretRef, secrets, ct);
    }

    internal static async ValueTask<IMsSqlSession> CreateAsync(MsSqlConnectionOptions connection, string? secretRef, ISftpCredentialProvider secrets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Authentication == MsSqlAuthentication.Integrated) return new SqlClientMsSqlSession(connection);
        if (string.IsNullOrEmpty(secretRef)) throw LoginRefused();
        var first = await ResolveAsync(secrets, secretRef, ct).ConfigureAwait(false);
        Array.Clear(first.Password);
        return new SqlClientMsSqlSession(connection with { UserName = first.User }, async token =>
        {
            var c = await ResolveAsync(secrets, secretRef, token).ConfigureAwait(false);
            if (!string.Equals(c.User, first.User, StringComparison.Ordinal))
            {
                Array.Clear(c.Password);
                throw LoginRefused(); // wpis zmieniony w trakcie zadania — bez mieszania loginu i hasła z dwóch wersji
            }
            return c.Password;
        });
    }

    private static async ValueTask<(string User, char[] Password)> ResolveAsync(ISftpCredentialProvider secrets, string secretRef, CancellationToken ct)
    {
        SftpCredential cred;
        try
        {
            cred = await secrets.ResolveAsync(secretRef, CredentialRole.Backup, ct).ConfigureAwait(false);
        }
        catch (SftpProtocolException)
        {
            throw LoginRefused();
        }
        if (string.IsNullOrEmpty(cred.Password) || cred.PrivateKey is not null) throw LoginRefused();
        return (cred.UserName, cred.Password.ToCharArray());
    }

    private static KobianException LoginRefused() =>
        new(KbCode.SqlLoginFailed, ErrorClass.Permanent, "SQL login credential is missing or invalid.");
}
