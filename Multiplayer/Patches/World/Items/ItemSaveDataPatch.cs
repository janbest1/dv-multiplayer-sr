using HarmonyLib;
using Multiplayer.Components.Networking.Jobs;
using Newtonsoft.Json.Linq;

namespace Multiplayer.Patches.World.Items;

/// <summary>
/// A job booklet writes the id of its job into its own save data, so it can be matched up again
/// when a save is loaded. A job overview sheet does not: the game never expects one to outlive a
/// visit to the station, because a station prints new ones whenever a player is nearby and throws
/// them away when nobody is.
///
/// In multiplayer a player can log out carrying a sheet, and their belongings are saved and handed
/// back on the next join. Without an id on it the sheet comes back as blank paper for a job nobody
/// can name, so the id is written here and read back the same way.
/// </summary>
[HarmonyPatch(typeof(ItemSaveData))]
public static class ItemSaveData_Patch
{
    private const string JOB_PAPER_KEY = "mp_jobOverviewId";

    [HarmonyPatch(nameof(ItemSaveData.SaveItemData))]
    [HarmonyPostfix]
    private static void SaveItemData(ItemSaveData __instance, JObject __result)
    {
        if (__result == null)
            return;

        JobOverview overview = __instance.GetComponent<JobOverview>();

        if (overview != null && overview.job != null)
        {
            __result[JOB_PAPER_KEY] = overview.job.ID;
            return;
        }

        //A sheet that is back but has not found its job yet must not lose the id on the way through
        //an autosave.
        JobPaper paper = __instance.GetComponent<JobPaper>();

        if (paper != null && !string.IsNullOrEmpty(paper.JobId))
            __result[JOB_PAPER_KEY] = paper.JobId;
    }

    [HarmonyPatch(nameof(ItemSaveData.LoadItemData))]
    [HarmonyPostfix]
    private static void LoadItemData(ItemSaveData __instance, JObject data)
    {
        if (data == null)
            return;

        JToken token = data[JOB_PAPER_KEY];

        if (token == null || token.Type != JTokenType.String)
            return;

        JobPaper.Remember(__instance.gameObject, token.Value<string>());
    }
}
