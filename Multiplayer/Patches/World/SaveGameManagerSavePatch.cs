using DV;
using DV.Common;
using DV.Utils;
using HarmonyLib;
using Multiplayer.Components.Networking;
using System.Collections;
using UnityEngine;

namespace Multiplayer.Patches.World;

/// <summary>
/// Holds a save back until every client has said what it is carrying, so that the answers go into
/// this save rather than the one after it.
///
/// A client's game is never written to disk, so what they have on them exists only in what they say
/// - and there is no way to ask and be answered without letting a frame go by, since the answer
/// comes in over the same connection the waiting would block. So the save is handed back to the
/// game as it was, a moment later, with everything in hand.
/// </summary>
[HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.Save))]
public static class SaveGameManager_Save_Patch
{
    //Set while a save is being held back, so the one we start ourselves goes straight through
    private static bool gathering;

    //Set once the game has decided to close. Nothing may be held back after that: the save on the
    //way out is followed by the process ending, and a save handed back a frame later is a save that
    //never happens. The host asked everyone a moment ago in the pause menu anyway.
    public static bool Leaving;

    //How recently an answer round has to have finished for a save to go straight through
    private const float RECENT = 30f;

    private static bool Prefix(SaveGameManager __instance, SaveType type, ISaveGame saveToOverwrite, bool updateInternalData, ref ISaveGame __result)
    {
        if (gathering || !NetworkLifecycle.Instance.IsHost() || NetworkLifecycle.Instance.Server == null)
            return true;

        //On the way out there is no time for this
        if (Leaving || __instance.isQuitting || UnloadWatcher.isUnloading)
            return true;

        //Just asked - as happens when the host leaves through the pause menu, which asks before it
        //hands the click on. Asking again would hold up a save that is already about to be the last.
        if (Time.realtimeSinceStartup - NetworkLifecycle.Instance.Server.LastStorageGather < RECENT)
            return true;

        int asked = NetworkLifecycle.Instance.Server.RequestPlayerStorage();

        //Nobody to wait for
        if (asked == 0)
            return true;

        gathering = true;
        __result = null;

        NetworkLifecycle.Instance.StartCoroutine(SaveWhenEveryoneHasAnswered(__instance, type, saveToOverwrite, updateInternalData, asked));

        return false;
    }

    private static IEnumerator SaveWhenEveryoneHasAnswered(SaveGameManager manager, SaveType type, ISaveGame saveToOverwrite, bool updateInternalData, int asked)
    {
        yield return NetworkLifecycle.Instance.Server.WaitForStorageReports(asked);

        manager.Save(type, saveToOverwrite, updateInternalData);

        gathering = false;
    }
}

/// <summary>
/// Notices that the game is closing before anything else does, so no save is held back from here on.
/// </summary>
[HarmonyPatch(typeof(SaveGameManager), "OnApplicationWantsToQuit")]
public static class SaveGameManager_OnApplicationWantsToQuit_Patch
{
    private static void Prefix()
    {
        SaveGameManager_Save_Patch.Leaving = true;
    }
}
