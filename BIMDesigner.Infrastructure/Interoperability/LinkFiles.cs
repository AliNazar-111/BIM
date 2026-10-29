using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Infrastructure.Interoperability;

/// <summary>
/// The files a project links to: a linked model read by its kind - a project by the project reader, an IFC
/// file by the IFC reader - each link's file found again from beside the project where it has moved with it,
/// and each linked drawing read again from its file as the project opens.
/// </summary>
public static class LinkFiles
{
    /// <summary>The kinds of file a model can be linked from.</summary>
    public const string Filter = "Models (*.bimx;*.ifc)|*.bimx;*.ifc|BIMDesigner project (*.bimx)|*.bimx|IFC file (*.ifc)|*.ifc";

    /// <summary>A linked model's file read into a document of its own.</summary>
    public static BimDocument Read(string path) =>
        string.Equals(Path.GetExtension(path), ".ifc", StringComparison.OrdinalIgnoreCase) ? IfcImport.Open(path).Document : ProjectFile.Load(path, refreshLinks: false);

    /// <summary>Each link's path from the folder the project is saved in, so the two can move together.</summary>
    public static void Relate(BimDocument document, string projectPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";
        foreach (var link in document.All<ModelLink>().Where(link => link.Path.Length > 0))
            link.RelativePath = Path.GetRelativePath(folder, link.Path);
    }

    /// <summary>
    /// After a project is opened: each link's file found - where it was, or where it is from the project as it
    /// was from where it was saved - and each linked drawing read again from its file where the file is there.
    /// </summary>
    public static void Refresh(BimDocument document, string projectPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        foreach (var link in document.All<ModelLink>())
            if (ModelLinks.FileOf(link, folder) is { } found) link.Path = found;

        foreach (var underlay in document.All<CadUnderlay>().Where(underlay => underlay.Linked))
        {
            var source = File.Exists(underlay.Source) ? underlay.Source
                : folder is not null && File.Exists(Path.Combine(folder, Path.GetFileName(underlay.Source))) ? Path.Combine(folder, Path.GetFileName(underlay.Source))
                : null;
            if (source is null) continue;
            try
            {
                underlay.Drawing = CadImport.Read(source).Drawing;
                underlay.Source = source;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The copy kept in the project is drawn instead.
            }
        }
    }
}
