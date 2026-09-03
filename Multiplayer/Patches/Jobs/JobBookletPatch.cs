using DV.Logic.Job;
using HarmonyLib;
using Multiplayer.Components.Networking.Jobs;
using Multiplayer.Components.Networking.World;


namespace Multiplayer.Patches.Jobs;

[HarmonyPatch(typeof(JobBooklet))]
public static class JobBooklet_Patch
{
    //[HarmonyPatch(nameof(JobBooklet.AssignJob))]
    //[HarmonyPostfix]
    //private static void AssignJob(JobBooklet __instance, Job jobToAssign)
    //{
    //    if (!NetworkedJob.TryGetFromJob(__instance.job, out NetworkedJob networkedJob))
    //    {
    //        Multiplayer.LogError($"JobBooklet.AssignJob() NetworkedJob not found for Job ID: {__instance.job?.ID}");
    //        return;
    //    }

    //    networkedJob.JobBooklet = __instance;
    //    if(networkedJob.TryGetComponent(out NetworkedItem netItem))
    //        networkedJob.ValidationItem = netItem;
    //}


    /// <summary>
    /// The booklet has just read its job id back out of the save. On a client the game's own
    /// matching never runs, so nothing would ever give it its job.
    /// </summary>
    [HarmonyPatch(nameof(JobBooklet.OnSaveDataLoaded))]
    [HarmonyPostfix]
    private static void OnSaveDataLoaded(JobBooklet __instance)
    {
        JobBookletRestorer.BookletLoaded(__instance);
    }

    [HarmonyPatch(nameof(JobBooklet.DestroyJobBooklet))]
    [HarmonyPrefix]
    private static void DestroyJobBooklet(JobBooklet __instance)
    {
        if (__instance == null || __instance.job == null)
            return;

        if (!NetworkedJob.TryGetFromJob(__instance?.job, out NetworkedJob networkedJob))
            Multiplayer.LogError($"JobBooklet.DestroyJobBooklet() NetworkedJob not found for Job ID: {__instance?.job?.ID}");
        else
            networkedJob.JobBooklet = null;
    }
}
