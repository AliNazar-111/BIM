using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Architecture;
using BIMDesigner.UI.Export;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// The drawing set as one PDF, each sheet a bookmark by its number and name, signed with the
/// designer's digital ID - from Windows' certificate store, or a .pfx or .p12 file and its
/// password - for the reason and place given. A PDF reader shows it signed, by whom, and whether
/// anything has changed since. The signature is not drawn on a sheet: a seal or stamp a jurisdiction
/// asks for is put on the title block as a picture.
/// </summary>
internal sealed class SignedSetWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly ComboBox _from = new() { MinWidth = 380 };
    private readonly TextBox _file = new() { MinWidth = 240, Padding = new Thickness(4, 2, 4, 2) };
    private readonly PasswordBox _password = new() { MinWidth = 200, Padding = new Thickness(4, 2, 4, 2) };
    private readonly StackPanel _fromFile = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox _reason = new() { MinWidth = 380, Padding = new Thickness(4, 2, 4, 2) };
    private readonly TextBox _location = new() { MinWidth = 380, Padding = new Thickness(4, 2, 4, 2) };
    private readonly TextBox _contact = new() { MinWidth = 380, Padding = new Thickness(4, 2, 4, 2) };
    private readonly TextBlock _sheets = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly TextBlock _said = new();

    private const string FromFile = "From a file (.pfx or .p12)...";

    private sealed record StoreChoice(X509Certificate2 Certificate)
    {
        public override string ToString() =>
            $"{Certificate.GetNameInfo(X509NameType.SimpleName, false)} - issued by {Certificate.GetNameInfo(X509NameType.SimpleName, true)}, until {Certificate.NotAfter:d MMM yyyy}";
    }

    public SignedSetWindow(MainWindow main)
    {
        _main = main;
        var us = main.Document.ProjectInformation.ClearanceStandard.IsUs();
        DialogLook.Apply(this, "Signed Drawing Set", 700, 470);
        MinWidth = 560;
        MinHeight = 380;

        // The digital IDs Windows keeps for this person, with their private keys; then a file.
        using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
        {
            store.Open(OpenFlags.ReadOnly);
            foreach (var certificate in store.Certificates.Where(certificate => certificate.HasPrivateKey && certificate.NotAfter > DateTime.Now && Signs(certificate)))
                _from.Items.Add(new StoreChoice(certificate));
        }

        _from.Items.Add(FromFile);
        _from.SelectedIndex = 0;
        _from.SelectionChanged += (_, _) => _fromFile.Visibility = Equals(_from.SelectedItem, FromFile) ? Visibility.Visible : Visibility.Collapsed;
        _fromFile.Visibility = Equals(_from.SelectedItem, FromFile) ? Visibility.Visible : Visibility.Collapsed;

        var browse = new Button { Content = "Browse...", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 12, 0) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = "Digital ID", Filter = "Digital IDs (*.pfx;*.p12)|*.pfx;*.p12" };
            if (dialog.ShowDialog(this) == true) _file.Text = dialog.FileName;
        };
        _fromFile.Children.Add(_file);
        _fromFile.Children.Add(browse);
        _fromFile.Children.Add(new TextBlock { Text = "Password", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _fromFile.Children.Add(_password);
        _password.SetResourceReference(BackgroundProperty, "Bg.Field");
        _password.SetResourceReference(ForegroundProperty, "Text");
        _password.SetResourceReference(BorderBrushProperty, "Line");
        _password.SetResourceReference(PasswordBox.CaretBrushProperty, "Text");

        _reason.Text = us ? "Issued for permit" : "Issued for planning";
        _location.Text = main.Document.ProjectInformation.Address.Split('\n', ',').Select(part => part.Trim()).LastOrDefault(part => part.Length > 0) ?? string.Empty;

        var grid = new Grid { Margin = new Thickness(16, 0, 16, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(string label, FrameworkElement field)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            Grid.SetRow(text, grid.RowDefinitions.Count - 1);
            Grid.SetRow(field, grid.RowDefinitions.Count - 1);
            Grid.SetColumn(field, 1);
            field.Margin = new Thickness(0, 3, 0, 3);
            field.HorizontalAlignment = HorizontalAlignment.Left;
            grid.Children.Add(text);
            grid.Children.Add(field);
        }

        Row("Signed with", _from);
        Row(string.Empty, _fromFile);
        Row("Reason", _reason);
        Row("Location", _location);
        Row("Contact", _contact);
        Row("Sheets", _sheets);
        DockPanel.SetDock(grid, Dock.Top);

        var sign = new Button { Content = "Sign and Save...", IsDefault = true };
        sign.Click += (_, _) => SignAndSave();
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();

        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(_said, sign, close));
        root.Children.Add(DialogLook.Intro("The drawing set as one PDF, each sheet a bookmark by its number and name, signed with your digital ID. "
                                           + "A PDF reader shows it signed, by whom, and whether anything has changed since. The signature is not drawn on a sheet: a seal or stamp is put on the title block as a picture."));
        root.Children.Add(grid);
        Content = root;

        var set = main.SheetsOfTheSet();
        _sheets.Text = set.Count == 0 ? "No sheets yet: View → New Sheet." : $"{set.Count}: {string.Join(", ", set.Select(sheet => sheet.Number))}";
    }

    /// <summary>The purposes a digital ID that signs documents is issued for: document signing, Adobe's authentic documents, email, or any.</summary>
    private static readonly HashSet<string> SigningPurposes = new()
    {
        "1.3.6.1.4.1.311.10.3.12", "1.2.840.113583.1.1.5", "1.3.6.1.5.5.7.3.4", "2.5.29.37.0", "1.3.6.1.5.5.7.3.36"
    };

    /// <summary>
    /// Whether a certificate is one to sign documents with: its key allowed to sign, and issued for
    /// signing documents or for anything - not a web server's, nor one a program keeps to log in with.
    /// </summary>
    internal static bool Signs(X509Certificate2 certificate)
    {
        if (certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault() is { } usage &&
            (usage.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0) return false;
        if (certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault() is { } purposes)
            return purposes.EnhancedKeyUsages.Cast<Oid>().Any(purpose => purpose.Value is { } value && SigningPurposes.Contains(value));

        // Issued for nothing in particular: a person's or a firm's, not one a program made for itself under a random id.
        var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
        return !System.Text.RegularExpressions.Regex.IsMatch(name, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
               && !string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The certificate chosen, with its private key - or why there is none.</summary>
    private (X509Certificate2? Certificate, string? Problem) Chosen()
    {
        if (_from.SelectedItem is StoreChoice choice) return (choice.Certificate, null);
        if (!File.Exists(_file.Text)) return (null, "Pick the digital ID's file: a .pfx or .p12.");
        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(_file.Text, _password.Password, X509KeyStorageFlags.EphemeralKeySet);
            return certificate.HasPrivateKey ? (certificate, null) : (null, "That file holds no private key: it cannot sign.");
        }
        catch (CryptographicException error)
        {
            return (null, $"It could not be opened - is the password right? {error.Message}");
        }
    }

    private void SignAndSave()
    {
        var sheets = _main.SheetsOfTheSet();
        if (sheets.Count == 0)
        {
            DialogLook.Warn(_said, "There are no sheets to sign: View → New Sheet.");
            return;
        }

        if (Chosen() is not ({ } certificate, null) chosen)
        {
            DialogLook.Warn(_said, Chosen().Problem ?? "Pick a digital ID.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save the Signed Drawing Set", Filter = "PDF document (*.pdf)|*.pdf", DefaultExt = ".pdf",
            FileName = SheetExport.SuggestedFileName(_main.Document, sheets).Replace(".pdf", " - Signed.pdf")
        };
        if (dialog.ShowDialog(this) != true) return;
        SaveTo(dialog.FileName, chosen.Certificate);
    }

    /// <summary>The set written to a file, signed with a certificate. Internal so the harness and tests can write one.</summary>
    internal bool SaveTo(string path, X509Certificate2 certificate)
    {
        try
        {
            SheetExport.ToPdf(_main.Document, _main.SheetsOfTheSet(), path, new PdfSigner(certificate, _reason.Text.Trim(), _location.Text.Trim(), _contact.Text.Trim()));
            DialogLook.Say(_said, $"Signed by {certificate.GetNameInfo(X509NameType.SimpleName, false)} and saved: {Path.GetFileName(path)}.");
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
        {
            DialogLook.Warn(_said, $"Not signed: {error.Message}");
            return false;
        }
    }
}
