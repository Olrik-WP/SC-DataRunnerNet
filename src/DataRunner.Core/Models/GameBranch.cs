namespace DataRunner.Core.Models;

/// <summary>
/// Star Citizen game branch a screenshot was taken from. Determined by
/// which configured screenshots folder slot detected the file (LIVE folder
/// vs PTU folder), NOT by parsing the file path — users may put arbitrary
/// paths in either slot.
///
/// Maps directly to the <c>game_version</c> field of UEX's /data_submit:
///   <see cref="Live"/> → resolved to UEX's current LIVE build number (e.g. "4.7.2")
///   <see cref="Ptu"/>  → resolved to UEX's current PTU build number (e.g. "4.8.0")
///
/// The user can always override the resolved version in the editor's
/// "Optional metadata" panel.
/// </summary>
public enum GameBranch
{
    /// <summary>The public LIVE channel — default when nothing is specified.</summary>
    Live = 0,

    /// <summary>The Public Test Universe — UEX may temporarily reject PTU
    /// reports during patch transitions (<c>ptu_reports_not_allowed</c>).</summary>
    Ptu = 1,
}
