using HarmonyLib;
using Multiplayer.Components.Networking;

namespace Multiplayer.Patches.World.Items;

/// <summary>
/// Stops a fast travel taking everyone else's world apart.
///
/// Travelling by the map ends with the game gathering up every one of the player's own things that
/// is lying loose in the world and putting it in their lost and found, so it can meet them at the
/// other end. Alone that is a kindness. Together it is one person's map click emptying a shed, a
/// desk and a station platform that two people were using - and the other one watching their tools
/// wink out around them.
///
/// The lost and found still opens and still holds whatever is genuinely in it. It just stops
/// reaching out into the world to fill itself.
/// </summary>
[HarmonyPatch(typeof(StorageController), nameof(StorageController.RequestLostAndFoundItemActivation))]
public static class StorageController_RequestLostAndFoundItemActivation_Patch
{
    private static bool Prefix(StorageController __instance)
    {
        if (!NetworkLifecycle.Instance.IsClientRunning && !NetworkLifecycle.Instance.IsServerRunning)
            return true;

        if (__instance.StorageLostAndFound == null)
            return true;

        //Everything the original does, less the summoning: put away what the lost and found is
        //already holding, then let the nearest one open again and lay it back out.
        __instance.ItemTransformControllerLostAndFound.DeactivateItems();
        __instance.RequestItemActivation();

        return false;
    }
}
