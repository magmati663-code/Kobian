using System.Net.Sockets;
using Kobian.Domain;
using Kobian.Notifications.Outbox;
using Kobian.Notifications.Webhooks;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace Kobian.Notifications.Email;

/// <summary>
/// Kanał e-mail outboxa (BL-45; 9.6, 9.7, SEC-9.1): koperta CloudEvents → <see cref="EmailMessageBuilder"/> → <see cref="ISmtpTransport"/>.
/// Całość próby ≤ <see cref="TotalTimeout"/>. Klasyfikacja błędów (D3.14):
/// <list type="bullet">
/// <item>trwałe (bez ponowień → <c>dead</c>): odrzucone uwierzytelnienie / 530/534/535/538, „5.7.30” (KB-6201); odpowiedź 5xx serwera, np. odbiorca odrzucony (KB-6203);
/// ładunek nieobsługiwany (KB-6203);</item>
/// <item>przejściowe (ponowienie wg harmonogramu outboxa): 4xx, błędy protokołu, sieci, limit czasu (KB-6203); TLS/certyfikat/brak STARTTLS (KB-6206 —
/// ponawiane, bo awaria certyfikatu bywa chwilowa; PROWIZORYCZNIE Qb66).</item>
/// </list>
/// Komunikaty serwera NIE są przenoszone dalej (mogą zawierać adresy) — tylko kod KB.
/// </summary>
public sealed class EmailSender(EmailSettings settings, ISmtpTransport transport) : IOutboxChannel
{
    public const string ChannelName = "email";
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(90);

    private readonly SmtpEndpoint _endpoint = new(settings.Host, settings.Port, settings.Tls);

    public string Name => ChannelName;

    public string DeadKbCode => KbCode.EmailDeliveryFailed;

    public EmailSettings Settings => settings;

    /// <summary>Limit całej próby (domyślnie <see cref="TotalTimeout"/>).</summary>
    public TimeSpan Timeout { get; init; } = TotalTimeout;

    public async Task<ChannelDelivery> DeliverAsync(string eventId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        MimeKit.MimeMessage message;
        try
        {
            message = EmailMessageBuilder.Build(settings, payload.Span);
        }
        catch (FormatException)
        {
            return new(DeliveryResult.Permanent, KbCode.EmailDeliveryFailed);
        }
        using (message)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(Timeout);
            try
            {
                await transport.SendAsync(_endpoint, settings.Credential, message, timeout.Token).ConfigureAwait(false);
                return new(DeliveryResult.Delivered);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new(DeliveryResult.Retry, KbCode.EmailDeliveryFailed); // limit czasu próby
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && Classify(ex) is { } d)
            {
                return d;
            }
        }
    }

    /// <summary>Klasyfikacja wyjątku transportu; null = nieznany typ (propaguje — błąd programu, nie kanału).</summary>
    public static ChannelDelivery? Classify(Exception ex) => ex switch
    {
        AuthenticationException => new(DeliveryResult.Permanent, KbCode.SmtpAuthRejected),
        SmtpCommandException c when IsAuthStatus(c) => new(DeliveryResult.Permanent, KbCode.SmtpAuthRejected),
        SmtpCommandException c when (int)c.StatusCode >= 500 => new(DeliveryResult.Permanent, KbCode.EmailDeliveryFailed),
        SmtpCommandException => new(DeliveryResult.Retry, KbCode.EmailDeliveryFailed),
        SslHandshakeException => new(DeliveryResult.Retry, KbCode.SmtpTlsFailed),
        System.Security.Authentication.AuthenticationException => new(DeliveryResult.Retry, KbCode.SmtpTlsFailed),
        NotSupportedException => new(DeliveryResult.Retry, KbCode.SmtpTlsFailed), // MailKit: serwer bez STARTTLS przy SecureSocketOptions.StartTls
        SmtpProtocolException or ServiceNotConnectedException or ProtocolException or IOException or SocketException or TimeoutException
            => new(DeliveryResult.Retry, KbCode.EmailDeliveryFailed),
        _ => null,
    };

    private static bool IsAuthStatus(SmtpCommandException c) =>
        (int)c.StatusCode is 530 or 534 or 535 or 538 || c.Message.Contains("5.7.30", StringComparison.Ordinal);
}
