using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// .bimx files opened with this copy of BIMDesigner by a double-click, for the user signed in:
/// the extension, its program id with its icon and its open command - and BIMDesigner offered
/// under Open With for an IFC file, without taking IFC files over from whatever opens them now.
/// The installer registers the same for everyone; this is for a copy run where it was built.
/// </summary>
public static class FileAssociations
{
    public const string ProjectProgId = "BIMDesigner.Project";
    public const string IfcProgId = "BIMDesigner.Ifc";

    /// <summary>The user's own file classes, where their associations are written.</summary>
    public static RegistryKey UserClasses() => Registry.CurrentUser.CreateSubKey(@"Software\Classes");

    /// <summary>Writes the associations under a classes key - the user's own, or one a test makes.</summary>
    public static void Register(string executable, RegistryKey classes)
    {
        using (var extension = classes.CreateSubKey(".bimx"))
        {
            extension.SetValue(string.Empty, ProjectProgId);
            extension.SetValue("Content Type", "application/x-bimdesigner-project");
            using var openWith = extension.CreateSubKey("OpenWithProgids");
            openWith.SetValue(ProjectProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (var project = classes.CreateSubKey(ProjectProgId))
        {
            project.SetValue(string.Empty, "BIMDesigner Project");
            using (var icon = project.CreateSubKey("DefaultIcon")) icon.SetValue(string.Empty, $"\"{executable}\",0");
            using var command = project.CreateSubKey(@"shell\open\command");
            command.SetValue(string.Empty, $"\"{executable}\" \"%1\"");
        }

        using (var ifc = classes.CreateSubKey(@".ifc\OpenWithProgids")) ifc.SetValue(IfcProgId, Array.Empty<byte>(), RegistryValueKind.None);
        using (var program = classes.CreateSubKey(IfcProgId))
        {
            program.SetValue(string.Empty, "IFC Model (BIMDesigner)");
            using (var icon = program.CreateSubKey("DefaultIcon")) icon.SetValue(string.Empty, $"\"{executable}\",0");
            using var command = program.CreateSubKey(@"shell\open\command");
            command.SetValue(string.Empty, $"\"{executable}\" \"%1\"");
        }
    }

    /// <summary>Whether .bimx files open with this executable now, under a classes key.</summary>
    public static bool IsRegisteredTo(string executable, RegistryKey classes)
    {
        using var extension = classes.OpenSubKey(".bimx");
        using var command = classes.OpenSubKey($@"{ProjectProgId}\shell\open\command");
        return extension?.GetValue(string.Empty) as string == ProjectProgId
               && (command?.GetValue(string.Empty) as string)?.StartsWith($"\"{executable}\"", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>For the user signed in, and Explorer told, so the icons change straight away.</summary>
    public static void RegisterForUser(string executable)
    {
        using var classes = UserClasses();
        Register(executable, classes);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
