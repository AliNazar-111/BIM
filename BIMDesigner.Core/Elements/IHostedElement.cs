namespace BIMDesigner.Core.Elements;

/// <summary>
/// An element that cannot exist on its own (specification section 2.5): a door or window
/// needs a wall, a light may need a ceiling, a sprinkler a pipe.
///
/// The host owns the hosted element's fate. Deleting a wall deletes its doors, because a
/// door floating in space is not a thing a building can contain - and leaving one behind
/// would quietly corrupt every schedule and quantity that counts them.
/// </summary>
public interface IHostedElement
{
    /// <summary>The element this one is carried by.</summary>
    Guid HostId { get; }
}
