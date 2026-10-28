using System.Security.Cryptography;
using Kobian.Application.Jobs;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.UI.Wizard;
using Xunit;

namespace Kobian.Tests.Unit.Ux;

public sealed class RecoveryKeyTests
{
    [Fact]
    public void Crc16_ccitt_false_check_value() =>
        Assert.Equal(0x29B1, RecoveryKey.Crc16Ccitt("123456789"u8));

    [Fact, Trait("AC", "AC-28.1"), Trait("K", "K14.1")]
    public void Encodes_to_55_chars_in_14_groups_and_roundtrips()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var text = RecoveryKey.Encode(key);
        var groups = RecoveryKey.Groups(text);
        Assert.Equal(14, groups.Length);
        Assert.All(groups[..13], g => Assert.Equal(4, g.Length));
        Assert.Equal(3, groups[13].Length);
        Assert.Equal(55, text.Replace("-", "", StringComparison.Ordinal).Length);
        var back = new byte[32];
        Assert.True(RecoveryKey.TryDecode(text.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal), back));
        Assert.Equal(key, back);
    }

    [Fact, Trait("K", "K14.1")]
    public void Every_single_character_typo_is_detected()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var raw = RecoveryKey.Encode(key).Replace("-", "", StringComparison.Ordinal).ToCharArray();
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var buf = new byte[32];
        for (var i = 0; i < raw.Length; i++)
        {
            var orig = raw[i];
            foreach (var c in alphabet)
            {
                if (c == orig) continue;
                raw[i] = c;
                Assert.False(RecoveryKey.TryDecode(new string(raw), buf), $"pos {i} char {c}");
            }
            raw[i] = orig;
        }
    }

    [Fact]
    public void Zero_and_one_are_read_as_O_and_I()
    {
        var key = new byte[32];
        string text;
        do
        {
            RandomNumberGenerator.Fill(key);
            text = RecoveryKey.Encode(key);
        } while (!text.Contains('O', StringComparison.Ordinal));
        var buf = new byte[32];
        Assert.True(RecoveryKey.TryDecode(text.Replace('O', '0'), buf));
        Assert.Equal(key, buf);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCD")]
    [InlineData("8BCD-EFGH")]
    public void Garbage_is_rejected(string s) => Assert.False(RecoveryKey.TryDecode(s, new byte[32]));

    [Fact, Trait("AC", "AC-28.1")]
    public void Confirmation_picks_two_distinct_groups_and_checks_input()
    {
        var text = RecoveryKey.Encode(RandomNumberGenerator.GetBytes(32));
        for (var i = 0; i < 200; i++)
        {
            var (a, b) = RecoveryKey.PickConfirmationGroups();
            Assert.InRange(a, 0, 13);
            Assert.InRange(b, 0, 13);
            Assert.True(a < b);
        }
        var g = RecoveryKey.Groups(text)[5];
        Assert.True(RecoveryKey.ConfirmGroup(text, 5, " " + g.ToLowerInvariant() + " "));
        Assert.False(RecoveryKey.ConfirmGroup(text, 5, "ZZZZ"));
    }
}

public sealed class JobValidatorTests
{
    private static readonly BuiltInCommonPasswords Common = new();

    [Theory, Trait("AC", "AC-01.2")]
    [InlineData(@"C:\Dane|C:\Dane\Faktury")]
    [InlineData(@"C:\dane\|c:\DANE")]
    [InlineData(@"D:\x\y|D:\X")]
    [InlineData(@"C:\Data|C:\Data\..\Data\sub")] // „..” w ścieżce nie ukrywa nakładania
    [InlineData(@"C:\Data\.|C:\Data\sub")]
    [InlineData(@"\\?\C:\Data|C:\Data\sub")] // prefiks długich ścieżek Windows
    [InlineData(@"\\srv\share|\\?\UNC\srv\share\dir")]
    [InlineData(@"\\srv\a\..\b|\\srv\a\b\sub")] // „..” nie kasuje udziału UNC (jak w Windows: \\srv\a\b)
    public void Overlapping_sources_are_rejected(string sources)
    {
        var issues = JobValidator.ValidateSources(sources.Split('|'));
        Assert.Contains(issues, i => i.KbCode == KbCode.SourcesOverlap);
    }

    [Fact]
    public void Sibling_with_common_prefix_is_not_overlap() =>
        Assert.Empty(JobValidator.ValidateSources([@"C:\Dane", @"C:\Dane2"]));

    [Fact]
    public void Dot_dot_that_leaves_the_folder_is_not_overlap() =>
        Assert.Empty(JobValidator.ValidateSources([@"C:\Dane", @"C:\Dane\..\Dane2"]));

