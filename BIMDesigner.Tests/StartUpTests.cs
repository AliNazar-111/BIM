using System.IO;
using System.Reflection;
using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Mep;
using BIMDesigner.Infrastructure.Projects;
using BIMDesigner.UI;
using BIMDesigner.UI.StartUp;

namespace BIMDesigner.Tests;

/// <summary>
/// Starting work: new projects made from a template and the New Project steps - the rules, the
/// particulars, the site and a level for each storey; the recent projects, newest first and the
/// pinned above them; the steps checking each answer; and the project they make opened and saved.
/// </summary>
[Collection("Wpf")]
public class StartUpTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static void OnUiThread(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                test();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("Failed on the UI thread.", failure);
    }

    /// <summary>The recent list kept in a folder of its own for the test, and put back after.</summary>
    private static void WithOwnRecentList(Action test)
    {
        var was = RecentProjects.Folder;
        RecentProjects.Folder = Path.Combine(Path.GetTempPath(), "BIMDesigner.Tests", "Recent " + Guid.NewGuid().ToString("N"));
        try
        {
            test();
        }
        finally
        {
            RecentProjects.Folder = was;
        }
    }

    [Fact]
    public void ANewProjectHasItsTemplatesRulesItsParticularsAndALevelForEachStorey()
    {
        // England, three storeys of 2.85 m over a basement, in Manchester 120 m up.
        var uk = ProjectTemplates.Create(new NewProjectSpec
        {
            Name = "  Elm Close ", Number = "P-104", Client = "J. Patel", Address = "4 Elm Close", BuildingType = "Detached house",
            HeatingLocation = HeatingLocation.Manchester, SiteAltitude = 120, Ground = GroundType.StiffClay, Storeys = 3, StoreyHeight = 2850, Basement = true
        });
        var info = uk.ProjectInformation;
        Assert.Equal(("Elm Close", "P-104", "J. Patel", "4 Elm Close", "Detached house"), (info.Name, info.Number, info.Client, info.Address, info.BuildingType));
        Assert.Equal((ClearanceStandard.ApprovedDocumentM, HeatingLocation.Manchester, 120.0, GroundType.StiffClay), (info.ClearanceStandard, info.HeatingLocation, info.SiteAltitude, info.Ground));
        Assert.Equal(new[] { ("Basement", -2850.0), ("Ground Floor", 0), ("First Floor", 2850), ("Second Floor", 5700), ("Roof", 8550) },
            uk.Levels.OrderBy(level => level.Elevation).Select(level => (level.Name, level.Elevation)));
        Assert.NotEmpty(uk.TypesOf<WallType>());

        // A wheelchair user home: M4(3); one storey, the roof's level over it.
        var wheelchair = ProjectTemplates.Create(new NewProjectSpec { Template = ProjectTemplate.EnglandWheelchairHouse, Storeys = 1 });
        Assert.Equal(ClearanceStandard.ApprovedDocumentM3, wheelchair.ProjectInformation.ClearanceStandard);
        Assert.Equal(new[] { "Ground Floor", "Roof" }, wheelchair.Levels.OrderBy(level => level.Elevation).Select(level => level.Name));

        // The US: the IRC, the jurisdiction's conditions, and storeys counted from the first floor.
        var us = ProjectTemplates.Create(new NewProjectSpec { Template = ProjectTemplate.UsHouse, WinterDesignTemperature = 14, GroundSnowLoad = 30, FrostDepth = 762, Storeys = 2 });
        Assert.Equal((ClearanceStandard.Irc, 14.0, 30.0, 762.0), (us.ProjectInformation.ClearanceStandard, us.ProjectInformation.WinterDesignTemperature!.Value, us.ProjectInformation.GroundSnowLoad, us.ProjectInformation.FrostDepth));
        Assert.Equal(new[] { "First Floor", "Second Floor", "Roof" }, us.Levels.OrderBy(level => level.Elevation).Select(level => level.Name));
        Assert.Equal(-6.0, Climate.DesignOutside(us)!.Value + 4, 0);
    }

    [Fact]
    public void RecentProjectsAreNewestFirstThePinnedAboveAndTheirPicturesKept() => WithOwnRecentList(() =>
    {
        var folder = Path.Combine(RecentProjects.Folder, "projects");
        Directory.CreateDirectory(folder);
        string Project(string name)
        {
            var path = Path.Combine(folder, name + ".bimx");
            File.WriteAllText(path, "{}");
            return path;
        }

        var (a, b, c) = (Project("A"), Project("B"), Project("C"));
        RecentProjects.Opened(a, "Alpha");
        Thread.Sleep(5);
        RecentProjects.Opened(b, "Bravo");
        Thread.Sleep(5);
        RecentProjects.Opened(c, "Charlie");
        Assert.Equal(new[] { "Charlie", "Bravo", "Alpha" }, RecentProjects.All().Select(project => project.Name));

        // Pinned, it stays on top; opened again, under its name as it now is.
        RecentProjects.Pin(a, true);
        Thread.Sleep(5);
        RecentProjects.Opened(b, "Bravo Two");
        Assert.Equal(new[] { "Alpha", "Bravo Two", "Charlie" }, RecentProjects.All().Select(project => project.Name));
        Assert.True(RecentProjects.All()[0].Pinned);

        // A picture kept beside it, and both gone when it is taken off the list.
        RecentProjects.KeepThumbnail(c, new byte[] { 1, 2, 3 });
        Assert.NotNull(RecentProjects.All().Single(project => project.Name == "Charlie").Thumbnail);
        RecentProjects.Forget(c);
        Assert.DoesNotContain(RecentProjects.All(), project => project.Name == "Charlie");
        Assert.False(File.Exists(RecentProjects.ThumbnailOf(c)));

        // A file moved away is still listed, said not to be there.
        File.Delete(a);
        Assert.False(RecentProjects.All().Single(project => project.Name == "Alpha").Exists);

        // Only so many kept, the pinned one always.
        for (var i = 0; i < RecentProjects.Most + 5; i++) RecentProjects.Opened(Path.Combine(folder, $"P{i}.bimx"), $"P{i}");
        Assert.Equal(RecentProjects.Most, RecentProjects.All().Count);
        Assert.Contains(RecentProjects.All(), project => project.Name == "Alpha");

        // A list that cannot be read is started again.
        File.WriteAllText(Path.Combine(RecentProjects.Folder, "Recent Projects.json"), "not json");
        Assert.Empty(RecentProjects.All());
    });

    [Fact]
    public void TheNewProjectStepsCheckEachAnswerAndTheProjectTheyMakeOpensSaved() => WithOwnRecentList(() => OnUiThread(() =>
    {
        var folder = Path.Combine(RecentProjects.Folder, "projects");
        var steps = new NewProjectSteps();
        StartChoice? made = null;
        steps.Created += (_, choice) => made = choice;

        // The template, then the project: no name, no going on.
        Assert.True(steps.Next());
        steps.ProjectName.Text = " ";
        Assert.False(steps.Next());
        Assert.Contains("name", steps.Problem);
        steps.ProjectName.Text = "Elm Close";
        Assert.Equal("Elm Close", steps.FileName.Text);
        Assert.True(steps.Next());

        // The site: an altitude that is not one refused.
        steps.Altitude.Text = "high";
        Assert.False(steps.Next());
        steps.Altitude.Text = "65";
        Assert.True(steps.Next());

        // The storeys: a height out of range refused.
        steps.Storeys.SelectedItem = 3;
        steps.StoreyHeight.Text = "900";
        Assert.False(steps.Next());
        steps.StoreyHeight.Text = "2850";
        Assert.True(steps.Next());
        Assert.Equal(4, steps.Step);

        // Kept where asked - not over a project already there.
        steps.Folder.Text = folder;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Elm Close.bimx"), "{}");
        Assert.False(steps.Create());
        Assert.Contains("already", steps.Problem);
        File.Delete(Path.Combine(folder, "Elm Close.bimx"));
        Assert.True(steps.Create());
        Assert.NotNull(made);
        Assert.Equal(Path.Combine(folder, "Elm Close.bimx"), made!.SaveTo);
        Assert.Equal(4, made.Document.Levels.Count);

        // Opened in the project window: saved there, on the recent list, named in the title.
        var window = new MainWindow();
        typeof(MainWindow).GetMethod("Begin", Hidden)!.Invoke(window, new object[] { made });
        Assert.True(File.Exists(made.SaveTo));
        Assert.Equal("Elm Close", RecentProjects.All()[0].Name);
        Assert.StartsWith("Elm Close", window.Title);
        Assert.False(window.HasUnsavedWork);

        // The steps' template switched to the US and back keeps one set of site questions.
        var again = new NewProjectSteps { Template = ProjectTemplate.UsHouse };
        again.Template = ProjectTemplate.EnglandHouse;
        Assert.Equal(ProjectTemplate.EnglandHouse, again.Spec.Template);
    }));

    [Fact]
    public void ANewProjectsToolsStartOnTheUsualTypes() => OnUiThread(() =>
    {
        string Picked(MainWindow window, string picker) =>
            ((System.Windows.Controls.ComboBox)typeof(MainWindow).GetField(picker, Hidden)!.GetValue(window)!).SelectedItem switch
            {
                Core.Elements.ElementType type => type.Name,
                { } item => item.GetType().GetProperty("Type")?.GetValue(item) is Core.Elements.ElementType inner ? inner.Name : item.ToString() ?? string.Empty,
                _ => string.Empty
            };

        // England: a plain single door, a casement window, and furniture to put in rooms.
        var window = new MainWindow();
        typeof(MainWindow).GetMethod("Begin", Hidden)!.Invoke(window, new object[] { new StartChoice(ProjectTemplates.Create(new NewProjectSpec()), null, "new") });
        Assert.Equal("Single - 900 x 2100", Picked(window, "DoorTypePicker"));
        Assert.Equal("Casement - 1200 x 1200", Picked(window, "WindowTypePicker"));
        Assert.Equal(Core.Elements.BuiltInCategory.Furniture, Core.Documents.BimDocument.CreateDefault().TypesOf<ComponentType>().First(type => type.Name == Picked(window, "ComponentTypePicker")).Kind);

        // The US: the inch-sized door and a double-hung window.
        typeof(MainWindow).GetMethod("Begin", Hidden)!.Invoke(window, new object[] { new StartChoice(ProjectTemplates.Create(new NewProjectSpec { Template = ProjectTemplate.UsHouse }), null, "new") });
        Assert.Equal("Exterior Door - 36 x 80 in", Picked(window, "DoorTypePicker"));
        Assert.StartsWith("Double Hung", Picked(window, "WindowTypePicker"));
    });

    [Fact]
    public void TheHomeScreenListsRecentProjectsFindsThemAndOpensWhatIsPicked() => WithOwnRecentList(() => OnUiThread(() =>
    {
        var folder = Path.Combine(RecentProjects.Folder, "projects");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Oak Lane.bimx");
        Infrastructure.Serialization.ProjectFile.Save(ProjectTemplates.Create(new NewProjectSpec { Name = "Oak Lane" }), path);
        RecentProjects.Opened(path, "Oak Lane");
        RecentProjects.Opened(Path.Combine(folder, "Gone.bimx"), "Gone");

        var home = new StartWindow();
        Assert.Equal(new[] { "Gone", "Oak Lane" }, home.Listed.Select(project => project.Name));
        StartChoice? picked = null;
        home.Chosen += (_, choice) => picked = choice;

        // Opened: read here, handed on with where it is.
        Assert.True(home.OpenProject(path, quiet: true));
        Assert.Equal(("Oak Lane", path), (picked!.Document.ProjectInformation.Name, picked.Path));

        // A file that is not a project is said, and nothing handed on.
        File.WriteAllText(Path.Combine(folder, "Broken.bimx"), "not a project");
        picked = null;
        Assert.False(home.OpenProject(Path.Combine(folder, "Broken.bimx"), quiet: true));
        Assert.Null(picked);

        // The example, and an empty project.
        home.OpenExample();
        Assert.True(picked!.Example);
        home.StartEmpty();
        Assert.Equal(3, picked!.Document.Levels.Count);

        // New Project in place of the home screen, and back.
        Assert.NotNull(home.ShowNewProject());
        home.ShowHome();
        Assert.Null(home.Steps);
    }));
}
