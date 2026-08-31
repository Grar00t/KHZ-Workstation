using System.Collections.Generic;

namespace KHZ.App.Trust;

internal enum Capability
{
    LocalOfficeNavigation,
    LocalFileLaunch,
    LocalRepositoryInspection,
    UserTerminalExecution,

    ExternalWebNavigation,

    InstitutionalNetwork,
    InternetEgress,

    ArbitraryProcessExecution,

    IntegrationRead,
    IntegrationWrite,

    AiInference
}

internal sealed class CapabilityPolicy
{
    private readonly HashSet<Capability> _allowed;

    private CapabilityPolicy(IEnumerable<Capability> allowed)
    {
        _allowed = new HashSet<Capability>(allowed);
    }

    public static CapabilityPolicy CreateInstitutionalDefault()
        => new(
        [
            Capability.LocalOfficeNavigation,
            Capability.LocalFileLaunch,
            Capability.LocalRepositoryInspection
        ]);

    public bool IsAllowed(Capability capability)
        => _allowed.Contains(capability);
}
