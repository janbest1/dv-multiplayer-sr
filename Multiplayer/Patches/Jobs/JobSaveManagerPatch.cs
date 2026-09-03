using HarmonyLib;
using Multiplayer.Components.Networking.Jobs;

namespace Multiplayer.Patches.Jobs;

/// <summary>
/// The game finishes loading a save by matching every job to its booklet and throwing away the
/// booklets nothing claimed. Overview sheets are never part of that, because the game does not
/// expect one to be saved - so the sheets that came back and found nothing are cleared up here.
/// </summary>
[HarmonyPatch(typeof(JobSaveManager), nameof(JobSaveManager.LoadJobSaveGameData))]
public static class JobSaveManager_LoadJobSaveGameData_Patch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        JobPaperwork.SaveLoaded();
    }
}
