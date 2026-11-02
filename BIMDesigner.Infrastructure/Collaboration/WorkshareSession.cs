using System.Text.Json.Nodes;
using BIMDesigner.Core.Documents;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Infrastructure.Collaboration;

/// <summary>What a synchronisation or a reload did: what went in, what came back, and what clashed - said.</summary>
public sealed record SyncResult(int Sent, int Received, IReadOnlyList<string> Conflicts, int Version, string Said);

/// <summary>
/// One person's work on a shared project: their local copy of the central model, the issue of the
/// central it was last brought up to, and what they have borrowed. Every edit is checked: what it
/// changed has to be theirs already, or nobody's and up to date - then it is borrowed - or the edit
/// is refused and who has it said. Synchronising sends what they have changed to the central,
/// brings back everyone else's, and lets go of what they borrowed; reloading brings back the others'
/// and keeps their own. A person is known by their user name.
/// </summary>
public sealed class WorkshareSession
{
    private Dictionary<Guid, OwnedItem> _prints;

    private WorkshareSession(BimDocument document, string localPath, string user)
    {
        Document = document;
        LocalPath = localPath;
        User = user;
        _prints = ItemPrints.Take(document);
    }

    public BimDocument Document { get; private set; }

    public string LocalPath { get; }

    public string User { get; }

    public string CentralPath => Document.Worksharing.CentralPath;

    /// <summary>The central's issue as it was when this copy was last brought up to it.</summary>
    private string BasePath => LocalPath + ".base";

    // ---- starting ---------------------------------------------------------------------

    /// <summary>
    /// A project made shared: its worksets made - its own, the levels and grids', the standards' and
    /// views' - its gridlines put on the levels and grids', and it saved as the central model, issue 1.
    /// </summary>
    public static void Enable(BimDocument document, string centralPath)
    {
        centralPath = Path.GetFullPath(centralPath);
        document.Worksharing.Start();
        document.Worksharing.CentralPath = centralPath;
        document.Worksharing.IsCentral = true;
        document.Worksharing.BaseVersion = 1;
        foreach (var grid in document.All<BIMDesigner.Core.Datums.Grid>()) grid.Workset = Worksharing.LevelsAndGrids;
        ProjectFile.Save(document, centralPath);

        using var gate = ShareRegistry.Lock(centralPath);
        var registry = ShareRegistry.Read(centralPath);
        registry.Version = 1;
        registry.History.Add(new SyncRecord { Version = 1, User = Environment.UserName, When = DateTime.Now, Comment = "Made the central model" });
        registry.Write(centralPath);
    }

    /// <summary>The folder local copies go in where none is given: the person's own, under the app's.</summary>
    public static string DefaultLocalFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMDesigner", "Local Copies");

    /// <summary>
    /// A local copy of a central model made for a person - "(central)_(user).bimx" in their folder - from the
    /// central as it now is, and opened. One already there is opened as it is, to go on with.
    /// </summary>
    public static WorkshareSession CreateLocal(string centralPath, string user, string? folder = null)
    {
        centralPath = Path.GetFullPath(centralPath);
        folder ??= DefaultLocalFolder;
        Directory.CreateDirectory(folder);
        var local = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(centralPath)}_{Safe(user)}.bimx");
        if (File.Exists(local) && File.Exists(local + ".base")) return Open(local, user);

        string central;
        int version;
        using (ShareRegistry.Lock(centralPath))
        {
            central = File.ReadAllText(centralPath);
            version = ShareRegistry.Read(centralPath).Version;
        }

