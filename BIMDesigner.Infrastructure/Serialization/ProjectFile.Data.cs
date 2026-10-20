using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Infrastructure.Serialization;

/// <summary>
/// What a project keeps about its data and its views beyond its elements: each element's phases
/// and design option, view filters and design option sets, each view's phase, phase filter,
/// filters and options shown, the project's own parameters and their values, and its classification.
/// </summary>
public static partial class ProjectFile
{
    private static FilterOverrideDto FilterUseDto(FilterOverride use) => new()
    {
        FilterId = use.FilterId, Visible = use.Visible, Colour = use.Colour?.ToString(), Halftone = use.Halftone
    };

    private static FilterOverride FilterUse(FilterOverrideDto saved) =>
        new(saved.FilterId, saved.Visible, string.IsNullOrWhiteSpace(saved.Colour) ? null : ParseColour(saved.Colour, new ColourRgb(0, 0, 0)), saved.Halftone);

    private static void WriteRound6(BimDocument document, ProjectFileDto dto)
    {
        foreach (var element in document.Elements.Where(element => element.PhaseCreated != DesignPhase.New || element.PhaseDemolished is not null))
            dto.ElementPhases.Add(new ElementPhaseDto { ElementId = element.Id, Created = element.PhaseCreated.ToString(), Demolished = element.PhaseDemolished?.ToString() });

        dto.ParameterFilters = document.ParameterFilters.Select(filter => new ParameterFilterDto
        {
            Id = filter.Id, Name = filter.Name, MatchAny = filter.MatchAny,
            Categories = filter.Categories.Select(category => category.ToString()).OrderBy(name => name, StringComparer.Ordinal).ToList(),
            Rules = filter.Rules.Select(rule => new ScheduleFilterDto { Field = rule.Field, Comparison = rule.Comparison.ToString(), Value = rule.Value }).ToList()
        }).ToList();

        dto.DesignOptionSets = document.DesignOptionSets.Select(set => new DesignOptionSetDto
        {
            Id = set.Id, Name = set.Name,
            Options = set.Options.Select(option => new DesignOptionDto { Id = option.Id, Name = option.Name, IsPrimary = option.IsPrimary }).ToList()
        }).ToList();
        dto.DesignOptionMembers = document.Elements.Where(element => element.DesignOptionId is not null)
            .Select(element => new OptionMemberDto { ElementId = element.Id, OptionId = element.DesignOptionId!.Value }).ToList();

        // The views' phases, filters and options, on the entries the views already have - or new ones.
        ViewSettingsDto Entry(ViewReference view)
        {
            var entry = dto.ViewSettings.FirstOrDefault(saved => saved.Kind == view.Kind.ToString() && saved.TargetId == view.TargetId && saved.Index == view.Index);
            if (entry is null)
            {
                entry = new ViewSettingsDto { Kind = view.Kind.ToString(), TargetId = view.TargetId, Index = view.Index };
                dto.ViewSettings.Add(entry);
            }

            return entry;
        }

        var settings = document.ViewSettings;
        foreach (var (view, phase) in settings.Phased) Entry(view).Phase = phase.ToString();
        foreach (var (view, filter) in settings.PhaseFiltered) Entry(view).PhaseFilter = filter.ToString();
        foreach (var (view, uses) in settings.WithFilters) Entry(view).Filters = uses.Select(FilterUseDto).ToList();
        foreach (var (view, set, option) in settings.OptionsShown)
        {
            var entry = Entry(view);
            entry.Options ??= new List<OptionChoiceDto>();
            entry.Options.Add(new OptionChoiceDto { Set = set, Option = option });
        }

        dto.ProjectParameters = document.ProjectParameters.Select(parameter => new ProjectParameterDto
        {
            Id = parameter.Id, Name = parameter.Name, DataType = parameter.DataType.ToString(), Binding = parameter.Binding.ToString(), Group = parameter.Group.ToString(),
            Categories = parameter.Categories.Select(category => category.ToString()).OrderBy(name => name, StringComparer.Ordinal).ToList(),
            Shared = parameter.Shared, SharedGroup = NullIfEmpty(parameter.SharedGroup), Description = NullIfEmpty(parameter.Description)
        }).ToList();

        // Only values of parameters the project still has: one deleted takes its values with it.
        var known = document.ProjectParameters.Select(parameter => parameter.Id).ToHashSet();
        var owners = document.Elements.Select(element => (element.Id, element.ParameterValues))
            .Concat(document.ElementTypes.OrderBy(type => type.Id).Select(type => (type.Id, type.ParameterValues)));
        foreach (var (owner, values) in owners)
            foreach (var (parameter, value) in values.Where(entry => known.Contains(entry.Key)).OrderBy(entry => entry.Key))
                if (Value(owner, parameter, value) is { } saved) dto.ParameterValues.Add(saved);

        dto.Classification = new ClassificationDto
        {
            System = document.Classification.System, Edition = NullIfEmpty(document.Classification.Edition),
            Entries = document.Classification.Entries.Select(entry => new ClassificationEntryDto { Code = entry.Code, Title = entry.Title }).ToList()
        };
    }

