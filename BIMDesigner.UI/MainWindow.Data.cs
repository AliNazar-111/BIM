using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// The project's data and its views of it: phases and phase filters, view filters, design
/// options, the project's own parameters, its classification - and COBie, which hands it over.
/// </summary>
public partial class MainWindow
{
    private static readonly ParameterDefinition ViewPhaseParameter = new("Phase", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Phasing);
    private static readonly ParameterDefinition PhaseFilterParameter = new("Phase Filter", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Phasing);
    private static readonly ParameterDefinition ViewFiltersParameter = new("Filters", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);

    /// <summary>A view's phase, its phase filter, the filters it has, and the option it shows of each set.</summary>
    private IEnumerable<ParameterValue> PhasingParameters(ViewReference view)
    {
        var settings = _document.ViewSettings;
        IUndoableCommand Change(string name, Action change) => new ViewStateCommand(_document, new[] { view }, name, change);

        yield return ParameterValue.BindChoiceCommand(ViewPhaseParameter, () => EnumText.Humanise(settings.PhaseOf(view)),
            text => EnumText.TryParse<DesignPhase>(text, out var phase) ? Change("View Phase", () => settings.SetPhase(view, phase)) : null,
            new[] { DesignPhase.Existing, DesignPhase.New, DesignPhase.Future }.Select(phase => EnumText.Humanise(phase)).ToList());

        var filters = Enum.GetValues<PhaseFilter>().Select(Phasing.Describe).ToList();
        yield return ViewTemplates.Locks(_document, view, ViewTemplateSetting.PhaseFilter)
            ? ParameterValue.ReadOnly(PhaseFilterParameter, () => Phasing.Describe(settings.PhaseFilterOf(view)))
            : ParameterValue.BindChoiceCommand(PhaseFilterParameter, () => Phasing.Describe(settings.PhaseFilterOf(view)),
                text => Phasing.TryParse(text, out var filter) ? Change("Phase Filter", () => settings.SetPhaseFilter(view, filter)) : null, filters);

        yield return ParameterValue.ReadOnly(ViewFiltersParameter, () =>
        {
            var names = settings.FiltersOf(view).Select(use => _document.ParameterFilters.FirstOrDefault(filter => filter.Id == use.FilterId)?.Name).OfType<string>().ToList();
            return names.Count == 0 ? "None - Manage tab, View Filters" : string.Join(", ", names);
        });

        foreach (var set in _document.DesignOptionSets)
        {
            var definition = new ParameterDefinition($"Design Option: {set.Name}", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Phasing);
            string Label(DesignOption option) => option.IsPrimary ? $"{option.Name} (primary)" : option.Name;
            yield return ParameterValue.BindChoiceCommand(definition,
                () => set.Options.FirstOrDefault(option => option.Id == DesignOptions.ShownIn(_document, view, set)) is { } shown ? Label(shown) : string.Empty,
                text => set.Options.FirstOrDefault(option => Label(option) == text) is { } chosen
                    ? Change("Design Option Shown", () => settings.SetOptionShown(view, set.Id, chosen.IsPrimary ? null : chosen.Id))
                    : null,
                set.Options.Select(Label).ToList());
        }
    }

    // ---- view filters --------------------------------------------------------------------------

    private void OnShowViewFilters(object sender, RoutedEventArgs e) => EditViewFilters();

    private bool EditViewFilters(Func<ViewFiltersWindow, bool>? accept = null)
    {
        var view = CurrentViewReference();
        var window = new ViewFiltersWindow(_document, view);
        if (accept is null) window.Owner = this;
        if (!(accept?.Invoke(window) ?? window.ShowDialog() == true)) return false;

        var before = _document.ParameterFilters.Select(filter => filter.Copy()).ToList();
        var after = window.Filters.Select(filter => filter.Copy()).ToList();
        void Put(List<ParameterFilter> filters)
        {
            _document.ParameterFilters.Clear();
            _document.ParameterFilters.AddRange(filters.Select(filter => filter.Copy()));
            _document.ViewSettings.Touch();
        }

        var steps = new List<IUndoableCommand> { new DelegateCommand("View Filters", () => Put(after), () => Put(before)) };
        if (!window.ViewLocked)
        {
            var uses = window.Uses.ToList();
            steps.Add(new ViewStateCommand(_document, new[] { view }, "View Filters", () => _document.ViewSettings.SetFilters(view, uses)));
        }

        _history.Execute(new CompositeCommand("View Filters", steps));
        AfterDataChange();
        return true;
    }

