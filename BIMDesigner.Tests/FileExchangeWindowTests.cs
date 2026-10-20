using System.IO;
using System.Reflection;
using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.UI;
using Microsoft.Win32;

namespace BIMDesigner.Tests;

/// <summary>Files to and from other programs, through the window: IFC opened as a project, and .bimx files tied to the app.</summary>
[Collection("Wpf")]
public class FileExchangeWindowTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ProjectFilesAreRegisteredToOpenWithTheApp()
    {
        var root = $@"Software\BIMDesignerTests\{Guid.NewGuid():N}";
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(root);
            const string executable = @"C:\Program Files\BIMDesigner\BIMDesigner.exe";
            Assert.False(FileAssociations.IsRegisteredTo(executable, classes));

            FileAssociations.Register(executable, classes);
            Assert.True(FileAssociations.IsRegisteredTo(executable, classes));
            Assert.Equal(FileAssociations.ProjectProgId, classes.OpenSubKey(".bimx")!.GetValue(string.Empty));
            Assert.Equal($"\"{executable}\" \"%1\"", classes.OpenSubKey($@"{FileAssociations.ProjectProgId}\shell\open\command")!.GetValue(string.Empty));

            // IFC files are offered to it, not taken over: their own default is left alone.
            Assert.Contains(FileAssociations.IfcProgId, classes.OpenSubKey(@".ifc\OpenWithProgids")!.GetValueNames());
            Assert.Null(classes.OpenSubKey(".ifc")!.GetValue(string.Empty));
            Assert.False(FileAssociations.IsRegisteredTo(@"C:\elsewhere\BIMDesigner.exe", classes));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }
    }

    [Fact]
    public void AnIfcFileOpensAsANewUnsavedProject()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var path = Path.Combine(Path.GetTempPath(), $"house-{Guid.NewGuid():N}.ifc");
                IfcExport.Save(IfcImportTests.House(), path);
                try
                {
                    var window = new MainWindow();
                    typeof(MainWindow).GetMethods(Hidden).First(m => m.Name == "LoadDocument").Invoke(window, new object?[] { BimDocument.CreateDefault(), null, false });
                    var result = (IfcImportResult?)typeof(MainWindow).GetMethod("ImportIfc", Hidden)!.Invoke(window, new object[] { path, true });
                    Assert.NotNull(result);
                    var document = (BimDocument)typeof(MainWindow).GetField("_document", Hidden)!.GetValue(window)!;
                    Assert.Same(result!.Document, document);
                    Assert.Equal(6, document.Walls.Count());
                    Assert.Null(typeof(MainWindow).GetField("_path", Hidden)!.GetValue(window));
                    Assert.Single(document.Elements.OfType<Door>());
                }
                finally { File.Delete(path); }
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The window failed on the UI thread.", failure);
    }
}
