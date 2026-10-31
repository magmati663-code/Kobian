using System.Security.Authentication;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Kobian.Notifications.Email;

/// <summary>Punkt docelowy SMTP (bez sekretów).</summary>
public sealed record SmtpEndpoint(string Host, int Port, SmtpTlsMode Tls);

/// <summary>
/// Port transportu SMTP (BL-45): połącz → (uwierzytelnij) → wyślij → rozłącz. Wyjątki — typy MailKit (<c>AuthenticationException</c>,
/// <c>SmtpCommandException</c>, <c>SslHandshakeException</c>, …) albo sieciowe; klasyfikuje je <see cref="EmailSender"/>. Testy: atrapa in-proc.
/// </summary>
public interface ISmtpTransport
{
    Task SendAsync(SmtpEndpoint endpoint, SmtpCredential? credential, MimeMessage message, CancellationToken ct);
}

/// <summary>
/// Adapter MailKit <see cref="SmtpClient"/> (MailKit/MimeKit ≥ 4.18.1 — SEC-9.1). Walidacja certyfikatu serwera — domyślna (łańcuch zaufania i nazwa hosta;
/// <c>ServerCertificateValidationCallback</c> NIE jest ustawiany), TLS 1.2/1.3, STARTTLS wymagany (<see cref="SecureSocketOptions.StartTls"/> — brak
/// rozszerzenia = błąd, nie cichy powrót do jawnego tekstu). Limit operacji <see cref="OperationTimeout"/>; całość ogranicza wywołujący.
/// Mechanizmy uwierzytelnienia tylko po TLS (jawne SMTP — bez poświadczeń, wymusza <see cref="EmailConfig"/>).
/// </summary>
public sealed class MailKitSmtpTransport : ISmtpTransport
{
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Nazwa w EHLO — stała, bez nazwy komputera (minimalizacja metadanych).</summary>
    public const string HeloName = "kobian.localdomain";

    public async Task SendAsync(SmtpEndpoint endpoint, SmtpCredential? credential, MimeMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(message);
        using var client = new SmtpClient
        {
            Timeout = (int)OperationTimeout.TotalMilliseconds,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            LocalDomain = HeloName,
        };
        var options = endpoint.Tls switch
        {
            SmtpTlsMode.Implicit => SecureSocketOptions.SslOnConnect,
            SmtpTlsMode.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        await client.ConnectAsync(endpoint.Host, endpoint.Port, options, ct).ConfigureAwait(false);
        try
        {
            if (credential is not null)
            {
                if (!client.IsSecure) throw new MailKit.Security.AuthenticationException("Refusing to authenticate over an unencrypted connection.");
                if (credential.TokenProvider is { } tokens)
                {
                    var token = await tokens.GetAccessTokenAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await client.AuthenticateAsync(new SaslMechanismOAuth2(credential.Login, token), ct).ConfigureAwait(false);
                    }
                    catch (MailKit.Security.AuthenticationException)
                    {
                        tokens.Invalidate(); // następna próba pobierze świeży token
                        throw;
                    }
                }
                else
                {
                    await client.AuthenticateAsync(credential.Login, credential.Secret, ct).ConfigureAwait(false);
                }
            }
            await client.SendAsync(message, ct).ConfigureAwait(false);
        }
        finally
        {
            if (client.IsConnected)
            {
                try
                {
                    await client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SmtpProtocolException or SmtpCommandException or OperationCanceledException)
                {
                    // QUIT po wysłaniu jest grzecznościowy — wiadomość już przyjęta albo błąd zgłoszony wyżej
                }
            }
        }
    }
}
