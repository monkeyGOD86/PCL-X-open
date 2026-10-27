namespace PCLCS.Bedrock;

/// <summary>Conservative UI eligibility for experimental operations, not a platform support guarantee.</summary>
public static class BedrockCapabilities {
    private static bool MatchesObservedPackage(BedrockInstallation? installation) => installation is not null &&
        string.Equals(installation.FamilyName, "Microsoft.MinecraftUWP_8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(installation.PackageFullName, "Microsoft.MinecraftUWP_1.26.5203.0_x64__8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(installation.AppUserModelId, "Microsoft.MinecraftUWP_8wekyb3d8bbwe!Game", StringComparison.OrdinalIgnoreCase) &&
        installation.Version == "1.26.5203.0" && installation.Architecture == "x64";

    public static bool CanManageResourcePacks(BedrockInstallation? installation, BedrockDataRoot? root) =>
        MatchesObservedPackage(installation) && root is not null && !root.SupportsWorlds && root.Kind == BedrockDataRootKind.GdkShared;

    public static bool CanEditWorldPacks(BedrockInstallation? installation, BedrockDataRoot? root, BedrockWorldInfo? world) =>
        MatchesObservedPackage(installation) && root is not null && root.SupportsWorlds && root.Kind == BedrockDataRootKind.GdkAccountCandidate &&
        world is not null && world.HasLevelData && world.Warning is null;
}
