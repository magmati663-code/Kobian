using System.Security.Cryptography;
using Kobian.Agent.Jobs;
using Kobian.Domain;
using Xunit;
using E = Kobian.Agent.Jobs.ServerIdentityEvent;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// BL-35 (D4.11, SEC-14.5, AC-12.2; zamyka L6): czysta maszyna stanów zatwierdzania tożsamości serwera — każde przejście dozwolone i zabronione
/// oraz test właściwości na losowych ciągach zdarzeń (niezmienniki: brak TOFU, pin tylko z decyzji Administratora albo z definicji przy braku pinu,
/// odrzucony klucz nigdy nie wraca, limity zbiorów). Odciski losowe (syntetyczne), zegar w argumencie.
/// </summary>
public sealed class ServerIdentityMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);
    private const string Admin = "S-1-5-21-1000-2000-3000-500";
    private const string Ssh = ServerIdentityMachine.SshHostKey;
    private const string Tls = ServerIdentityMachine.TlsSpki;
    private const string Ep = "sftp://backup.example.test:22";

    internal static string Fp() => "SHA256:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=');

    private static ServerIdentityTransition Run(IReadOnlyList<ServerIdentityRow> rows, E ev, DateTimeOffset? now = null) =>
        ServerIdentityMachine.Apply(rows, ev, now ?? T0);

    private static IReadOnlyList<ServerIdentityRow> Pinned(string fp)
    {
        var t = Run([], new E.Observed(Ssh, fp, Ep, "ssh-ed25519"));
        return Run(t.Rows, new E.Approve(fp, false, Admin)).Rows;
    }

    private static IdentityState StateOf(IReadOnlyList<ServerIdentityRow> rows, string fp) => rows.Single(r => r.Fingerprint == fp).State;

    [Fact, Trait("D", "D4.11"), Trait("AC", "AC-04.1")]
    public void First_contact_is_pending_never_trusted_and_repeated_contact_keeps_it_pending()
    {
        var k = Fp();
        var t = Run([], new E.Observed(Ssh, k, Ep, "ssh-ed25519"));
        Assert.Equal((ServerIdentityNotice.NewPending, (string?)null), (t.Notice, t.Code));
        var row = Assert.Single(t.Rows);
        Assert.Equal((IdentityState.Pending, (string?)null, (DateTimeOffset?)null), (row.State, row.DecidedBy, row.DecidedAt));
        Assert.Empty(ServerIdentityMachine.EffectivePins(t.Rows)); // brak TOFU: połączenie nadal odmówione

        var again = Run(t.Rows, new E.Observed(Ssh, k, Ep, null), T0.AddHours(1));
        Assert.Equal(ServerIdentityNotice.StillPending, again.Notice);
        Assert.Equal((T0, T0.AddHours(1)), (again.Rows[0].FirstSeen, again.Rows[0].LastSeen)); // 24 h liczone od pierwszego kontaktu
    }

    [Fact, Trait("D", "D4.11"), Trait("SEC", "SEC-4.1")]
    public void Approval_pins_only_the_exact_pending_fingerprint_and_records_the_administrator()
    {
        var k = Fp();
        var pending = Run([], new E.Observed(Ssh, k, Ep, null)).Rows;
        var t = Run(pending, new E.Approve(k, false, Admin), T0.AddMinutes(5));
        Assert.Null(t.Code);
        Assert.Equal(ServerIdentityNotice.Approved, t.Notice);
        Assert.Equal((IdentityState.Pinned, Admin, (DateTimeOffset?)T0.AddMinutes(5)), (t.Rows[0].State, t.Rows[0].DecidedBy, t.Rows[0].DecidedAt));
        Assert.Equal([k], ServerIdentityMachine.EffectivePins(t.Rows));

        // Odcisk różniący się jednym znakiem, nigdy nie widziany, albo inna wielkość liter — odmowa KB-2141 bez zmian (ta sama lista).
        var near = k[..^1] + (k[^1] == 'A' ? 'B' : 'A');
        var otherCase = "SHA256:" + new string([.. k[7..].Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c))]);
        foreach (var bad in new[] { near, Fp(), otherCase, k + "=" })
        {
            var r = Run(pending, new E.Approve(bad, false, Admin));
            Assert.Equal(KbCode.ServerIdentityNotPending, r.Code);
            Assert.Same(pending, r.Rows);
        }
    }

    [Fact, Trait("D", "D4.11")]
    public void Only_pending_can_be_decided_pinned_rejected_and_retired_cannot()
    {
        var a = Fp();
        var b = Fp();
        var rows = Pinned(a);
        rows = Run(rows, new E.Observed(Ssh, b, Ep, null)).Rows;
        rows = Run(rows, new E.Approve(b, true, Admin)).Rows; // a → retired
        Assert.Equal(IdentityState.Retired, StateOf(rows, a));
        foreach (var fp in new[] { a, b })
        {
            Assert.Equal(KbCode.ServerIdentityNotPending, Run(rows, new E.Approve(fp, false, Admin)).Code);
            Assert.Equal(KbCode.ServerIdentityNotPending, Run(rows, new E.Reject(fp, Admin)).Code);
        }
        var c = Fp();
        rows = Run(rows, new E.Observed(Ssh, c, Ep, null)).Rows;
        rows = Run(rows, new E.Reject(c, Admin)).Rows;
        Assert.Equal(IdentityState.Rejected, StateOf(rows, c));
        Assert.Equal(KbCode.ServerIdentityNotPending, Run(rows, new E.Approve(c, false, Admin)).Code);
        Assert.Equal(KbCode.ServerIdentityNotPending, Run(rows, new E.Reject(c, Admin)).Code);
    }

    [Fact, Trait("D", "D4.11"), Trait("KB", "KB-2104")]
    public void Key_change_on_a_pinned_destination_adds_a_pending_key_and_keeps_the_old_pin_until_decided()
    {
        var oldKey = Fp();
        var newKey = Fp();
        var rows = Pinned(oldKey);
        var t = Run(rows, new E.Observed(Ssh, newKey, Ep, null), T0.AddDays(30));
        Assert.Equal(ServerIdentityNotice.NewPending, t.Notice);
        Assert.Equal(IdentityState.Pending, StateOf(t.Rows, newKey));
        Assert.Equal([oldKey], ServerIdentityMachine.EffectivePins(t.Rows)); // kopia na tym celu staje z KB-2104/KB-2109, stary pin zostaje

        var replaced = Run(t.Rows, new E.Approve(newKey, true, Admin), T0.AddDays(30).AddHours(1)).Rows;
        Assert.Equal([newKey], ServerIdentityMachine.EffectivePins(replaced));
        var retired = replaced.Single(r => r.Fingerprint == oldKey);
        Assert.Equal((IdentityState.Retired, Admin), (retired.State, retired.DecidedBy));
    }

    [Fact, Trait("AC", "AC-12.2"), Trait("D", "D4.11")]
    public void Add_keeps_existing_pins_for_a_cluster_up_to_four_then_requires_replace()
    {
        var keys = Enumerable.Range(0, 5).Select(_ => Fp()).ToList();
        IReadOnlyList<ServerIdentityRow> rows = [];
        for (var i = 0; i < 4; i++)
        {
            rows = Run(rows, new E.Observed(Tls, keys[i], Ep, null), T0.AddMinutes(i)).Rows;
            var t = Run(rows, new E.Approve(keys[i], false, Admin), T0.AddMinutes(i));
            Assert.Null(t.Code);
            rows = t.Rows;
        }
        Assert.Equal(4, ServerIdentityMachine.EffectivePins(rows).Count);
        rows = Run(rows, new E.Observed(Tls, keys[4], Ep, null)).Rows;
        var limit = Run(rows, new E.Approve(keys[4], false, Admin));
        Assert.Equal(KbCode.ServerIdentityPinLimit, limit.Code);
        Assert.Same(rows, limit.Rows);
        var replace = Run(rows, new E.Approve(keys[4], true, Admin)).Rows;
        Assert.Equal([keys[4]], ServerIdentityMachine.EffectivePins(replace));
    }

    [Fact, Trait("D", "D4.11")]
    public void Pending_expires_after_24_hours_cannot_be_approved_but_can_be_rejected_and_a_new_contact_renews_it()
    {
        var k = Fp();
        var rows = Run([], new E.Observed(Ssh, k, Ep, null)).Rows;
        var late = T0 + ServerIdentityMachine.PendingTtl + TimeSpan.FromSeconds(1);
        Assert.True(rows[0].IsExpired(late));
        Assert.False(rows[0].IsExpired(T0 + ServerIdentityMachine.PendingTtl)); // granica włącznie
        Assert.Equal(KbCode.ServerIdentityApprovalExpired, Run(rows, new E.Approve(k, false, Admin), late).Code);
        Assert.Equal(IdentityState.Rejected, StateOf(Run(rows, new E.Reject(k, Admin), late).Rows, k));

        var renewed = Run(rows, new E.Observed(Ssh, k, Ep, null), late);
        Assert.Equal(ServerIdentityNotice.NewPending, renewed.Notice);
        Assert.Equal(late, renewed.Rows[0].FirstSeen);
        Assert.Null(Run(renewed.Rows, new E.Approve(k, false, Admin), late.AddMinutes(1)).Code);
    }

    [Fact, Trait("D", "D4.11")]
    public void Rejected_key_stays_rejected_on_new_contacts_definition_seed_and_ca()
    {
        var k = Fp();
        var rows = Run(Run([], new E.Observed(Tls, k, Ep, null)).Rows, new E.Reject(k, Admin)).Rows;
        foreach (var ev in new E[] { new E.Observed(Tls, k, Ep, null), new E.SeedFromDefinition(Tls, k, Ep), new E.TrustedByCa(k, Ep, null) })
        {
            var t = Run(rows, ev, T0.AddDays(3));
            Assert.Same(rows, t.Rows);
            Assert.Equal(IdentityState.Rejected, StateOf(t.Rows, k));
        }
    }

    [Fact, Trait("D", "D4.11")]
    public void Retired_key_that_comes_back_needs_approval_again()
    {
        var a = Fp();
        var b = Fp();
        var rows = Pinned(a);
        rows = Run(Run(rows, new E.Observed(Ssh, b, Ep, null)).Rows, new E.Approve(b, true, Admin)).Rows;
        var back = Run(rows, new E.Observed(Ssh, a, Ep, null), T0.AddDays(1));
        Assert.Equal(ServerIdentityNotice.NewPending, back.Notice);
        Assert.Equal(IdentityState.Pending, StateOf(back.Rows, a));
        Assert.Equal([b], ServerIdentityMachine.EffectivePins(back.Rows));
    }

    [Fact, Trait("D", "D4.11"), Trait("AC", "AC-04.1")]
    public void Definition_pin_is_migrated_only_when_the_destination_has_no_pin_so_an_operator_cannot_swap_it()
    {
        var k = Fp();
        var seeded = Run([], new E.SeedFromDefinition(Ssh, k, Ep));
        Assert.Equal(ServerIdentityNotice.Seeded, seeded.Notice);
        Assert.Equal((IdentityState.Pinned, ServerIdentityMachine.DefinitionActor), (seeded.Rows[0].State, seeded.Rows[0].DecidedBy));
        Assert.Equal([k], ServerIdentityMachine.EffectivePins(seeded.Rows));
        Assert.Same(seeded.Rows, Run(seeded.Rows, new E.SeedFromDefinition(Ssh, k, Ep)).Rows); // idempotentne

        var other = Fp();
        var swap = Run(seeded.Rows, new E.SeedFromDefinition(Ssh, other, Ep));
        Assert.Same(seeded.Rows, swap.Rows); // edycja hostKey w zadaniu nie zmienia pinu celu
        // pending (np. z kreatora) → zatwierdzony z definicji
        var p = Fp();
        var fromPending = Run(Run([], new E.Observed(Ssh, p, Ep, null)).Rows, new E.SeedFromDefinition(Ssh, p, Ep)).Rows;
        Assert.Equal(IdentityState.Pinned, StateOf(fromPending, p));
    }

    [Fact, Trait("SEC", "SEC-14.5"), Trait("D", "D4.11")]
    public void Valid_ca_chain_records_spki_as_informational_auto_approval_which_is_not_a_connection_pin()
    {
        var k = Fp();
        var t = Run([], new E.TrustedByCa(k, Ep, "SPKI RSA, subject CN=nas"));
        Assert.Equal(ServerIdentityNotice.AutoApprovedByCa, t.Notice);
        var row = Assert.Single(t.Rows);
        Assert.Equal((IdentityState.Pinned, ServerIdentityMachine.CaChainActor), (row.State, row.DecidedBy));
        Assert.False(row.IsEffectivePin);
        Assert.Empty(ServerIdentityMachine.EffectivePins(t.Rows)); // unieważniony/wygasły certyfikat z tym kluczem NIE przejdzie „przez pin”

        // Cel przeszedł na „wysokie ryzyko”: odmowa z tym SPKI → oczekuje na zatwierdzenie jak SSH.
        var high = Run(t.Rows, new E.Observed(Tls, k, Ep, null), T0.AddDays(1));
        Assert.Equal(IdentityState.Pending, StateOf(high.Rows, k));
        var approved = Run(high.Rows, new E.Approve(k, false, Admin), T0.AddDays(1)).Rows;
        Assert.Equal([k], ServerIdentityMachine.EffectivePins(approved));
        // Zmiana SPKI przy ważnym CA nie dotyka pinów Administratora.
        var renewed = Run(approved, new E.TrustedByCa(Fp(), Ep, null), T0.AddDays(90)).Rows;
        Assert.Equal([k], ServerIdentityMachine.EffectivePins(renewed));
    }

    [Fact, Trait("SEC", "SEC-14.5")]
    public void Ca_records_are_bounded_oldest_retired()
    {
        IReadOnlyList<ServerIdentityRow> rows = [];
        var keys = Enumerable.Range(0, 7).Select(_ => Fp()).ToList();
        for (var i = 0; i < keys.Count; i++) rows = Run(rows, new E.TrustedByCa(keys[i], Ep, null), T0.AddDays(90 * i)).Rows;
        var ca = rows.Where(r => r.State == IdentityState.Pinned).Select(r => r.Fingerprint).ToList();
        Assert.Equal(keys.Skip(3).Order(StringComparer.Ordinal), ca.Order(StringComparer.Ordinal)); // 4 najnowsze
        Assert.Equal(3, rows.Count(r => r.State == IdentityState.Retired));
    }

    [Fact, Trait("D", "D4.11"), Trait("SEC", "SEC-3.3")]
    public void Malformed_input_and_flooding_are_ignored_without_state_change()
    {
        IReadOnlyList<ServerIdentityRow> rows = [];
        foreach (var bad in new[] { "", "SHA256:", "MD5:aa:bb", "SHA256:abc,SHA256:def", "SHA256:abc=", "SHA256:" + new string('A', 60), "SHA256:ab\ncd" })
            Assert.Equal(ServerIdentityNotice.Ignored, Run(rows, new E.Observed(Ssh, bad, Ep, null)).Notice);
        Assert.Equal(ServerIdentityNotice.Ignored, Run(rows, new E.Observed("ssh-rsa-unknown-kind", Fp(), Ep, null)).Notice);
        // Serwer (albo napastnik w sieci) przedstawiający co chwilę inny klucz nie zapcha katalogu: maks. 8 oczekujących.
        for (var i = 0; i < 20; i++) rows = Run(rows, new E.Observed(Ssh, Fp(), Ep, null)).Rows;
        Assert.Equal(ServerIdentityMachine.MaxPending, rows.Count);
        // Niepoprawny aktor (pusty, znaki sterujące) — odmowa.
        var k = rows[0].Fingerprint;
        Assert.Equal(KbCode.ApiAccessDenied, Run(rows, new E.Approve(k, false, "")).Code);
        Assert.Equal(KbCode.ApiAccessDenied, Run(rows, new E.Reject(k, "S-1\n-5")).Code);
        // Opis z serwera: bez znaków sterujących, przycięty.
        var d = Run([], new E.Observed(Ssh, Fp(), Ep, "evil\u001b[31m\r\n" + new string('x', 500))).Rows[0].Display!;
        Assert.DoesNotContain(d, char.IsControl);
        Assert.Equal(ServerIdentityMachine.MaxDisplayLength, d.Length);
    }

    /// <summary>Test właściwości: 300 losowych ciągów po 60 zdarzeń na alfabecie 6 odcisków (ziarno stałe — powtarzalność; inne: KOBIAN_PROP_SEED).</summary>
    [Fact, Trait("D", "D4.11"), Trait("AC", "AC-12.2"), Trait("SEC", "SEC-14.5")]
    public void Property_random_event_sequences_preserve_the_invariants()
    {
        var seed = int.TryParse(Environment.GetEnvironmentVariable("KOBIAN_PROP_SEED"), out var s) ? s : 4111;
        var rnd = new Random(seed); // dane testowe, nie klucze
        var alphabet = Enumerable.Range(0, 6).Select(_ => Fp()).ToArray();
        for (var run = 0; run < 300; run++)
        {
            IReadOnlyList<ServerIdentityRow> rows = [];
            var now = T0;
            for (var step = 0; step < 60; step++)
            {
                now += TimeSpan.FromMinutes(rnd.Next(0, 3) == 0 ? rnd.Next(0, 3000) : rnd.Next(0, 60));
                var fp = alphabet[rnd.Next(alphabet.Length)];
                E ev = rnd.Next(6) switch
                {
                    0 => new E.Observed(rnd.Next(2) == 0 ? Ssh : Tls, fp, Ep, null),
                    1 => new E.Approve(fp, rnd.Next(2) == 0, Admin),
                    2 => new E.Reject(fp, Admin),
                    3 => new E.SeedFromDefinition(Ssh, fp, Ep),
                    4 => new E.TrustedByCa(fp, Ep, null),
                    _ => new E.Observed(Ssh, fp, Ep, null),
                };
                var before = rows;
                var t = ServerIdentityMachine.Apply(before, ev, now);
                var ctx = $"seed={seed} run={run} step={step} ev={ev}";
                if (t.Code is not null) Assert.Same(before, t.Rows); // odmowa = brak zmian
                rows = t.Rows;
                Assert.True(rows.Select(r => r.Fingerprint).Distinct(StringComparer.Ordinal).Count() == rows.Count, ctx);
                Assert.True(ServerIdentityMachine.EffectivePins(rows).Count <= ServerIdentityMachine.MaxPins && rows.Count(r => r.IsEffectivePin) <= ServerIdentityMachine.MaxPins, ctx);
                Assert.True(rows.Count(r => r.State == IdentityState.Pinned && !r.IsEffectivePin) <= ServerIdentityMachine.MaxCaRecords, ctx);
                Assert.True(rows.Count(r => r.State == IdentityState.Pending) <= ServerIdentityMachine.MaxPending && rows.Count <= ServerIdentityMachine.MaxRows, ctx);
                // Lustro CHECK z DDL: oczekujący bez decyzji, pozostałe z decyzją.
                Assert.All(rows, r => Assert.True(r.State == IdentityState.Pending ? r.DecidedBy is null && r.DecidedAt is null : r.DecidedBy is not null && r.DecidedAt is not null, ctx));
                // Odrzucony pozostaje odrzucony.
                foreach (var r in before.Where(r => r.State == IdentityState.Rejected))
                    Assert.Equal(IdentityState.Rejected, StateOf(rows, r.Fingerprint));
                // Brak TOFU: nowy pin połączenia wyłącznie z decyzji Administratora (z oczekującego, niewygasłego) albo z definicji przy braku pinów.
                var gained = rows.Where(r => r.IsEffectivePin).Select(r => r.Fingerprint).Except(before.Where(r => r.IsEffectivePin).Select(r => r.Fingerprint)).ToList();
                foreach (var g in gained)
                {
                    var prev = before.SingleOrDefault(r => r.Fingerprint == g);
                    var viaApproval = ev is E.Approve a && a.Fingerprint == g && prev is { State: IdentityState.Pending } && !prev.IsExpired(now);
                    var viaSeed = ev is E.SeedFromDefinition sd && sd.Fingerprint == g && !before.Any(r => r.IsEffectivePin);
                    Assert.True(viaApproval || viaSeed, ctx);
                }
                if (ev is E.Observed or E.TrustedByCa or E.Reject) Assert.Empty(gained);
            }
        }
    }
}
