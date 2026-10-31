using Kobian.Api.V1;
using Kobian.Domain;
using Kobian.UI.Services;
using Kobian.UI.Wizard;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>BL-61 (it. 63): mapowanie odpowiedzi Agenta w adapterze kreatora (czyste — bez transportu). Ścieżka przez pipe: Kobian.Tests.Unit WizardOverPipeTests.</summary>
public sealed class GrpcWizardServiceTests
{
    [Fact, Trait("BL", "BL-61")]
    public void Adapter_maps_replies_and_destination_without_secrets()
    {
        Assert.Equal(new ProbeResult(ProbeOutcome.UnknownIdentity, null, "SHA256:x"),
            GrpcWizardService.Map(new ProbeDestinationReply { Status = ProbeStatus.UnknownIdentity, Code = KbCode.HostKeyMismatch, IdentityFingerprint = "SHA256:x" }));
        Assert.Equal(new ProbeResult(ProbeOutcome.PermanentError, KbCode.HostKeyMismatch, "SHA256:y"),
            GrpcWizardService.Map(new ProbeDestinationReply { Status = ProbeStatus.Failed, Code = KbCode.HostKeyMismatch, IdentityFingerprint = "SHA256:y" }));
        Assert.Equal(ProbeOutcome.PermanentError, GrpcWizardService.Map(new ProbeDestinationReply { Status = ProbeStatus.UnknownIdentity }).Outcome); // bez odcisku nie ma czego zatwierdzić
        Assert.Equal([new StepIssue("job", KbCode.OperationNotAvailable)], GrpcWizardService.Map(new InitRepositoryReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable }).Issues);

        var sftp = GrpcWizardService.Destination(new WizardDraft("A", [], DestinationKind.Sftp, " /kopie ", " nas.example.test ", 2222, " backup ", "SHA256:abc", null, 7, 4, 12));
        Assert.Equal(("sftp", "/kopie", "nas.example.test", 2222, "file:backup", "SHA256:abc"), (sftp.Scheme, sftp.Root, sftp.Host, sftp.Port, sftp.SecretRef, sftp.HostKey));
        Assert.Equal(["database", "name", "sources"], GrpcWizardService.FieldsFor(WizardState.S1Sources).Order(StringComparer.Ordinal));
        Assert.Empty(GrpcWizardService.FieldsFor(WizardState.S4Security));
    }
}