    // ---- design options -------------------------------------------------------------------------

    private void OnShowDesignOptions(object sender, RoutedEventArgs e) => EditDesignOptions();

    private bool EditDesignOptions(Func<DesignOptionsWindow, bool>? accept = null)
    {
        var window = new DesignOptionsWindow(_document);
        if (accept is null) window.Owner = this;
        if (!(accept?.Invoke(window) ?? window.ShowDialog() == true)) return false;

        var before = _document.DesignOptionSets.Select(set => set.Copy()).ToList();
        var after = window.Sets.Select(set => set.Copy()).ToList();
        var kept = after.SelectMany(set => set.Options).Select(option => option.Id).ToHashSet();
        var gone = _document.Elements.Where(element => element.DesignOptionId is { } option && !kept.Contains(option)).ToList();
        void Put(List<DesignOptionSet> sets)
        {
            _document.DesignOptionSets.Clear();
            _document.DesignOptionSets.AddRange(sets.Select(set => set.Copy()));
            _document.ViewSettings.Touch();
        }

        var steps = gone.Select(element => (IUndoableCommand)new DeleteElementCommand(_document, element)).ToList();
        steps.Insert(0, new DelegateCommand("Design Options", () => Put(after), () => Put(before)));
        _history.Execute(new CompositeCommand("Design Options", steps));

        if (window.AcceptSet is { } accepted && _document.DesignOptionSets.FirstOrDefault(set => set.Id == accepted) is { } toAccept)
        {
            _history.Execute(DesignOptions.AcceptPrimary(_document, toAccept));
            StatusHint.Text = $"{toAccept.Name}: its primary is part of the building now, the other options gone.";
        }

        _document.EditingDesignOption = window.Editing is { } editing && _document.DesignOptionSets.Any(set => set.Options.Any(option => option.Id == editing)) ? editing : null;
        if (window.AcceptSet is null)
            StatusHint.Text = _document.EditingDesignOption is null
                ? "New work goes into the main model."
                : $"New work goes into {DesignOptions.Describe(_document, _document.EditingDesignOption)} - Manage tab, Design Options, to go back to the main model.";
        AfterDataChange();
        return true;
    }

    // ---- project parameters and classification ---------------------------------------------

    private void OnShowProjectParameters(object sender, RoutedEventArgs e) => EditProjectParameters();

    private bool EditProjectParameters(Func<ProjectParametersWindow, bool>? accept = null)
    {
        var window = new ProjectParametersWindow(_document);
        if (accept is null) window.Owner = this;
        if (!(accept?.Invoke(window) ?? window.ShowDialog() == true)) return false;

        var before = _document.ProjectParameters.Select(parameter => parameter.Copy()).ToList();
        var after = window.Parameters.Select(parameter => parameter.Copy()).ToList();
        void Put(List<ProjectParameter> parameters)
        {
            _document.ProjectParameters.Clear();
            _document.ProjectParameters.AddRange(parameters.Select(parameter => parameter.Copy()));
        }

        _history.Execute(new DelegateCommand("Project Parameters", () => Put(after), () => Put(before)));
        AfterDataChange();
        return true;
    }

    private void OnShowClassification(object sender, RoutedEventArgs e) => EditClassification();

    private bool EditClassification(Func<ClassificationWindow, bool>? accept = null)
    {
        var window = new ClassificationWindow(_document);
        if (accept is null) window.Owner = this;
        if (!(accept?.Invoke(window) ?? window.ShowDialog() == true)) return false;

        var (before, after) = (_document.Classification.Copy(), window.Table);
        _history.Execute(new DelegateCommand("Classification", () => _document.Classification.CopyFrom(after), () => _document.Classification.CopyFrom(before)));
        AfterDataChange();
        return true;
    }

