using System.Data;
using System.Security;
using System.Security.Authentication;
using Microsoft.Data.SqlClient;

namespace Kobian.Providers.Sources.MsSql.SqlClient;

/// <summary>Dostawca hasła loginu SQL (SecretRef → magazyn sekretów Agenta; DPAPI na Windows). Wynik nie jest buforowany.</summary>
public delegate ValueTask<char[]> SqlPasswordProvider(CancellationToken cancellationToken);

/// <summary>
/// Adapter produkcyjny <see cref="IMsSqlSession"/> na <c>Microsoft.Data.SqlClient</c> 7.1.1 (BL-40 cz. 2, plan R2.5.1/3097):
/// jedno połączenie na sesję (zadanie), zapytania z limitem <see cref="MsSqlConnectionOptions.QueryTimeoutSeconds"/>,
/// BACKUP/RESTORE z <c>CommandTimeout = 0</c> i przerwaniem tokenem (<c>SqlCommand.Cancel</c> wewnątrz SqlClient),
/// parametry wyłącznie przez <see cref="SqlParameterMapper"/> (SEC-5.1), błędy jako <see cref="MsSqlServerException"/>
/// z samymi numerami (tekst komunikatu serwera może zawierać nazwy obiektów klienta — NFR-11).
/// Wykonanie na prawdziwym SQL Server: kolejka Windows W-24 (S5c = bez kontenerów).
/// </summary>
public sealed class SqlClientMsSqlSession : IMsSqlSession, IAsyncDisposable
{
    /// <summary>Numer zastępczy dla nieudanego uzgodnienia TLS bez numeru serwera (zarządzany SNI: AuthenticationException) → KB-3004.</summary>
    public const int TlsHandshakeFailed = -2146893019;

    private readonly MsSqlConnectionOptions _options;
    private readonly SqlPasswordProvider? _password;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqlConnection? _connection;

    public SqlClientMsSqlSession(MsSqlConnectionOptions options, SqlPasswordProvider? password = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = MsSqlConnectionFactory.Build(options); // walidacja od razu (KB-0103), nie przy pierwszym zapytaniu
        if (options.Authentication == MsSqlAuthentication.SqlLogin && password is null)
            throw new ArgumentException("SQL login requires a password provider.", nameof(password));
        _options = options;
        _password = password;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(SqlCommandSpec command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = Create(conn, command, _options.QueryTimeoutSeconds);
            try
            {
                await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false);
                var rows = new List<IReadOnlyDictionary<string, object?>>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        var v = reader.GetValue(i);
                        row[reader.GetName(i)] = v is DBNull ? null : v;
                    }

                    rows.Add(row);
                }

                return rows;
            }
            catch (SqlException e)
            {
                throw Translate(e);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ExecuteAsync(SqlCommandSpec command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = Create(conn, command, timeoutSeconds: 0);
            try
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException e)
            {
                throw Translate(e);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
        _connection = null;
        _gate.Dispose();
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (_connection is { State: ConnectionState.Open }) return _connection;
        if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
        _connection = null;

        var builder = MsSqlConnectionFactory.Build(_options);
        SqlConnection conn;
        if (_options.Authentication == MsSqlAuthentication.SqlLogin)
        {
            var chars = await _password!(cancellationToken).ConfigureAwait(false);
            try
            {
                conn = new SqlConnection(builder.ConnectionString, ToCredential(_options.UserName!, chars));
            }
            finally
            {
                Array.Clear(chars); // SEC: zerowanie bufora z sekretem
            }
        }
        else
        {
            conn = new SqlConnection(builder.ConnectionString);
        }

        try
        {
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException e)
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw Translate(e);
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _connection = conn;
        return conn;
    }

    private static SqlCredential ToCredential(string user, char[] password)
    {
        // SqlCredential wymaga SecureString — jedyny sposób na hasło poza łańcuchem połączenia (PersistSecurityInfo=false).
        var secure = new SecureString();
        foreach (var c in password) secure.AppendChar(c);
        secure.MakeReadOnly();
        return new SqlCredential(user, secure);
    }

    private static SqlCommand Create(SqlConnection conn, SqlCommandSpec spec, int timeoutSeconds)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandType = CommandType.Text;
        cmd.CommandText = spec.Text;
        cmd.CommandTimeout = timeoutSeconds;
        foreach (var (name, value) in spec.Parameters) cmd.Parameters.Add(SqlParameterMapper.Map(name, value));
        return cmd;
    }

    /// <summary>SqlException → <see cref="MsSqlServerException"/> (same numery, bez tekstu serwera; wyjątek wewnętrzny NIE jest dołączany,
    /// bo jego Message trafiałby do logów).</summary>
    internal static MsSqlServerException Translate(SqlException e)
    {
        var numbers = new List<int>();
        foreach (SqlError err in e.Errors) numbers.Add(err.Number);
        if (HasAuthenticationFailure(e) && !numbers.Contains(TlsHandshakeFailed)) numbers.Add(TlsHandshakeFailed);
        return new MsSqlServerException(numbers);
    }

    internal static bool HasAuthenticationFailure(Exception e)
    {
        for (var x = e.InnerException; x is not null; x = x.InnerException)
            if (x is AuthenticationException) return true;
        return false;
    }
}
