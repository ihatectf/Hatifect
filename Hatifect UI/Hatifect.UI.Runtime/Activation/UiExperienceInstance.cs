using System;
using System.Threading;
using Hatifect.UI.Experience;

namespace Hatifect.UI.Runtime.Activation;

/// <summary>
/// Internal/provisional activation value that keeps a semantic definition and its composition-root
/// owner in one lifecycle. Public registrations create unowned values; first-party Cached dogfood
/// registrations may attach an owner until the final lifecycle API is selected.
/// </summary>
internal sealed class UiExperienceInstance : IDisposable
{
    private IDisposable? _owner;

    public UiExperienceInstance(UiExperienceDefinition experience, IDisposable? owner = null)
    {
        Experience = experience ?? throw new ArgumentNullException(nameof(experience));
        _owner = owner;
    }

    public UiExperienceDefinition Experience { get; }
    public bool HasOwner => _owner != null;

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Dispose();

    internal Exception DisposeAfterFailure(Exception failure, string message)
        => DisposeOwnerAfterFailure(this, failure, message);

    internal static Exception DisposeOwnerAfterFailure(
        IDisposable owner,
        Exception failure,
        string message)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(failure);
        try
        {
            owner.Dispose();
            return failure;
        }
        catch (Exception disposalFailure)
        {
            return new AggregateException(message, failure, disposalFailure);
        }
    }
}
