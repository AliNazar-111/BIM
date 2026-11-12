using System.IO;
using System.Windows;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// A picture brought under the plan to work over - a map, an aerial photograph, a scanned survey - kept in the project or
/// linked to its file; put where its world file says on the grid the project is set out on, or set to its true size by two
/// points on it a known distance apart; cropped to the part wanted; and its world file written for others to place it by.
/// </summary>
public partial class MainWindow
{
    private const string PictureFilter = "Pictures (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|All files (*.*)|*.*";

    private void OnImportImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import a Picture to Work Over", Filter = PictureFilter };
        if (dialog.ShowDialog(this) == true) ImportImage(dialog.FileName, quiet: false);
    }

    private void OnLinkImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Link a Picture to Work Over", Filter = PictureFilter };
        if (dialog.ShowDialog(this) == true) ImportImage(dialog.FileName, quiet: false, linked: true);
    }

    /// <summary>
    /// A PNG or JPEG brought onto the storey shown - kept in the project, or linked to its file. Where a world file lies beside
    /// it, it goes where that puts it on the grid the project is set out on - the project's survey point set from it, if the
    /// project has none and it would land far off; otherwise its middle at the middle of the view, a pixel 10 mm to start with.
    /// Said on the status bar.
    /// </summary>
    internal ImageUnderlay? ImportImage(string path, bool quiet, bool linked = false)
    {
        if (_document.FindLevel(Plan.ActiveLevelId) is not { } level) return null;
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!quiet) MessageBox.Show(this, $"Could not read {Path.GetFileName(path)}.\n\n{exception.Message}", "Import Image", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        if (ImageUnderlay.From(Path.GetFullPath(path), data, level.Id) is not { } picture)
        {
            if (!quiet) MessageBox.Show(this, $"{Path.GetFileName(path)} is not a PNG or JPEG picture.", "Import Image", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        picture.Linked = linked;
        var steps = new List<IUndoableCommand>();
        string said;
        if (LinkFiles.WorldFileBeside(path) is var (world, worldPath))
        {
            var (placed, problem) = Georeferenced(picture, world, steps);
            said = placed
                ? $"put where {Path.GetFileName(worldPath)} says on the grid, a pixel {Units.FormatLength(picture.Scale)}{(steps.Count > 0 ? $" - the project's survey point set from it, its origin at E {_document.ProjectInformation.SurveyOffset.X / 1000:#,0} N {_document.ProjectInformation.SurveyOffset.Y / 1000:#,0}" : string.Empty)}"
                : $"not put where {Path.GetFileName(worldPath)} says: {problem} Its middle is at the middle of the view, 10 mm a pixel";
            if (!placed) picture.Location = Plan.ViewCentre - new Vector2D(picture.Width / 2, picture.Height / 2);
        }
        else
        {
            picture.Location = Plan.ViewCentre - new Vector2D(picture.Width / 2, picture.Height / 2);
            said = "at 10 mm a pixel to start with. Scale Image sets its true size: click two points on it a known distance apart, and give the distance";
        }

        steps.Add(new AddElementCommand(_document, picture, linked ? "Link Image" : "Import Image"));
        _history.Execute(steps.Count == 1 ? steps[0] : new CompositeCommand(linked ? "Link Image" : "Import Image", steps));
        if (linked) _underlayRead[picture.Id] = File.GetLastWriteTimeUtc(path);
        Plan.Select(picture);
        _linksWindow?.Refresh();
        StatusHint.Text = $"{(linked ? "Linked" : "Imported")} {Path.GetFileName(path)} onto {level.Name}, {picture.PixelWidth:#,0} x {picture.PixelHeight:#,0} pixels, {said}. Pin it, so it is not moved by a click in it.";
        return picture;
    }

    /// <summary>
    /// A picture put where a world file says, in metres on the grid the project is set out on. A project with no survey point
    /// whose picture would land over 10 km off has its survey point set first - the picture's middle, to the metre - as a step
    /// put with the rest. False, and why, where the world file cannot place it.
    /// </summary>
    private (bool Placed, string? Problem) Georeferenced(ImageUnderlay picture, WorldFile world, List<IUndoableCommand> steps)
    {
        var survey = _document.ProjectInformation.SurveyOffset;
        var (placed, problem) = ImageUnderlays.Place(world, picture.PixelWidth, picture.PixelHeight, survey);
        if (placed is not var (location, rotation, scale)) return (false, problem);

        var middle = new Point2D(location.X, location.Y) + new Vector2D(picture.PixelWidth * scale / 2, picture.PixelHeight * scale / 2);
        if (survey.X == 0 && survey.Y == 0 && Math.Max(Math.Abs(middle.X), Math.Abs(middle.Y)) > 10_000_000)
        {
            var information = _document.ProjectInformation;
            var origin = new Vector2D(Math.Round(middle.X / 1000) * 1000, Math.Round(middle.Y / 1000) * 1000);
            var command = new DelegateCommand("Set Survey Point", () => information.SurveyOffset = origin, () => information.SurveyOffset = survey);
            command.Redo();
            steps.Add(command);
            (placed, _) = ImageUnderlays.Place(world, picture.PixelWidth, picture.PixelHeight, origin);
            (location, rotation, scale) = placed!.Value;
        }

        (picture.Location, picture.Rotation, picture.Scale) = (location, rotation, scale);
        return (true, null);
    }

    private void OnScaleImage(object sender, RoutedEventArgs e)
    {
        Plan.ImageScaleLength = null;
        Plan.BeginImageScale();
        Plan.Focus();
    }

    /// <summary>The two points clicked: the distance they are to be apart asked for, the picture scaled to it.</summary>
    private void OnImageScaleMeasured(object? sender, double measured)
    {
        var answer = NamePrompt.Ask(this, "Scale Image", $"How far apart the two points are in truth - they are {Units.FormatLength(measured)} apart on the plan now:", Units.FormatLength(measured));
        if (answer is null)
        {
            Plan.CancelPendingOperation();
            return;
        }

        if (ParameterFormatter.TryParse(ParameterDataType.Length, answer, out var value) && value is double length && length > 0)
        {
            Plan.FinishImageScale(length);
            RefreshProperties();
        }
        else
        {
            StatusHint.Text = $"\"{answer}\" is not a length: the picture is left as it was.";
            Plan.CancelPendingOperation();
        }
    }

    private void OnCropImage(object sender, RoutedEventArgs e)
    {
        Plan.BeginImageCrop();
        Plan.Focus();
    }

    private ImageUnderlay? PickedPicture() =>
        Plan.SelectedElements.OfType<ImageUnderlay>().FirstOrDefault() ?? (_document.All<ImageUnderlay>().Where(picture => picture.LevelId == Plan.ActiveLevelId).Take(2).ToList() is [var only] ? only : null);

    private void OnUncropImage(object sender, RoutedEventArgs e)
    {
        if (PickedPicture() is not { Crop: { } before } picture)
        {
            StatusHint.Text = "Select a cropped picture to show all of it again.";
            return;
        }

        _history.Execute(new DelegateCommand("Uncrop Image", () => picture.Crop = null, () => picture.Crop = before));
        AfterHistoryChange();
        StatusHint.Text = $"All of {Path.GetFileName(picture.Source)} shown again.";
    }

    /// <summary>A picture put where a world file picked says, on the grid the project is set out on: one step to undo.</summary>
    private void OnPlaceImageByWorldFile(object sender, RoutedEventArgs e)
    {
        if (PickedPicture() is not { } picture)
        {
            StatusHint.Text = "Select the picture to place first.";
            return;
        }

        var beside = WorldFile.NamesFor(picture.Source).FirstOrDefault(File.Exists);
        var dialog = new OpenFileDialog
        {
            Title = "Place by World File", Filter = "World files (*.pgw;*.pngw;*.jgw;*.jpgw;*.jpegw;*.wld)|*.pgw;*.pngw;*.jgw;*.jpgw;*.jpegw;*.wld|All files (*.*)|*.*",
            FileName = beside ?? string.Empty, InitialDirectory = Path.GetDirectoryName(beside ?? picture.Source) ?? string.Empty
        };
        if (dialog.ShowDialog(this) == true) PlaceImageByWorldFile(picture, dialog.FileName);
    }

    /// <summary>A picture put where a world file says - internal so tests and the harness can place one. False where it cannot be.</summary>
    internal bool PlaceImageByWorldFile(ImageUnderlay picture, string path)
    {
        WorldFile? world;
        try { world = WorldFile.Parse(File.ReadAllText(path)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusHint.Text = $"{Path.GetFileName(path)} could not be read: {exception.Message}";
            return false;
        }

        if (world is null)
        {
            StatusHint.Text = $"{Path.GetFileName(path)} is not a world file: it holds not six numbers, a line each.";
            return false;
        }

        var before = (picture.Location, picture.Rotation, picture.Scale);
        var steps = new List<IUndoableCommand>();
        var (placed, problem) = Georeferenced(picture, world, steps);
        if (!placed)
        {
            StatusHint.Text = $"{Path.GetFileName(path)} does not place it: {problem}";
            return false;
        }

        var after = (picture.Location, picture.Rotation, picture.Scale);
        steps.Add(new DelegateCommand("Place Image", () => (picture.Location, picture.Rotation, picture.Scale) = after, () => (picture.Location, picture.Rotation, picture.Scale) = before));
        _history.Execute(new CompositeCommand("Place by World File", steps));
        AfterHistoryChange();
        Plan.ZoomToFit();
        StatusHint.Text = $"{Path.GetFileName(picture.Source)} put where {Path.GetFileName(path)} says: a pixel {Units.FormatLength(picture.Scale)}, turned {picture.Rotation:0.##}°.";
        return true;
    }

    private void OnSaveImageWorldFile(object sender, RoutedEventArgs e)
    {
        if (PickedPicture() is not { } picture)
        {
            StatusHint.Text = "Select the picture to write a world file for first.";
            return;
        }

        var name = WorldFile.NamesFor(picture.Source)[0];
        var dialog = new SaveFileDialog
        {
            Title = "Save World File", Filter = "World file|*" + Path.GetExtension(name) + "|All files (*.*)|*.*",
            FileName = Path.GetFileName(name), InitialDirectory = Path.GetDirectoryName(picture.Source) ?? string.Empty
        };
        if (dialog.ShowDialog(this) == true) SaveImageWorldFile(picture, dialog.FileName);
    }

    /// <summary>A picture's world file written where it lies on the grid the project is set out on - internal for tests and the harness.</summary>
    internal bool SaveImageWorldFile(ImageUnderlay picture, string path)
    {
        try
        {
            File.WriteAllText(path, ImageUnderlays.For(picture, _document.ProjectInformation.SurveyOffset).ToString());
            StatusHint.Text = $"Saved {Path.GetFileName(path)}: beside {Path.GetFileName(picture.Source)}, a GIS or another BIM program puts the picture where it lies here, in metres on the project's grid.";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusHint.Text = $"{Path.GetFileName(path)} could not be written: {exception.Message}";
            return false;
        }
    }

    /// <summary>A linked picture read again from its file - or from another, which it is then linked to - as one step to undo.</summary>
    internal bool ReloadImage(ImageUnderlay picture, string? from = null)
    {
        var source = Path.GetFullPath(from ?? picture.Source);
        if (!File.Exists(source) || LinkFiles.ReadPicture(source) is not { } data || Core.Sheets.SheetImage.SizeOf(data) is null)
        {
            StatusHint.Text = $"{Path.GetFileName(source)} is not where it was, or is not a PNG or JPEG: {source}";
            return false;
        }

        var before = (picture.Data, picture.PixelWidth, picture.PixelHeight, picture.Crop, picture.Source);
        var copy = new ImageUnderlay { PixelWidth = picture.PixelWidth, PixelHeight = picture.PixelHeight, Crop = picture.Crop };
        ImageUnderlays.Reread(copy, data);
        var after = (copy.Data, copy.PixelWidth, copy.PixelHeight, copy.Crop, source);
        _history.Execute(new DelegateCommand($"Reload {Path.GetFileName(source)}",
            () => (picture.Data, picture.PixelWidth, picture.PixelHeight, picture.Crop, picture.Source) = after,
            () => (picture.Data, picture.PixelWidth, picture.PixelHeight, picture.Crop, picture.Source) = before));
        _underlayRead[picture.Id] = File.GetLastWriteTimeUtc(source);
        AfterHistoryChange();
        _linksWindow?.Refresh();
        StatusHint.Text = $"Reloaded {Path.GetFileName(source)}: {picture.PixelWidth:#,0} x {picture.PixelHeight:#,0} pixels.";
        return true;
    }
}
