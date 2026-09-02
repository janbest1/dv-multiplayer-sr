using DV.UI;
using DV.UIFramework;
using HarmonyLib;
using Multiplayer.Components.Networking;
using System;
using System.Collections;
using System.Reflection;

namespace Multiplayer.Patches.PauseMenu;

[HarmonyPatch(typeof(PauseMenuController))]
public static class PauseMenuController_Patch
{
    private static readonly PopupLocalizationKeys popupQuitLocalizationKeys = new PopupLocalizationKeys
    {
        positiveKey = "yes",
        negativeKey = "no",
        labelKey = Locale.PAUSE_MENU_QUIT_KEY
    };
    private static readonly PopupLocalizationKeys popupDisconnectLocalizationKeys = new PopupLocalizationKeys
    {
        positiveKey = "yes",
        negativeKey = "no",
        labelKey = Locale.PAUSE_MENU_DISCONNECT_KEY
    };


    [HarmonyPatch(nameof(PauseMenuController.Start))]
    [HarmonyPostfix]
    private static void Start(PauseMenuController __instance)
    {
        if(NetworkLifecycle.Instance.IsHost())
            return;

        __instance.loadSaveButton.gameObject.SetActive(false);
        __instance.tutorialsButton.gameObject.SetActive(false);
    }

    //Set while we are holding a click back to gather from the clients, so the second one goes
    //through rather than starting all over again
    private static bool gathering;

    /// <summary>
    /// Holds a host's click just long enough to ask everyone what they are carrying and hear back.
    /// Nothing is replaced: the click is handed back to the game the moment the answers are in, so
    /// the usual "are you sure" and everything after it happen exactly as they always did.
    /// </summary>
    private static bool GatherFromClients(Action carryOn)
    {
        if (gathering || !NetworkLifecycle.Instance.IsHost() || NetworkLifecycle.Instance.Server == null)
            return false;

        gathering = true;

        NetworkLifecycle.Instance.StartCoroutine(Gather(carryOn));

        return true;
    }

    private static IEnumerator Gather(Action carryOn)
    {
        yield return NetworkLifecycle.Instance.Server.WaitForPlayerStorage();

        carryOn();

        gathering = false;
    }

    [HarmonyPatch(nameof(PauseMenuController.OnExitLevelClicked))]
    [HarmonyPrefix]
    private static bool OnExitLevelClicked(PauseMenuController __instance, IClickable _)
    {
        IClickable clicked = _;

        if (NetworkLifecycle.Instance.IsHost())
            return !GatherFromClients(() => __instance.OnExitLevelClicked(clicked));


        if (!__instance.popupManager.CanShowPopup())
        {
            Multiplayer.LogWarning("PauseMenuController.OnExitLevelClicked() PopupManager can't show popups at this moment");
            return false;
        }
        Popup popupPrefab = __instance.yesNoPopupPrefab;
        PopupLocalizationKeys locKeys = popupDisconnectLocalizationKeys;

        __instance.popupManager.ShowPopup(popupPrefab, locKeys).Closed += (PopupResult result) =>
            {
                //Negative = 'No', so we're aborting the disconnect
                if (result.closedBy == PopupClosedByAction.Negative)
                    return;

                //Negative = 'No', so we're aborting the disconnect
                if (result.closedBy == PopupClosedByAction.Negative)
                    return;

                //On the way out, say what we have on us. Our own game is never written to disk, so
                //if we leave without a word the host has only what it could work out for itself.
                NetworkLifecycle.Instance.Client?.SendPlayerStorage();

                FieldInfo eventField = __instance.GetType().GetField("ExitLevelRequested", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (eventField != null)
                {
                    Delegate eventDelegate = (Delegate)eventField.GetValue(__instance);
                    if (eventDelegate != null)
                        eventDelegate.DynamicInvoke();
                }
            };

        return false;
    }

    [HarmonyPatch("OnQuitClicked")]
    [HarmonyPrefix]
    private static bool OnQuitClicked(PauseMenuController __instance, IClickable _)
    {
        IClickable clicked = _;

        if (NetworkLifecycle.Instance.IsHost())
            return !GatherFromClients(() => __instance.OnQuitClicked(clicked));


        if (!__instance.popupManager.CanShowPopup())
        {
            Multiplayer.LogWarning("PauseMenuController.OnQuitClicked() PopupManager can't show popups at this moment");
            return false;
        }
        Popup popupPrefab = __instance.yesNoPopupPrefab;
        PopupLocalizationKeys locKeys = popupDisconnectLocalizationKeys;

        __instance.popupManager.ShowPopup(popupPrefab, locKeys).Closed += (PopupResult result) =>
            {
                //Negative = 'No', so we're aborting the disconnect
                if (result.closedBy == PopupClosedByAction.Negative)
                    return;

                //On the way out, say what we have on us. Our own game is never written to disk, so
                //if we leave without a word the host has only what it could work out for itself.
                NetworkLifecycle.Instance.Client?.SendPlayerStorage();

                FieldInfo eventField = __instance.GetType().GetField("QuitGameRequested", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (eventField != null)
                {
                    Delegate eventDelegate = (Delegate)eventField.GetValue(__instance);
                    if (eventDelegate != null)
                        eventDelegate.DynamicInvoke();
                }
            };

        return false;
    }


}