        var document = ProjectFile.FromJson(central);
        document.Worksharing.IsCentral = false;
        document.Worksharing.CentralPath = centralPath;
        document.Worksharing.BaseVersion = version;
        File.WriteAllText(local + ".base", central);
        ProjectFile.Save(document, local);
        return new WorkshareSession(document, local, user);
    }

    /// <summary>A person's local copy opened, to go on with.</summary>
    public static WorkshareSession Open(string localPath, string user) =>
        new(ProjectFile.Load(localPath), Path.GetFullPath(localPath), user);

    /// <summary>A person's local copy already open, taken on.</summary>
    public static WorkshareSession Attach(BimDocument document, string localPath, string user) => new(document, Path.GetFullPath(localPath), user);

    private static string Safe(string name) => new(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

    // ---- each edit ----------------------------------------------------------------------

    /// <summary>
    /// After an edit: what it changed borrowed - or, where any of it is someone else's or has been
    /// changed in the central since this copy was brought up to it, nothing borrowed and why, for the
    /// edit to be taken back.
    /// </summary>
    public string? Borrow()
    {
        var now = ItemPrints.Take(Document);
        var changed = ItemPrints.Changed(_prints, now);
        if (changed.Count == 0)
        {
            _prints = now;
            return null;
        }

        using (ShareRegistry.Lock(CentralPath))
        {
            var registry = ShareRegistry.Read(CentralPath);
            var theirs = changed.Select(item => (Item: item, Owner: registry.OwnerOf(item.Id, item.Workset)))
                .Where(entry => entry.Owner is not null && entry.Owner != User).ToList();
            if (theirs.Count > 0)
                return $"{Capital(theirs[0].Item.Name)} is {theirs[0].Owner}'s{(theirs.Count > 1 ? $", and {theirs.Count - 1} more of what this changes" : string.Empty)}: ask for it - Collaborate, Editing Requests - or leave it.";

            var stale = changed.Where(item => registry.Versions.TryGetValue(item.Id, out var version) && version > Document.Worksharing.BaseVersion).ToList();
            if (stale.Count > 0)
                return $"{Capital(stale[0].Name)} has been changed in the central model since this copy was brought up to it{(stale.Count > 1 ? $", with {stale.Count - 1} more" : string.Empty)}: Reload Latest first.";

            foreach (var item in changed)
                if (registry.OwnerOf(item.Id, item.Workset) is null) registry.Owners[item.Id] = User;
            registry.Write(CentralPath);
        }

        _prints = now;
        return null;
    }

    /// <summary>The prints taken again as the model now is - after an undo or redo, which only takes back what was this person's.</summary>
    public void Rebaseline() => _prints = ItemPrints.Take(Document);

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // ---- synchronising ----------------------------------------------------------------------

    /// <summary>The project as saved, without what belongs to the file rather than the project: when it was saved, and whose copy it is.</summary>
    private static JsonObject Project(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove("savedUtc");
        node.Remove("worksharing");
        return node;
    }

    /// <summary>
    /// What this person has changed sent to the central model, everyone else's brought back, and - asked
    /// - what they borrowed let go; their copy then the central as it now is, saved. One clash - something
    /// nobody owns changed both here and there - keeps the central's, and is said.
    /// </summary>
    public SyncResult Synchronize(string comment, bool relinquish)
    {
        using var gate = ShareRegistry.Lock(CentralPath);
        var registry = ShareRegistry.Read(CentralPath);
        var centralJson = File.ReadAllText(CentralPath);
        var baseJson = File.Exists(BasePath) ? File.ReadAllText(BasePath) : centralJson;

        // What is mine to send: what has changed here since the issue this copy came from.
        var mine = ItemPrints.Changed(ItemPrints.Take(ProjectFile.FromJson(baseJson)), ItemPrints.Take(Document));
        var theirs = ItemPrints.Changed(ItemPrints.Take(ProjectFile.FromJson(baseJson)), ItemPrints.Take(ProjectFile.FromJson(centralJson)));

        var central = JsonNode.Parse(centralJson)!.AsObject();
        var local = Project(ProjectFile.ToJson(Document));
        var sending = !JsonNode.DeepEquals(Project(baseJson), local);
        var merge = JsonMerge.Merge(Project(baseJson), local, Project(centralJson));
        var merged = merge.Merged!.AsObject();
        merged["savedUtc"] = DateTime.UtcNow;
        merged["worksharing"] = central["worksharing"]?.DeepClone();

        var version = registry.Version + (sending ? 1 : 0);
        var mergedJson = merged.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        if (sending)
        {
            var temporary = CentralPath + ".tmp";
            File.WriteAllText(temporary, mergedJson);
            File.Move(temporary, CentralPath, overwrite: true);
            foreach (var item in mine) registry.Versions[item.Id] = version;
            registry.Version = version;
            registry.History.Add(new SyncRecord { Version = version, User = User, When = DateTime.Now, Comment = comment.Trim(), Changed = mine.Count });
        }

        var released = relinquish ? registry.Relinquish(User) : 0;
        registry.Write(CentralPath);

        TakeUp(mergedJson, mergedJson, version);
        var said = !sending && theirs.Count == 0
            ? "Synchronised: nothing to send, nothing new in the central model."
            : $"Synchronised with the central model, issue {version}: {Count(mine.Count, "change")} sent, {Count(theirs.Count, "change")} of others' brought in"
              + (released > 0 ? $", {Count(released, "thing")} let go" : string.Empty)
              + (merge.Conflicts.Count > 0 ? $". Both here and in the central, {Count(merge.Conflicts.Count, "change")} the central's kept: {string.Join(", ", merge.Conflicts.Take(3))}" : string.Empty) + ".";
        return new SyncResult(mine.Count, theirs.Count, merge.Conflicts, version, said);
    }

    /// <summary>Everyone else's changes brought back from the central model, this person's own kept, nothing sent; the copy saved.</summary>
    public SyncResult ReloadLatest()
    {
        string centralJson;
        int version;
        using (ShareRegistry.Lock(CentralPath))
        {
            centralJson = File.ReadAllText(CentralPath);
            version = ShareRegistry.Read(CentralPath).Version;
        }

        var baseJson = File.Exists(BasePath) ? File.ReadAllText(BasePath) : centralJson;
        var theirs = ItemPrints.Changed(ItemPrints.Take(ProjectFile.FromJson(baseJson)), ItemPrints.Take(ProjectFile.FromJson(centralJson)));
        var merge = JsonMerge.Merge(Project(baseJson), Project(ProjectFile.ToJson(Document)), Project(centralJson));
        var merged = merge.Merged!.AsObject();
        merged["worksharing"] = JsonNode.Parse(centralJson)!["worksharing"]?.DeepClone();

        TakeUp(merged.ToJsonString(), centralJson, version);
        return new SyncResult(0, theirs.Count, merge.Conflicts, version, theirs.Count == 0
            ? $"Up to date with the central model, issue {version}."
            : $"Brought up to the central model, issue {version}: {Count(theirs.Count, "change")} of others' brought in, this copy's own kept.");
    }

    /// <summary>This copy made what has been merged, its base the central as it was, and saved.</summary>
    private void TakeUp(string projectJson, string baseJson, int version)
    {
        var document = ProjectFile.FromJson(projectJson);
        document.Worksharing.IsCentral = false;
        document.Worksharing.CentralPath = CentralPath;
        document.Worksharing.BaseVersion = version;
        File.WriteAllText(BasePath, baseJson);
        ProjectFile.Save(document, LocalPath);
        Document = document;
        _prints = ItemPrints.Take(document);
    }

    private static string Count(int count, string what) => $"{count} {what}{(count == 1 ? string.Empty : "s")}";

    /// <summary>Whether the central model has been written since this copy was brought up to it.</summary>
    public bool CentralIsNewer => ShareRegistry.Read(CentralPath).Version > Document.Worksharing.BaseVersion;

    /// <summary>The central's registry as it now is: who has what, its issues, the requests.</summary>
    public ShareRegistry Registry() => ShareRegistry.Read(CentralPath);

    // ---- ownership ------------------------------------------------------------------------

    /// <summary>Everything this person has let go, without synchronising - refused where they have changed any of it since.</summary>
    public string Relinquish()
    {
        var baseDocument = File.Exists(BasePath) ? ProjectFile.FromJson(File.ReadAllText(BasePath)) : Document;
        var changed = ItemPrints.Changed(ItemPrints.Take(baseDocument), ItemPrints.Take(Document));
        using var gate = ShareRegistry.Lock(CentralPath);
        var registry = ShareRegistry.Read(CentralPath);
        var mine = changed.Where(item => registry.OwnerOf(item.Id, item.Workset) == User).ToList();
        if (mine.Count > 0)
            return $"{Count(mine.Count, "thing")} borrowed here {(mine.Count == 1 ? "has" : "have")} been changed - {mine[0].Name} - synchronise to send {(mine.Count == 1 ? "it" : "them")}, then let go.";
        var released = registry.Relinquish(User);
        registry.Write(CentralPath);
        return released == 0 ? "Nothing borrowed to let go." : $"{Count(released, "thing")} let go: others can change them now.";
    }

    /// <summary>A workset made this person's own, so everything on it is theirs to change; refused where someone else has it, or anything on it.</summary>
    public string MakeEditable(string workset)
    {
        using var gate = ShareRegistry.Lock(CentralPath);
        var registry = ShareRegistry.Read(CentralPath);
        if (registry.WorksetOwners.TryGetValue(workset, out var owner))
            return owner == User ? $"{workset} is yours already." : $"{workset} is {owner}'s.";
        var on = Document.Elements.Where(element => string.Equals(element.Workset, workset, StringComparison.OrdinalIgnoreCase)).Select(element => element.Id).ToHashSet();
        var others = registry.Owners.Where(entry => entry.Value != User && on.Contains(entry.Key)).Select(entry => entry.Value).Distinct().ToList();
        if (others.Count > 0) return $"{string.Join(" and ", others)} {(others.Count == 1 ? "has" : "have")} borrowed some of {workset}: it cannot be made yours till they let go.";
        registry.WorksetOwners[workset] = User;
        registry.Write(CentralPath);
        return $"{workset} is yours: everything on it is yours to change until you let it go.";
    }

    // ---- requests -----------------------------------------------------------------------

    /// <summary>A request to whoever has something: for an element, a type, a level - or a whole workset.</summary>
    public string Request(Guid item, string workset, string name)
    {
        using var gate = ShareRegistry.Lock(CentralPath);
        var registry = ShareRegistry.Read(CentralPath);
        var owner = item == Guid.Empty ? registry.WorksetOwners.GetValueOrDefault(workset) : registry.OwnerOf(item, workset);
        if (owner is null || owner == User) return $"{Capital(name)} is nobody else's: change it.";
        registry.Requests.Add(new EditRequest { Item = item, Workset = workset, Name = name, From = User, To = owner, When = DateTime.Now });
        registry.Write(CentralPath);
        return $"Asked {owner} for {name}.";
    }

    /// <summary>The requests made of this person still waiting, and the answers to theirs not yet seen.</summary>
    public IReadOnlyList<EditRequest> Requests() =>
        Registry().Requests.Where(request => (request.To == User && request.Answer.Length == 0) || (request.From == User && request.Answer.Length > 0)).ToList();

    /// <summary>
    /// A request answered: granted, what was asked for goes to whoever asked - refused where it has
    /// been changed here and not yet sent; denied, it stays. Either way they are told.
    /// </summary>
    public string Answer(Guid request, bool grant)
    {
        var baseDocument = File.Exists(BasePath) ? ProjectFile.FromJson(File.ReadAllText(BasePath)) : Document;
        var changed = ItemPrints.Changed(ItemPrints.Take(baseDocument), ItemPrints.Take(Document)).Select(item => item.Id).ToHashSet();
        using var gate = ShareRegistry.Lock(CentralPath);
        var registry = ShareRegistry.Read(CentralPath);
        if (registry.Requests.FirstOrDefault(each => each.Id == request) is not { } asked) return "That request has gone.";
        if (grant)
        {
            var touched = asked.Item != Guid.Empty
                ? changed.Contains(asked.Item)
                : Document.Elements.Any(element => changed.Contains(element.Id) && string.Equals(element.Workset, asked.Workset, StringComparison.OrdinalIgnoreCase));
            if (touched)
                return $"{Capital(asked.Name)} has been changed here: synchronise first, then grant it.";
            if (asked.Item == Guid.Empty) registry.WorksetOwners[asked.Workset] = asked.From;
            else registry.Owners[asked.Item] = asked.From;
        }

        asked.Answer = grant ? "Granted" : "Denied";
        registry.Write(CentralPath);
        return grant ? $"{Capital(asked.Name)} is {asked.From}'s now." : $"Told {asked.From} no.";
    }

    /// <summary>An answer seen, taken off the list.</summary>
    public void Seen(Guid request)
    {
        using var gate = ShareRegistry.Lock(CentralPath);
        var registry = ShareRegistry.Read(CentralPath);
        registry.Requests.RemoveAll(each => each.Id == request && each.From == User && each.Answer.Length > 0);
        registry.Write(CentralPath);
    }
}
