using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.UI;

/// <summary>
/// Coordination: clash detection, the construction programme (4D) and the cost plan (5D) - each a
/// window of its own beside the model, which they show clashes in, play the programme through
/// and select from.
/// </summary>
public partial class MainWindow : ICoordinationHost
{
    private ClashDetectiveWindow? _clashWindow;
    private ConstructionScheduleWindow? _programmeWindow;
    private CostPlanWindow? _costWindow;

    BimDocument ICoordinationHost.Document => _document;

    IReadOnlyList<Element> ICoordinationHost.Selected => Plan.SelectedElements;

    /// <summary>Raised when the coordination may have changed: every step done or undone, and a project opened.</summary>
    public event EventHandler? CoordinationChanged;

    private void RaiseCoordinationChanged() => CoordinationChanged?.Invoke(this, EventArgs.Empty);

    void ICoordinationHost.Record(IUndoableCommand command)
    {
        _history.Record(command);
        if (Model3D.ConstructionDate is not null) Refresh3D();
    }

    private static T Open<T>(ref T? window, Func<T> make, System.Windows.Window owner) where T : System.Windows.Window
    {
        if (window is { IsLoaded: true })
        {
            window.Activate();
            return window;
        }

        var made = make();
        if (owner.IsLoaded) made.Owner = owner;
        made.Show();
        window = made;
        return made;
    }

    private void OnClashDetective(object sender, RoutedEventArgs e) => Open(ref _clashWindow, () => new ClashDetectiveWindow(this), this);

    private void OnRunClashTests(object sender, RoutedEventArgs e)
    {
        var window = Open(ref _clashWindow, () => new ClashDetectiveWindow(this), this);
        window.Run(all: true);
    }

    private void OnConstructionProgramme(object sender, RoutedEventArgs e) => Open(ref _programmeWindow, () => new ConstructionScheduleWindow(this), this);

    private void OnCostPlan(object sender, RoutedEventArgs e) => Open(ref _costWindow, () => new CostPlanWindow(this), this);

    void ICoordinationHost.ShowClash(Clash clash)
    {
        var elements = new[] { clash.ElementA, clash.ElementB }.Select(id => _document.FindElement(id)).OfType<Element>().ToList();
        if (elements.Count > 0) Plan.SelectMany(elements);
        if (ModelPanel.Visibility != Visibility.Visible) Show3D();
        Model3D.SetSelection(elements.Select(element => element.Id));
        Model3D.LookAt(clash.Point, 5000);
        RefreshProperties();
        StatusHint.Text = $"{clash.Name}: {clash.Description}";
    }

