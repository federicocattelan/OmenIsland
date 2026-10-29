namespace OmenIsland.App.Services;

public interface IControlConsentService
{
    bool HasAcceptedControl { get; }

    Task<bool> EnsureConsentAsync();
}
