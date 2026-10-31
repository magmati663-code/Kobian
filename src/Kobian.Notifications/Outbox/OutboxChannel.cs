using Kobian.Domain;
using Kobian.Notifications.Webhooks;

namespace Kobian.Notifications.Outbox;

/// <summary>Wynik jednej próby doręczenia w kanale z kodem KB przyczyny (null = brak szczegółów); kod trafia do audytu przy przejściu w <c>dead</c>.</summary>
public readonly record struct ChannelDelivery(DeliveryResult Result, string? KbCode = null);

/// <summary>
/// Port kanału konsumowanego z trwałej kolejki <c>core_outbox</c> (R2.2.3 NotificationDispatcher, D9.10; BL-45): nazwa kanału = wartość kolumny
/// <c>channel</c>, ładunek = koperta CloudEvents. Kanał nie zna katalogu — ponowienia, dead-letter i audyt robi dyspozytor w Agencie.
/// </summary>
public interface IOutboxChannel
{
    string Name { get; }

    /// <summary>Kod KB zapisywany w audycie, gdy wpis przechodzi w <c>dead</c>, a próba nie podała własnego.</summary>
    string DeadKbCode { get; }

    Task<ChannelDelivery> DeliverAsync(string eventId, ReadOnlyMemory<byte> payload, CancellationToken ct);
}

/// <summary>Adapter webhooka (D9.10) do <see cref="IOutboxChannel"/>.</summary>
public sealed class WebhookOutboxChannel(IWebhookSender sender) : IOutboxChannel
{
    public const string ChannelName = "webhook";

    public string Name => ChannelName;

    public string DeadKbCode => KbCode.WebhookDeliveryFailed;

    public async Task<ChannelDelivery> DeliverAsync(string eventId, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
        new(await sender.SendAsync(eventId, payload, ct).ConfigureAwait(false));
}
