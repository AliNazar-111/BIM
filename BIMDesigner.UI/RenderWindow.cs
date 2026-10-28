using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Rendering;
using BIMDesigner.UI.Controls;
using BIMDesigner.UI.Export;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// A rendering of the 3D view as it looks: lit by the sun where it is at the place, date and time
/// set - or an overcast sky - its shadows cast, the sky and everything round about lighting what the
/// sun does not reach, glass seen through and reflecting, each face in its material's pattern. Made
/// pass by pass, a rough picture at once and smoother with each; saved as PNG or copied.
/// </summary>
internal sealed class RenderWindow : System.Windows.Window
{
    private static readonly (int Width, int Height)[] Sizes = { (1280, 720), (1600, 900), (1920, 1080), (2560, 1440), (3840, 2160), (1200, 1200), (1600, 1200) };

    private readonly MainWindow _main;
    private readonly ModelView _view;
    private readonly DatePicker _date = new() { Width = 120, SelectedDateFormat = DatePickerFormat.Short };
    private readonly TextBox _time = new() { Width = 64 };
    private readonly ComboBox _sky = new() { Width = 100, ItemsSource = new[] { "Clear sky", "Overcast" } };
    private readonly ComboBox _quality = new() { Width = 100, ItemsSource = Enum.GetValues<RenderQuality>().Select(quality => EnumText.Humanise(quality)).ToList() };
    private readonly ComboBox _size = new() { Width = 110, ItemsSource = Sizes.Select(size => $"{size.Width} x {size.Height}").ToList() };
    private readonly TextBox _exposure = new() { Width = 44 };
    private readonly TextBox _latitude = new() { Width = 64 };
    private readonly TextBox _longitude = new() { Width = 64 };
    private readonly TextBox _offset = new() { Width = 44 };
    private readonly ComboBox _summer = new() { Width = 130, ItemsSource = new[] { "No summer time", "European", "North American" } };
    private readonly TextBlock _sun = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8, Margin = new Thickness(6, 0, 0, 0) };
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _problem = new();
    private readonly Button _render = new() { Content = "Render", IsDefault = true };
    private readonly Button _stop = new() { Content = "Stop", IsEnabled = false };
    private readonly Button _save = new() { Content = "Save PNG...", IsEnabled = false };
    private readonly Button _copy = new() { Content = "Copy", IsEnabled = false };
    private CancellationTokenSource? _cancel;
    private WriteableBitmap? _bitmap;
    private bool _filling;

    public RenderWindow(MainWindow main, ModelView view)
    {
        (_main, _view) = (main, view);
        DialogLook.Apply(this, "Render", 1100, 820);
        MinWidth = 760;
        MinHeight = 560;

        var when = Bar("Sun:");
        Pair(when, "Date", _date, "The day the sun is worked out for");
        Pair(when, "Time", _time, "The time on the clocks there, 24-hour: 14:30");
        Pair(when, "Sky", _sky, "A clear sky with the sun and its shadows, or an overcast one without");
        when.Children.Add(_sun);

        var where = Bar("Place:");
        Pair(where, "Latitude", _latitude, "Degrees north; south is less than 0");
        Pair(where, "Longitude", _longitude, "Degrees east; west is less than 0");
        Pair(where, "UTC", _offset, "Its standard time's hours ahead of Greenwich: 0 in the UK, -5 in New York");
        Pair(where, "Clocks", _summer, "When the clocks go forward an hour for the summer");

        var picture = Bar("Picture:");
        Pair(picture, "Size", _size, "The picture's size in pixels; the 3D view's width of view is kept");
        Pair(picture, "Quality", _quality, "How many paths of light each pixel is worked out from: 4, 24, 96 or 384 - smoother, and slower");
        Pair(picture, "Exposure", _exposure, "Brighter or darker, in stops: +1 twice as bright");

        _date.SelectedDateChanged += (_, _) => Change(settings => { if (_date.SelectedDate is { } date) settings.Date = DateOnly.FromDateTime(date); });
        _sky.SelectionChanged += (_, _) => Change(settings => settings.Sky = (RenderSky)Math.Max(0, _sky.SelectedIndex));
        _quality.SelectionChanged += (_, _) => Change(settings => settings.Quality = (RenderQuality)Math.Max(0, _quality.SelectedIndex));
        _size.SelectionChanged += (_, _) => Change(settings => { if (_size.SelectedIndex >= 0) (settings.Width, settings.Height) = Sizes[_size.SelectedIndex]; });
        _summer.SelectionChanged += (_, _) => Change(settings => settings.Summer = (SummerTime)Math.Max(0, _summer.SelectedIndex));
        foreach (var box in new[] { _time, _exposure, _latitude, _longitude, _offset })
        {
            box.LostFocus += (_, _) => Typed();
            box.KeyDown += (_, args) =>
            {
                if (args.Key != Key.Enter) return;
                Typed();
                args.Handled = true;
            };
        }

        _render.Click += (_, _) => Render();
        _stop.Click += (_, _) => _cancel?.Cancel();
        _save.Click += (_, _) => Save();
        _copy.Click += (_, _) =>
        {
            if (_bitmap is null) return;
            Clipboard.SetImage(_bitmap);
            DialogLook.Say(_problem, "Copied.");
        };
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();

        var top = new StackPanel { Margin = new Thickness(12, 8, 12, 4) };
        top.Children.Add(when);
        top.Children.Add(where);
        top.Children.Add(picture);
        DockPanel.SetDock(top, Dock.Top);

        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(_problem, _render, _stop, _save, _copy, close));
        root.Children.Add(top);
        root.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x26)), Margin = new Thickness(12, 4, 12, 8), Child = _image });
        Content = root;

        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) =>
        {
            _cancel?.Cancel();
            _main.History.Changed -= OnHistoryChanged;
        };
        Fill();
        DialogLook.Say(_problem, "Render makes a picture of the 3D view as it looks now.");
    }

    private Core.Documents.BimDocument Document => _main.Document;

    private void OnHistoryChanged(object? sender, EventArgs e) => Fill();

    private static WrapPanel Bar(string title)
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        bar.Children.Add(new TextBlock { Text = title, Width = 64, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 });
        return bar;
    }

    private static void Pair(Panel bar, string label, FrameworkElement control, string tip)
    {
        var pair = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 16, 3), ToolTip = tip };
        pair.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Opacity = 0.85 });
        pair.Children.Add(control);
        bar.Children.Add(pair);
    }

    /// <summary>The settings in the boxes, the place as set or the country's, and where the sun is then.</summary>
    private void Fill()
    {
        var settings = Document.ProjectInformation.Render;
        var (latitude, longitude, offset, summer) = settings.Place(Document);
        _filling = true;
        _date.SelectedDate = settings.Date.ToDateTime(TimeOnly.MinValue);
        _time.Text = settings.Time.ToString("HH:mm", CultureInfo.InvariantCulture);
        _sky.SelectedIndex = (int)settings.Sky;
        _quality.SelectedIndex = (int)settings.Quality;
        var size = Array.IndexOf(Sizes, (settings.Width, settings.Height));
        _size.SelectedIndex = size;
        if (size < 0) _size.Text = $"{settings.Width} x {settings.Height}";
        _exposure.Text = settings.Exposure.ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture);
        _latitude.Text = latitude.ToString("0.###", CultureInfo.CurrentCulture);
        _longitude.Text = longitude.ToString("0.###", CultureInfo.CurrentCulture);
        _offset.Text = offset.ToString("+0.##;-0.##;0", CultureInfo.CurrentCulture);
        _summer.SelectedIndex = (int)summer;
        _filling = false;

        var (altitude, azimuth) = settings.Sun(Document);
        _sun.Text = settings.Sky == RenderSky.Overcast ? "an overcast sky"
            : altitude <= 0 ? $"the sun is down ({altitude:0}°)"
            : $"the sun {altitude:0}° up, {azimuth:0}° from north{(SunPosition.IsSummerTime(settings.Date, summer) ? ", clocks forward" : string.Empty)}";
    }

    private void Change(Action<RenderSettings> edit)
    {
        if (_filling) return;
        var settings = Document.ProjectInformation.Render;
        var after = settings.Copy();
        edit(after);
        if (after.SameAs(settings)) return;
        _main.History.Execute(new SetRenderSettingsCommand(settings, after));
    }

    /// <summary>The typed boxes read, as one change; anything that does not read put back.</summary>
    private void Typed()
    {
        if (_filling) return;
        double? Number(TextBox box, double least, double most) =>
            double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && value >= least && value <= most ? value : null;
        Change(settings =>
        {
            if (TimeOnly.TryParse(_time.Text.Trim(), CultureInfo.InvariantCulture, out var time) || TimeOnly.TryParse(_time.Text.Trim(), CultureInfo.CurrentCulture, out time))
                settings.Time = time;
            if (Number(_exposure, -5, 5) is { } exposure) settings.Exposure = exposure;
            var (latitude, longitude, offset, _) = settings.Place(Document);
            if (Number(_latitude, -90, 90) is { } lat && Math.Abs(lat - latitude) > 1e-9) settings.Latitude = lat;
            if (Number(_longitude, -180, 180) is { } lon && Math.Abs(lon - longitude) > 1e-9) settings.Longitude = lon;
            if (Number(_offset, -12, 14) is { } utc && Math.Abs(utc - offset) > 1e-9) settings.UtcOffset = utc;
        });
        Fill();
    }

    /// <summary>The 3D view rendered: built into a scene, then pass by pass, each shown as it finishes.</summary>
    private async void Render()
    {
        // Each pass comes back here to be shown, however the rendering was started.
        if (SynchronizationContext.Current is not System.Windows.Threading.DispatcherSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));

        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource();
        var settings = Document.ProjectInformation.Render.Copy();
        var meshes = _view.ShownMeshes();
        if (meshes.Count == 0)
        {
            DialogLook.Warn(_problem, "The 3D view shows nothing to render.");
            return;
        }

        var camera = _view.CurrentCamera();
        var ground = _view.GroundShown;
        var materials = SurfacePatterns.ByName(Document);
        var light = RenderLight.For(Document, settings);
        var total = RenderSettings.Samples(settings.Quality);
        (_render.IsEnabled, _stop.IsEnabled) = (false, true);
        var clock = Stopwatch.StartNew();
        DialogLook.Say(_problem, $"Gathering {meshes.Count:#,0} pieces of the model...");
        try
        {
            var scene = await Task.Run(() => RenderScene.Build(meshes, materials, ground), cancel.Token);
            var job = new RenderJob(scene, camera, light, settings.Width, settings.Height, settings.Exposure);
            _bitmap = new WriteableBitmap(job.Width, job.Height, 96, 96, PixelFormats.Bgra32, null);
            _image.Source = _bitmap;

            // A path a pixel first, then twice as many each pass, up to the quality's.
            for (var step = 1; job.Samples < total; step = Math.Min(step * 2, 32))
            {
                var samples = Math.Min(step, total - job.Samples);
                var lastShown = Stopwatch.StartNew();
                void Row(int done)
                {
                    if (lastShown.ElapsedMilliseconds < 250) return;
                    lastShown.Restart();
                    Dispatcher.BeginInvoke(() => DialogLook.Say(_problem, $"{scene.TriangleCount:#,0} triangles; {job.Samples + samples} paths a pixel - {100 * done / job.Height}%"));
                }

                await Task.Run(() => job.Pass(samples, cancel.Token, Row), cancel.Token);
                var pixels = job.Bgra();
                _bitmap.WritePixels(new Int32Rect(0, 0, job.Width, job.Height), pixels, job.Width * 4, 0);
                (_save.IsEnabled, _copy.IsEnabled) = (true, true);
                DialogLook.Say(_problem, $"{scene.TriangleCount:#,0} triangles; {job.Samples} of {total} paths a pixel, {clock.Elapsed.TotalSeconds:0.0} s");
            }

            DialogLook.Say(_problem, $"Rendered {job.Width} x {job.Height} at {job.Samples} paths a pixel from {scene.TriangleCount:#,0} triangles in {clock.Elapsed.TotalSeconds:0.0} s.");
        }
        catch (OperationCanceledException)
        {
            if (IsLoaded) DialogLook.Say(_problem, "Stopped: the picture as far as it got.");
        }
        catch (Exception exception)
        {
            DialogLook.Warn(_problem, $"It could not be rendered: {exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(_cancel, cancel)) (_render.IsEnabled, _stop.IsEnabled) = (true, false);
        }
    }

    private void Save()
    {
        if (_bitmap is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Save the Rendering",
            Filter = "PNG image (*.png)|*.png",
            FileName = CadExport.SuggestedFileName(Document, "rendering", ".png")
        };
        if (dialog.ShowDialog(this) != true) return;
        SaveTo(dialog.FileName);
    }

    /// <summary>The picture as it stands written to a PNG file.</summary>
    internal void SaveTo(string path)
    {
        if (_bitmap is null) return;
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
            DialogLook.Say(_problem, $"Saved {Path.GetFileName(path)}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DialogLook.Warn(_problem, $"It could not be saved: {exception.Message}");
        }
    }

    /// <summary>Whether a rendering is under way.</summary>
    internal bool Rendering => _stop.IsEnabled;

    /// <summary>Starts a rendering, as the Render button does.</summary>
    internal void Start() => Render();
}
