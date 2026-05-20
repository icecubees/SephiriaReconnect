using HarmonyLib;
using Mirror;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SephiriaReconnect;

public static class ReconnectPatches
{
    public static void Apply(Harmony harmony)
    {
        if (harmony == null)
        {
            return;
        }

        ApplyPlayerSlotPatch(harmony);
        ApplyFloorEntryPatch(harmony);
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
                Debug.LogWarning("[SephiriaReconnect] Player slot patch target was not found. UI and reconnect session logic will still load.");
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Debug.Log("[SephiriaReconnect] Player slot patch applied to " + target.Name + ".");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Player slot patch failed. UI and reconnect session logic will still load: " + ex);
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
                Debug.LogWarning("[SephiriaReconnect] Floor entry patch target was not found. Automatic checkpoints will use manual actions only.");
                return;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Debug.Log("[SephiriaReconnect] Floor entry patch applied to DungeonManager.HandleStartFloorAfterSavingServerside().");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Floor entry patch failed. Automatic checkpoints will use manual actions only: " + ex);
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
            Debug.Log("[SephiriaReconnect] Rebound reconnecting player " + steamID + " to reserved slot " + reservedSlot + " before Initialize().");
        }
    }

    private static void NotifyFloorEntryAfterOriginalSave(bool allowSave, string floorGuid, bool runStarted)
    {
        ReconnectController.NotifyFloorEntryCheckpointCandidate(allowSave, floorGuid, runStarted);
    }
}