    [Theory, Trait("SEC", "SEC-6.5")]
    [InlineData("short1!")]
    [InlineData("Haslo1234567!")]
    [InlineData("123456789012")]
    [InlineData("qwertyuiopas")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    [InlineData("mateusz-zima-2026")]
    [InlineData("biuro-pc01-bezpieczne")]
    public void Weak_passwords_are_rejected(string pw) =>
        Assert.Contains(JobValidator.ValidatePassword(pw, "mateusz", "BIURO-PC01", Common), i => i.KbCode == KbCode.PasswordWeak);

    [Theory, Trait("SEC", "SEC-6.5")]
    [InlineData("kot w butach jedzie tramwajem")]
    [InlineData("Zielony-Most-Nad-Wisla-7")]
    public void Long_unusual_passphrases_are_accepted(string pw) =>
        Assert.Empty(JobValidator.ValidatePassword(pw, "mateusz", "BIURO-PC01", Common));

    [Fact, Trait("AC", "AC-10.1")]
    public void Ac10_policy_is_valid()
    {
        var p = new RetentionPolicy(KeepLast: 7, KeepDaily: 14, RetentionLock: TimeSpan.FromDays(14));
        Assert.Empty(JobValidator.ValidateRetention(p));
    }

    [Fact]
    public void Lock_longer_than_shortest_retention_is_rejected()
    {
        var p = new RetentionPolicy(KeepDaily: 7, RetentionLock: TimeSpan.FromDays(30));
        Assert.Contains(JobValidator.ValidateRetention(p), i => i.Field == "retention.retentionLock");
    }

    [Fact]
    public void Empty_policy_is_rejected() =>
        Assert.Contains(JobValidator.ValidateRetention(new RetentionPolicy()), i => i.KbCode == KbCode.RetentionInvalid);
}

public sealed class WizardFlowTests
{
    private static readonly WizardContext Ok = new(StepValid: true, RepositoryCreated: false, RunNow: true);

    [Fact, Trait("AC", "AC-01.1")]
    public void Happy_path_new_repository_with_first_backup()
    {
        var s = WizardState.S1Sources;
        var ctx = Ok;
        s = WizardFlow.Next(s, WizardEvent.Next, ctx);
        s = WizardFlow.Next(s, WizardEvent.Next, ctx);
        s = WizardFlow.Next(s, WizardEvent.ProbeOk, ctx);
        s = WizardFlow.Next(s, WizardEvent.Next, ctx);
        Assert.Equal(WizardState.S4Security, s);
        s = WizardFlow.Next(s, WizardEvent.RepositoryCreated, ctx);
        ctx = ctx with { RepositoryCreated = true };
        Assert.False(WizardFlow.CanGoBack(s, ctx));
        s = WizardFlow.Next(s, WizardEvent.RecoveryKeyConfirmed, ctx);
        s = WizardFlow.Next(s, WizardEvent.Next, ctx);
        s = WizardFlow.Next(s, WizardEvent.SaveSucceeded, ctx);
        s = WizardFlow.Next(s, WizardEvent.Next, ctx);
        s = WizardFlow.Next(s, WizardEvent.FirstBackupSucceeded, ctx);
        s = WizardFlow.Next(s, WizardEvent.MiniTestFinished, ctx);
        Assert.Equal(WizardState.Done, s);
    }

    [Fact, Trait("AC", "AC-01.2")]
    public void Invalid_step_blocks_next_and_permanent_probe_error_returns_to_destination()
    {
        Assert.Equal(WizardState.S1Sources, WizardFlow.Next(WizardState.S1Sources, WizardEvent.Next, Ok with { StepValid = false }));
        Assert.Equal(WizardState.S2Destination, WizardFlow.Next(WizardState.S2Probe, WizardEvent.ProbePermanentError, Ok));
    }

    [Fact]
    public void After_repository_creation_step4_is_skipped_both_ways()
    {
        var ctx = Ok with { RepositoryCreated = true };
        Assert.Equal(WizardState.S3Schedule, WizardFlow.Next(WizardState.S5Retention, WizardEvent.Back, ctx));
        Assert.Equal(WizardState.S5Retention, WizardFlow.Next(WizardState.S3Schedule, WizardEvent.Next, ctx));
    }

    [Fact]
    public void Random_event_sequences_respect_invariants()
    {
        var events = Enum.GetValues<WizardEvent>();
        for (var run = 0; run < 2000; run++)
        {
            var s = WizardState.S1Sources;
            var created = false;
            var saved = false;
            for (var step = 0; step < 40; step++)
            {
                var e = events[RandomNumberGenerator.GetInt32(events.Length)];
                var ctx = new WizardContext(RandomNumberGenerator.GetInt32(2) == 0, created, RandomNumberGenerator.GetInt32(2) == 0);
                var prev = s;
                s = WizardFlow.Next(s, e, ctx);
                if (prev == WizardState.S4Security && e == WizardEvent.RepositoryCreated) created = true;
                if (s == WizardState.Saved) saved = true;
                // I1: po utworzeniu repo nigdy nie wracamy do S4Security
                if (created && prev != WizardState.S4Security) Assert.NotEqual(WizardState.S4Security, s);
                // I2: Done tylko po zapisie
                if (s == WizardState.Done) Assert.True(saved);
                // I3: Cancelled jest stanem końcowym
                if (prev == WizardState.Cancelled) Assert.Equal(WizardState.Cancelled, s);
            }
        }
    }
}
