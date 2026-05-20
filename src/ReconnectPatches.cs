using HarmonyLib;
using HeathenEngineering.SteamworksIntegration;
using HeathenEngineering.SteamworksIntegration.API;
using Mirror;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SephiriaReconnect;

public static class ReconnectPatches
{
    private static bool patchesApplied;
    private static readonly FieldInfo SteamInvitationInvitePhaseField =
        AccessTools.Field(typeof(SteamInvitation), "invitePhase");
    private static readonly FieldInfo SteamInvitationBackgroundInvitedField =
        AccessTools.Field(typeof(SteamInvitation), "backgroundInvited");

    public static void Apply(Harmony harmony)
    {
        if (harmony == null)
        {
            return;
        }

        if (patchesApplied)
        {
            ReconnectLogger.Info("Harmony patches already applied; skipping duplicate patch pass.");
            return;
        }

        ApplyPlayerSlotPatch(harmony);
        ApplyFloorEntryPatch(harmony);
        ApplySteamInvitationPatch(harmony);
        patchesApplied = true;
    }

    public static void ResetAppliedState()
    {
        patchesApplied = false;
    }

    private static void ApplyPlayerSlotPatch(Harmony harmony)
    {
        try
        {
            MethodInfo target = AccessTools.GetDeclaredMethods(typeof(PlayerSpawner))
                .FirstOrDefault(IsCmdSetDefaultPlayerDataUserCode);
            MethodInfo prefix = AccessTools.Method(typeof(ReconnectPatches), nameof(BindReservedPlayerSlotBeforeInitialize));

            if (target == null || prefix == null)
            {
                ReconnectLogger.Warning("Player slot patch target was not found. UI and reconnect session logic will still load.");
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            ReconnectLogger.Info("Player slot patch applied to " + target.Name + ".");
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Player slot patch failed. UI and reconnect session logic will still load: " + ex);
        }
    }

    private static void ApplyFloorEntryPatch(Harmony harmony)
    {
        try
        {
            MethodInfo target = AccessTools.Method(
                typeof(DungeonManager),
                "HandleStartFloorAfterSavingServerside",
                new[] { typeof(bool), typeof(string), typeof(bool) });
            MethodInfo postfix = AccessTools.Method(typeof(ReconnectPatches), nameof(NotifyFloorEntryAfterOriginalSave));

            if (target == null || postfix == null)
            {
                ReconnectLogger.Warning("Floor entry patch target was not found. Automatic checkpoints will use manual actions only.");
                return;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            ReconnectLogger.Info("Floor entry patch applied to DungeonManager.HandleStartFloorAfterSavingServerside().");
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Floor entry patch failed. Automatic checkpoints will still use manual actions only: " + ex);
        }
    }

    private static void ApplySteamInvitationPatch(Harmony harmony)
    {
        try
        {
            MethodInfo enterTarget = AccessTools.Method(
                typeof(SteamInvitation),
                "HandleEnterSuccess",
                new[] { typeof(LobbyData) });
            MethodInfo enterPostfix = AccessTools.Method(typeof(ReconnectPatches), nameof(ConnectToReconnectHostAfterLobbyEnter));
            MethodInfo dataTarget = AccessTools.Method(
                typeof(SteamInvitation),
                "HandleDataUpdated",
                new[] { typeof(LobbyDataUpdateEventData) });
            MethodInfo dataPostfix = AccessTools.Method(typeof(ReconnectPatches), nameof(ConnectToReconnectHostAfterLobbyDataUpdated));

            if (enterTarget == null || enterPostfix == null)
            {
                ReconnectLogger.Warning("Steam invitation enter-success patch target was not found. Steam invite reconnect will rely on vanilla behavior.");
            }
            else
            {
                harmony.Patch(enterTarget, postfix: new HarmonyMethod(enterPostfix));
                ReconnectLogger.Info("Steam invitation enter-success patch applied.");
            }

            if (dataTarget == null || dataPostfix == null)
            {
                ReconnectLogger.Warning("Steam invitation data-update patch target was not found. Reconnect invites may require entering the save first.");
            }
            else
            {
                harmony.Patch(dataTarget, postfix: new HarmonyMethod(dataPostfix));
                ReconnectLogger.Info("Steam invitation data-update patch applied.");
            }
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Steam invitation patch failed. Steam invite reconnect will rely on vanilla behavior: " + ex);
        }
    }

    private static bool IsCmdSetDefaultPlayerDataUserCode(MethodInfo method)
    {
        if (method == null || !method.Name.StartsWith("UserCode_CmdSetDefaultPlayerData", StringComparison.Ordinal))
        {
            return false;
        }

        ParameterInfo[] parameters = method.GetParameters();
        return parameters.Length >= 1 && parameters[0].ParameterType == typeof(ulong);
    }

    private static void BindReservedPlayerSlotBeforeInitialize(PlayerSpawner __instance, ulong steamID)
    {
        if (!NetworkServer.active || __instance == null || steamID == 0)
        {
            return;
        }

        if (!ReconnectController.TryGetReservedPlayerSlot(steamID, out int reservedSlot))
        {
            return;
        }

        bool changed = false;
        if (__instance.currentPlayerIdxForSave != reservedSlot)
        {
            __instance.currentPlayerIdxForSave = reservedSlot;
            changed = true;
        }

        if (__instance.currentPlayerIdx != reservedSlot)
        {
            __instance.NetworkcurrentPlayerIdx = reservedSlot;
            changed = true;
        }

        if (changed)
        {
            ReconnectLogger.Info("Rebound reconnecting player " + steamID + " to reserved slot " + reservedSlot + " before Initialize().");
        }
    }

    private static void NotifyFloorEntryAfterOriginalSave(bool allowSave, string floorGuid, bool runStarted)
    {
        ReconnectController.NotifyFloorEntryCheckpointCandidate(allowSave, floorGuid, runStarted);
    }

    private static void ConnectToReconnectHostAfterLobbyEnter(LobbyData arg0)
    {
        ReconnectController.NotifySteamLobbyEntered(arg0, "enter-success");
    }

    private static void ConnectToReconnectHostAfterLobbyDataUpdated(SteamInvitation __instance, LobbyDataUpdateEventData arg0)
    {
        if (!IsActiveBackgroundInvite(__instance, arg0.lobby))
        {
            return;
        }

        ReconnectController.NotifySteamLobbyEntered(arg0.lobby, "data-updated");
    }

    private static bool IsActiveBackgroundInvite(SteamInvitation invitation, LobbyData lobby)
    {
        try
        {
            if (invitation == null || SteamInvitationInvitePhaseField == null || SteamInvitationBackgroundInvitedField == null)
            {
                return false;
            }

            int invitePhase = (int)SteamInvitationInvitePhaseField.GetValue(invitation);
            if (invitePhase != 1)
            {
                return false;
            }

            object value = SteamInvitationBackgroundInvitedField.GetValue(invitation);
            return value is LobbyData backgroundInvited && backgroundInvited == lobby;
        }
        catch (Exception ex)
        {
            ReconnectLogger.Warning("Failed to inspect Steam invitation background invite state: " + ex.Message);
            return false;
        }
    }
}
