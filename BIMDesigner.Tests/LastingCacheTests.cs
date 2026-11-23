using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.UI.ViewModels;

namespace BIMDesigner.Tests;

/// <summary>
/// Speed in a big building (round 122): a cache that lasts while the model does not change, the history saying when a step is being
/// applied, and a Properties row that reads its value once.
/// </summary>
public class LastingCacheTests
{
    [Fact]
    public void ALastingCacheKeepsWhatItWorkedOutUntilTheModelChanges()
    {
        var document = BimDocument.CreateDefault();
        var lasting = ModelCache.Lasting(document);
        Assert.Null(ModelCache.Of(document));
        var worked = 0;
        int Ask()
        {
            using var _ = ModelCache.Use(lasting);
            return ModelCache.Of(document)!.Keep("probe", document, null, null, () => ++worked);
        }

        // Asked twice, in two uses: worked out once.
        Assert.Equal(1, Ask());
        Assert.Equal(1, Ask());
        Assert.Equal(1, worked);
        Assert.Null(ModelCache.Of(document));

        // An element added: worked out again.
        document.Add(new Room { LevelId = document.Levels[0].Id, Location = new Point2D(0, 0), Name = "Office" });
        Assert.Equal(2, Ask());
        Assert.True(lasting.Holds(document));
        lasting.Dispose();
    }

    private sealed class Probe(UndoStack history, List<bool> seen) : IUndoableCommand
    {
        public string Name => "Probe";
        public void Redo() => seen.Add(history.Applying);
        public void Undo() => seen.Add(history.Applying);
    }

    [Fact]
    public void TheHistorySaysWhenAStepIsBeingApplied()
    {
        var history = new UndoStack();
        var seen = new List<bool>();
        Assert.False(history.Applying);
        history.Execute(new Probe(history, seen));
        history.Undo();
        history.Redo();
        Assert.Equal(new[] { true, true, true }, seen);
        Assert.False(history.Applying);
    }

    [Fact]
    public void ARowReadsItsValueOnceUntilItIsWritten()
    {
        var reads = 0;
        var value = "Rw 38 dB";
        var definition = new ParameterDefinition("Regulations", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);
        var row = new ParameterRow(ParameterValue.ReadOnly(definition, () => { reads++; return value; }));
        row.Prime();
        var afterPrime = reads;
        Assert.Equal("Rw 38 dB", row.Text);
        Assert.Equal("Rw 38 dB", row.Text);
        Assert.False(row.Flag);
        Assert.Equal(afterPrime, reads);

        // A value read once even through the tick box WPF asks every row for.
        value = "changed";
        Assert.Equal("Rw 38 dB", row.Text);
    }
}
