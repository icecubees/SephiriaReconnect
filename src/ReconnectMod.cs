using HarmonyLib;
using UnityEngine;

namespace SephiriaReconnect;

public sealed class ReconnectMod : HorayModBase
{
    private const string HarmonyId = "SephiriaReconnect.RuntimePatches";

    private GameObject controllerObject;
    private Harmony harmony;

    protected override void OnModLoaded()
    {
        harmony = new Harmony(HarmonyId);
        ReconnectPatches.Apply(harmony);

        if (ReconnectController.Instance != null)
        {
            controllerObject = ReconnectController.Instance.gameObject;
            ReconnectLogger.Info("Reusing existing controller.");
            return;
        }

        controllerObject = new GameObject("SephiriaReconnectController");
        Object.DontDestroyOnLoad(controllerObject);
        controllerObject.AddComponent<ReconnectController>();
        ReconnectLogger.Info("Loaded with Harmony patches.");
    }

    protected override void OnModUnloaded()
    {
        if (harmony != null)
        {
            harmony.UnpatchAll(HarmonyId);
            ReconnectPatches.ResetAppliedState();
            harmony = null;
        }

        if (controllerObject != null)
        {
            Object.Destroy(controllerObject);
            controllerObject = null;
        }

        ReconnectLogger.Shutdown();
    }
}
