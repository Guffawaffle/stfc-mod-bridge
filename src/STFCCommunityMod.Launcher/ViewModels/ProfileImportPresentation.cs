using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

public sealed record ProfileImportPresentation(string Title, string CopyExplanation, string PermissionExplanation, string Details)
{
    public static ProfileImportPresentation From(ProfileUserImportPlan plan) => new(
        $"Import {plan.SourceUserName}’s STFC setup",
        $"We’ll copy {plan.SourceUserName}’s saved login and game settings into {plan.Name}, a new profile for Windows user {plan.DestinationUserName}. {plan.SourceUserName}’s original setup will stay as it is.",
        plan.RequiresElevation
            ? $"{plan.Reason} Choose Continue to open the Windows permission prompt. If needed, Windows will ask for an administrator’s username and password."
            : "This is a one-time copy. Choose Continue to import it. Your launch selection will stay as it is.",
        "Windows checks permission to read the selected user’s saved STFC data. Any administrator approval applies only to this import. The new profile still belongs to the destination Windows user shown above. Profiles does not ask for or store Windows passwords.");
}