    private static ParameterValueDto? Value(Guid owner, Guid parameter, object? value) => value switch
    {
        null => null,
        string text => new ParameterValueDto { OwnerId = owner, ParameterId = parameter, Kind = "Text", Text = text },
        bool flag => new ParameterValueDto { OwnerId = owner, ParameterId = parameter, Kind = "Flag", Flag = flag },
        int whole => new ParameterValueDto { OwnerId = owner, ParameterId = parameter, Kind = "Integer", Number = whole },
        decimal money => new ParameterValueDto { OwnerId = owner, ParameterId = parameter, Kind = "Money", Number = (double)money },
        double number => new ParameterValueDto { OwnerId = owner, ParameterId = parameter, Kind = "Number", Number = number },
        _ => new ParameterValueDto { OwnerId = owner, ParameterId = parameter, Kind = "Text", Text = value.ToString() }
    };

    private static void ReadRound6(ProjectFileDto dto, BimDocument document)
    {
        foreach (var saved in dto.ElementPhases ?? new List<ElementPhaseDto>())
            if (document.FindElement(saved.ElementId) is { } element)
            {
                element.PhaseCreated = ParseEnum(saved.Created, DesignPhase.New);
                element.PhaseDemolished = saved.Demolished is { } gone && Enum.TryParse<DesignPhase>(gone, out var phase) ? phase : null;
            }

        static IEnumerable<BuiltInCategory> Categories(IEnumerable<string>? names) =>
            (names ?? Enumerable.Empty<string>()).Select(name => Enum.TryParse<BuiltInCategory>(name, out var category) ? category : (BuiltInCategory?)null)
                .OfType<BuiltInCategory>();

        foreach (var saved in dto.ParameterFilters ?? new List<ParameterFilterDto>())
        {
            var filter = new ParameterFilter { Id = saved.Id, Name = saved.Name ?? "Filter", MatchAny = saved.MatchAny };
            filter.Categories.UnionWith(Categories(saved.Categories));
            filter.Rules.AddRange((saved.Rules ?? new List<ScheduleFilterDto>()).Select(rule =>
                new ScheduleFilter(rule.Field ?? string.Empty, ParseEnum(rule.Comparison, ScheduleOperator.Equals), rule.Value ?? string.Empty)));
            document.ParameterFilters.Add(filter);
        }

        foreach (var saved in dto.DesignOptionSets ?? new List<DesignOptionSetDto>())
        {
            var set = new DesignOptionSet { Id = saved.Id, Name = saved.Name ?? "Option Set" };
            set.Options.AddRange((saved.Options ?? new List<DesignOptionDto>()).Select(option => new DesignOption { Id = option.Id, Name = option.Name ?? "Option", IsPrimary = option.IsPrimary }));
            if (set.Options.Count > 0 && !set.Options.Any(option => option.IsPrimary)) set.Options[0].IsPrimary = true;
            document.DesignOptionSets.Add(set);
        }

        foreach (var member in dto.DesignOptionMembers ?? new List<OptionMemberDto>())
            if (document.FindElement(member.ElementId) is { } element) element.DesignOptionId = member.OptionId;

        foreach (var saved in dto.ViewSettings)
        {
            var view = new ViewReference(ParseEnum(saved.Kind, ViewKind.FloorPlan), saved.TargetId, string.Empty) { Index = saved.Index };
            if (saved.Phase is { } phase && Enum.TryParse<DesignPhase>(phase, out var designPhase)) document.ViewSettings.SetPhase(view, designPhase);
            if (saved.PhaseFilter is { } filter && Enum.TryParse<PhaseFilter>(filter, out var phaseFilter)) document.ViewSettings.SetPhaseFilter(view, phaseFilter);
            if (saved.Filters is { Count: > 0 } filters) document.ViewSettings.SetFilters(view, filters.Select(FilterUse));
            foreach (var choice in saved.Options ?? new List<OptionChoiceDto>()) document.ViewSettings.SetOptionShown(view, choice.Set, choice.Option);
        }

        foreach (var saved in dto.ProjectParameters ?? new List<ProjectParameterDto>())
        {
            var parameter = new ProjectParameter
            {
                Id = saved.Id, Name = string.IsNullOrWhiteSpace(saved.Name) ? "Parameter" : saved.Name,
                DataType = ParseEnum(saved.DataType, ParameterDataType.Text), Binding = ParseEnum(saved.Binding, ParameterBinding.Instance),
                Group = ParseEnum(saved.Group, ParameterGroup.IdentityData), Shared = saved.Shared,
                SharedGroup = saved.SharedGroup ?? string.Empty, Description = saved.Description ?? string.Empty
            };
            parameter.Categories.UnionWith(Categories(saved.Categories));
            document.ProjectParameters.Add(parameter);
        }

        foreach (var saved in dto.ParameterValues ?? new List<ParameterValueDto>())
        {
            var values = document.FindElement(saved.OwnerId)?.ParameterValues
                         ?? document.ElementTypes.FirstOrDefault(type => type.Id == saved.OwnerId)?.ParameterValues;
            if (values is null) continue;
            values[saved.ParameterId] = saved.Kind switch
            {
                "Flag" => saved.Flag ?? false,
                "Integer" => (int)Math.Round(saved.Number ?? 0),
                "Money" => (decimal)(saved.Number ?? 0),
                "Number" => saved.Number ?? 0,
                _ => saved.Text ?? string.Empty
            };
        }

        // A file from before the classification was kept opens with the starter set.
        if (dto.Classification is null) document.Classification.CopyFrom(ClassificationTable.Starter());
        else
        {
            document.Classification.System = dto.Classification.System ?? "Uniclass 2015";
            document.Classification.Edition = dto.Classification.Edition ?? string.Empty;
            document.Classification.Entries.AddRange((dto.Classification.Entries ?? new List<ClassificationEntryDto>())
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Code)).Select(entry => new ClassificationEntry(entry.Code, entry.Title ?? string.Empty)));
        }
    }
}