    byte[]? ICoordinationHost.Snapshot(Clash clash)
    {
        try
        {
            ((ICoordinationHost)this).ShowClash(clash);
            Model3D.UpdateLayout();
            var picture = Model3D.RenderFrame();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(picture));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    IssueViewpoint ICoordinationHost.CaptureViewpoint()
    {
        if (ModelPanel.Visibility != Visibility.Visible || Model3D.Elevation is not null || _shownWalkthrough is not null) Show3D();
        Model3D.UpdateLayout();
        var aspect = Model3D.ActualHeight > 0 ? Model3D.ActualWidth / Model3D.ActualHeight : 16.0 / 9;
        byte[]? picture = null;
        try
        {
            picture = Png(Model3D.RenderFrame(), 1500);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // Not drawn yet: the viewpoint goes without its picture.
        }

        return Issues.ViewOf(Model3D.CurrentCamera(), aspect, Plan.SelectedElements.Select(element => element.Id), Model3D.View.ActiveBox, picture);
    }

    void ICoordinationHost.ShowViewpoint(IssueViewpoint viewpoint)
    {
        if (ModelPanel.Visibility != Visibility.Visible || Model3D.Elevation is not null || _shownWalkthrough is not null) Show3D();
        var elements = Issues.Selected(_document, viewpoint);
        var ids = elements.Select(element => element.Id).ToList();
        if (viewpoint.Camera is { } camera)
        {
            // Looking at what it is about, where it is about anything; ten metres ahead where not.
            var distance = Model3D.BoxAround(ids) is { } around
                ? Math.Max(1000, Math.Sqrt(Math.Pow((around.Min.X + around.Max.X) / 2 - camera.Eye.X, 2) + Math.Pow((around.Min.Y + around.Max.Y) / 2 - camera.Eye.Y, 2) + Math.Pow((around.Min.Z + around.Max.Z) / 2 - camera.Eye.Z, 2)))
                : 10000;
            var shown = camera.ToCamera(distance);
            var box = viewpoint.Box();
            ChangeView(view =>
            {
                view.Camera = shown;
                view.Perspective = shown.Perspective;
                if (box is { } cut) view.SectionBox = cut;
                view.SectionBoxOn = box is not null;
            }, "Show Issue");
        }

        Plan.SelectMany(elements);
        Model3D.SetSelection(ids);
        RefreshProperties();
        StatusHint.Text = viewpoint.Selected.Count == 0 ? "The issue's view." : $"The issue's view: {elements.Count} of the {viewpoint.Selected.Count} elements it is about are in the model.";
    }

    /// <summary>A picture as a PNG, made smaller where it is bigger than BCF wants.</summary>
    internal static byte[] Png(BitmapSource picture, int longest)
    {
        var scale = Math.Min(1.0, longest / (double)Math.Max(1, Math.Max(picture.PixelWidth, picture.PixelHeight)));
        BitmapSource sized = scale < 1 ? new TransformedBitmap(picture, new System.Windows.Media.ScaleTransform(scale, scale)) : picture;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(sized));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private IssuesWindow? _issuesWindow;

    private void OnIssues(object sender, RoutedEventArgs e) => Open(ref _issuesWindow, () => new IssuesWindow(this), this);

    /// <summary>The Issues window, opened or brought forward.</summary>
    internal IssuesWindow OpenIssues() => Open(ref _issuesWindow, () => new IssuesWindow(this), this);

    void ICoordinationHost.Select(IEnumerable<Element> elements)
    {
        var list = elements.ToList();
        Plan.SelectMany(list);
        Model3D.SetSelection(list.Select(element => element.Id));
        RefreshProperties();
    }

    void ICoordinationHost.ShowConstruction(DateOnly? date, bool actual, int lookAheadDays)
    {
        if (date is not null && ModelPanel.Visibility != Visibility.Visible) Show3D();
        Model3D.ConstructionActual = actual;
        Model3D.LookAheadDays = lookAheadDays;
        Model3D.ConstructionDate = date;
        if (date is { } shown)
            StatusHint.Text = $"The building on {shown:dddd d MMMM yyyy}{(actual ? ", as it happened" : string.Empty)}: green going up, red coming down, yellow temporary{(lookAheadDays > 0 ? ", orange starting soon" : string.Empty)}{(actual ? ", orange late" : string.Empty)}.";
    }

    /// <summary>The programme played through in the 3D view, a frame a day, saved as a video.</summary>
    internal async Task SaveConstructionVideo(string path, IReadOnlyList<DateOnly> days, bool actual, int lookAheadDays)
    {
        if (days.Count == 0) return;
        if (ModelPanel.Visibility != Visibility.Visible) Show3D();
        try
        {
            await using var file = File.Create(path);
            BitmapSource? first = null;
            Infrastructure.Interoperability.AviWriter? video = null;
            for (var i = 0; i < days.Count; i++)
            {
                ((ICoordinationHost)this).ShowConstruction(days[i], actual, lookAheadDays);
                Model3D.UpdateLayout();
                var picture = Model3D.RenderFrame();
                first ??= picture;
                video ??= new Infrastructure.Interoperability.AviWriter(file, first.PixelWidth, first.PixelHeight, 5);
                var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
                encoder.Frames.Add(BitmapFrame.Create(picture));
                using var jpeg = new MemoryStream();
                encoder.Save(jpeg);
                video.Add(jpeg.ToArray());
                StatusHint.Text = $"Saving the construction video: day {i + 1} of {days.Count}.";
                await Dispatcher.Yield(DispatcherPriority.Background);
            }

            video?.Finish();
            StatusHint.Text = $"Construction video saved: {days.Count} days at five a second, {days.Count / 5.0:0.#} seconds.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusHint.Text = exception.Message;
        }
    }
}
