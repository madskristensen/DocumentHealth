global using System;
global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
global using Task = System.Threading.Tasks.Task;
using System.Runtime.InteropServices;

namespace DocumentHealth
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuids.DocumentHealthString)]
    [ProvideOptionPage(typeof(OptionsProvider.GeneralOptions), "Environment", Vsix.Name, 0, 0, true, SupportsProfiles = true)]
    public sealed class DocumentHealthPackage : ToolkitPackage
    {
    }
}