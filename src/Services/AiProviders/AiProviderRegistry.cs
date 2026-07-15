// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

namespace FiveOS.Services.AiProviders;

/// <summary>
/// Single source of truth for every Image/Text → 3D backend the app knows
/// about. The order here is the order shown in the provider dropdown.
/// </summary>
public static class AiProviderRegistry
{
    public static IReadOnlyList<IAiProvider> All { get; } = new IAiProvider[]
    {
        new MeshyProvider(),
        new Tripo3DProvider(),
        new RodinProvider(),
        new ReplicateProvider(),
        new StabilityProvider(),
    };

    public static IAiProvider? FindById(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