    /// <summary>The project's data, or how its views show it, changed: everything showing it follows.</summary>
    private void AfterDataChange()
    {
        Plan.RefreshModel();
        RefreshProperties();
        RefreshSchedule();
        RefreshSection();
        SheetSurface.Refresh();
        Refresh3D();
    }

    // ---- COBie -----------------------------------------------------------------------------------

    /// <summary>Who the last COBie this session was from, to start the next from.</summary>
    private static CobieOptions _lastCobie = new() { ContactEmail = string.Empty };

    private void OnExportCobie(object sender, RoutedEventArgs e)
    {
        if (AskCobie() is not { } options) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export COBie",
            Filter = "COBie spreadsheet (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            FileName = $"{Sanitised(_document.ProjectInformation.Name, "Project")} - COBie.xlsx"
        };
        if (dialog.ShowDialog(this) != true) return;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            CobieExport.Save(_document, dialog.FileName, options);
            StatusHint.Text = $"Exported COBie to {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not write the COBie spreadsheet.\n\n{exception.Message}\n\nIf it is open in a spreadsheet, close it and try again.",
                "Export COBie", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>Export gbXML: the building for energy analysis, each enclosed room a space.</summary>
    private void OnExportGbXml(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export gbXML",
            Filter = "gbXML (*.xml;*.gbxml)|*.xml;*.gbxml",
            DefaultExt = ".xml",
            FileName = $"{Sanitised(_document.ProjectInformation.Name, "Project")}.xml"
        };
        if (dialog.ShowDialog(this) != true) return;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var file = GbXmlExport.Build(_document);
            file.Save(dialog.FileName);
            var spaces = file.Descendants(GbXmlExport.Ns + "Space").Count();
            var surfaces = file.Descendants(GbXmlExport.Ns + "Surface").Count();
            StatusHint.Text = spaces == 0
                ? $"Exported {Path.GetFileName(dialog.FileName)} - but no room is enclosed, so it has no spaces: place rooms first."
                : $"Exported gbXML to {Path.GetFileName(dialog.FileName)}: {spaces} space{(spaces == 1 ? string.Empty : "s")} and {surfaces} surfaces, at their centre lines, in metres.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not write the gbXML file.\n\n{exception.Message}", "Export gbXML", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>Who it is from - its Contact row - and the currency, asked for; null if cancelled.</summary>
    private CobieOptions? AskCobie()
    {
        var window = new System.Windows.Window();
        DialogLook.Apply(window, "Export COBie", 520, 380);
        window.SizeToContent = SizeToContent.Height;
        window.Owner = this;
        TextBox Field(string value) => new() { Text = value, Width = 300, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        var email = Field(_lastCobie.ContactEmail);
        var company = Field(string.IsNullOrWhiteSpace(_lastCobie.Company) ? _document.ProjectInformation.Client : _lastCobie.Company);
        var phone = Field(_lastCobie.Phone);
        var currency = Field(string.IsNullOrWhiteSpace(_lastCobie.Currency) ? "GBP" : _lastCobie.Currency);
        var form = new StackPanel { Margin = new Thickness(16, 0, 16, 12) };
        foreach (var (label, box) in new[] { ("Your email - who each row is created by", email), ("Company", company), ("Phone", phone), ("Currency", currency) })
        {
            form.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            form.Children.Add(box);
        }

        var problem = new TextBlock();
        var ok = new Button { Content = "Export", IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (!email.Text.Contains('@')) problem.Text = "COBie names everyone by their email.";
            else window.DialogResult = true;
        };
        var dock = new DockPanel();
        dock.Children.Add(DialogLook.Intro("COBie hands the building over: its floors and spaces, and the doors, windows, fittings and equipment kept up in it, with their types, systems and parameters."));
        dock.Children.Add(DialogLook.Footer(problem, ok, new Button { Content = "Cancel", IsCancel = true }));
        dock.Children.Add(form);
        window.Content = dock;
        if (window.ShowDialog() != true) return null;

        _lastCobie = new CobieOptions { ContactEmail = email.Text.Trim(), Company = company.Text.Trim(), Phone = phone.Text.Trim(), Currency = currency.Text.Trim().ToUpperInvariant() };
        return _lastCobie;
    }
}
