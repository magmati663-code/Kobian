using Kobian.UI.Wizard;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>Okno wyboru folderów w kreatorze: wybrane foldery trafiają na listę źródeł jak wpisane ręcznie; przycisk celu tylko dla dysku lokalnego i SMB.</summary>
public sealed class WizardFolderPickerTests
{
    [Fact]
    public void Picked_folders_are_added_like_typed_ones_without_duplicates()
    {
        var vm = new WizardViewModel(new FakeWizardService());
        vm.AddPickedSource(@"C:\Users\jan\Dokumenty");
        vm.AddPickedSource(@"C:\Users\jan\Zdjęcia");
        vm.AddPickedSource(@"c:\users\jan\dokumenty");
        Assert.Equal([@"C:\Users\jan\Dokumenty", @"C:\Users\jan\Zdjęcia"], vm.Sources);
        Assert.Equal("", vm.NewSource);
        Assert.False(string.IsNullOrEmpty(vm.JobName));
    }

    [Fact]
    public void Destination_browse_is_offered_only_for_local_disk_and_smb()
    {
        var vm = new WizardViewModel(new FakeWizardService());
        foreach (var d in vm.Destinations)
        {
            vm.SelectedDestination = d;
            Assert.Equal(d.Value is DestinationKind.Local or DestinationKind.Smb, vm.CanBrowseDestination);
        }
    }
}
