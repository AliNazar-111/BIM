using System.IO;
using System.Windows;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// A picture brought under the plan to work over - a map, an aerial photograph, a scanned survey - and set to its true size
/// by two points on it a known distance apart.
/// </summary>
public partial class MainWindow
{
    private void OnImportImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a Picture to Work Over",
            Filter = "Pictures (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true) ImportImage(dialog.FileName, quiet: false);
    }

    /// <summary>
    /// A PNG or JPEG brought onto the storey shown, its middle at the middle of the view, a pixel 10 mm to start with - said on
    /// the status bar, with how to set it to its true size.
    /// </summary>
    internal ImageUnderlay? ImportImage(string path, bool quiet)
    {
        if (_document.FindLevel(Plan.ActiveLevelId) is not { } level) return null;
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!quiet) MessageBox.Show(this, $"Could not read {Path.GetFileName(path)}.\n\n{exception.Message}", "Import Image", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        if (ImageUnderlay.From(path, data, level.Id) is not { } picture)
        {
            if (!quiet) MessageBox.Show(this, $"{Path.GetFileName(path)} is not a PNG or JPEG picture.", "Import Image", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        picture.Location = Plan.ViewCentre - new Vector2D(picture.Width / 2, picture.Height / 2);
        _history.Execute(new AddElementCommand(_document, picture, "Import Image"));
        Plan.Select(picture);
        StatusHint.Text = $"Imported {Path.GetFileName(path)} onto {level.Name}, {picture.PixelWidth:#,0} x {picture.PixelHeight:#,0} pixels at 10 mm a pixel to start with. " +
                          "Scale Image sets its true size: click two points on it a known distance apart, and give the distance. Pin it, so it is not moved by a click in it.";
        return picture;
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
}
