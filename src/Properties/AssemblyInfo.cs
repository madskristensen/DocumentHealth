using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using DocumentHealth;

[assembly: AssemblyTitle(Vsix.Name)]
[assembly: AssemblyDescription(Vsix.Description)]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany(Vsix.Author)]
[assembly: AssemblyProduct(Vsix.Name)]
[assembly: AssemblyCopyright(Vsix.Author)]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]

[assembly: AssemblyVersion(Vsix.Version)]
[assembly: AssemblyFileVersion(Vsix.Version)]

[assembly: InternalsVisibleTo("DocumentHealth.Test")]

namespace System.Runtime.CompilerServices
{
    public class IsExternalInit { }
}