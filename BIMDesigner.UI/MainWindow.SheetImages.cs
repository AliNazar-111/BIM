using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Sheets;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>Pictures on sheets: put there from a file or from a rendering, moved, made wider or narrower, taken off.</summary>
public partial class MainWindow
{
    /// <summary>Sheet → Picture...: a picture from a file, put on the sheet open.</summary>
    private void OnPlaceSheetImage(object sender, RoutedEventArgs e)
    {
        if (SheetSurface.Sheet is not { } sheet)
        {
            StatusHint.Text = "Open or create a sheet first.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Put a picture on the sheet",
            Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var data = File.ReadAllBytes(dialog.FileName);
            if (PlaceImageOnSheet(PictureBytes(data), Path.GetFileName(dialog.FileName), sheet) is null)
                StatusHint.Text = $"{Path.GetFileName(dialog.FileName)} is not a picture that can be read.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            MessageBox.Show(this, $"The picture could not be read:\n\n{error.Message}", "Picture", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>A file's picture as a sheet keeps it: a PNG or a JPEG as it is, anything else read and written again as a PNG.</summary>
    private static byte[] PictureBytes(byte[] data)
    {
        if (SheetImage.KindOf(data) is not null) return data;
        using var input = new MemoryStream(data);
        var frame = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return Png(frame, int.MaxValue);
    }

    /// <summary>
    /// Puts a picture - a PNG or a JPEG - on a sheet: the one given, or the one open, or the first; a new one
    /// for presentation where the project has none. It is made as wide as fits the free space well, put in
    /// the next free space, selected, and the sheet shown. Null where the bytes are no picture.
    /// </summary>
    internal SheetImage? PlaceImageOnSheet(byte[] data, string name, Sheet? on = null)
    {
        if (SheetImage.From(name, data) is not { } image) return null;
        var sheet = on ?? SheetSurface.Sheet ?? _document.All<Sheet>().OrderBy(each => each.Number, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (sheet is null)
        {
            sheet = new Sheet { Number = Sheets.NextNumber(_document), Name = "Presentation", DrawnBy = Environment.UserName };
            _history.Execute(new AddElementCommand(_document, sheet, $"Add Sheet {sheet.Number}"));
            RefreshProjectBrowser();
        }

        // As wide as half the drawing area, or as tall as most of it - whichever it reaches first.
        var area = sheet.DrawingArea;
        var aspect = (double)image.PixelHeight / image.PixelWidth;
        image.Width = Math.Round(Math.Min(area.Width * 0.5, (area.Height - Viewport.TitleBand) * 0.8 / aspect));
        image.Centre = sheet.NextFreePosition(_document, image.Width, image.Height, image.ShowTitle, null, image);
        _history.Execute(new AddSheetImageCommand(sheet, image, $"Place {image.TitleShown} on Sheet"));

        ShowSheet(sheet);
        SheetSurface.SelectImage(image);
        SheetSurface.Refresh();
        RefreshViewportControls();
        StatusHint.Text = $"{image.TitleShown} placed on {sheet.Number}, {image.Width:0} mm wide. Drag it to move it; its width is on the bar.";
        return image;
    }

    private void OnSheetImageWidthKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnSheetImageWidthChanged(sender, e);
        e.Handled = true;
    }

    /// <summary>The selected picture made as wide as typed, mm, its height following - and moved where it no longer fits.</summary>
    private void OnSheetImageWidthChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSheets || SheetSurface.SelectedImage is not { } image || SheetSurface.Sheet is not { } sheet) return;
        var text = SheetImageWidthBox.Text.Trim().Replace("mm", string.Empty).Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var width) || width < 10 || width > Math.Max(sheet.Width, sheet.Height))
        {
            RefreshViewportControls();
            return;
        }

        if (Math.Abs(width - image.Width) < 0.05) return;
        var before = SetSheetImageCommand.StateOf(image);
        image.Width = width;
        image.Centre = sheet.PositionAfterResize(_document, image);
        _history.Record(new SetSheetImageCommand(image, before, "Resize Image on Sheet"));
        SheetSurface.Refresh();
        RefreshViewportControls();
        StatusHint.Text = sheet.IsWellPlaced(_document, image)
            ? $"{image.TitleShown} is {image.Width:0} x {image.Height:0} mm."
            : $"At {image.Width:0} mm {image.TitleShown} is too big for the free space. Try it narrower.";
    }

    /// <summary>The selected picture taken off its sheet.</summary>
    private void RemoveSheetImage()
    {
        if (SheetSurface.Sheet is not { } sheet || SheetSurface.SelectedImage is not { } image) return;
        _history.Execute(new RemoveSheetImageCommand(sheet, image));
        SheetSurface.SelectImage(null);
        SheetSurface.Refresh();
        RefreshViewportControls();
    }
}
