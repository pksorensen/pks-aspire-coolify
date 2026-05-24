namespace Aspire.Hosting.Coolify;

/// <summary>
/// ADR-002 §2: pinned minimum supported Coolify version. The configure-phase probe
/// compares the observed version against this floor (FT-002 step 5).
///
/// The concrete value is wired here when the first endpoint lands; raising the floor is
/// an explicit PR review per ADR-002 §7.
/// </summary>
internal static class SupportedCoolifyVersions
{
    /// <summary>Minimum supported Coolify version (SemVer-ish).</summary>
    public const string Floor = "4.0.0";
}
