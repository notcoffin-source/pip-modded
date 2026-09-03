using BepInEx.Bootstrap;
using EFT;
using HarmonyLib;
using System.Reflection;

namespace PiPDisabler.Patches
{
    internal static class DERPCompat
    {
        private const string DERPFixPluginGuid = "com.Shibatsu.DynamicExternalResolution";
        private const string HarmonyId = "com.fiodor.pipdisabler.derpcompat";
        private static Harmony _harmony;
        public static void Enable()
        {
            if (!Chainloader.PluginInfos.ContainsKey(DERPFixPluginGuid))
            {
                PiPDisablerPlugin.DebugLogInfo("[DERPCompat] DynamicExternalResolution not installed, no patching O.o");
                return;
            }

            var patchesType = AccessTools.TypeByName("DynamicExternalResolution.DynamicExternalResolutionPatches");

            var setResolutionAim = AccessTools.Method(patchesType, "SetResolutionAim");

            var prefix = new HarmonyMethod(typeof(DERPCompat).GetMethod(
                nameof(SetResolutionAimPrefix),
                BindingFlags.NonPublic | BindingFlags.Static));

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(setResolutionAim, prefix: prefix);

            PiPDisablerPlugin.DebugLogInfo("[DERPCompat] Patched DynamicExternalResolution SetResolutionAim.");

        }

        private static bool SetResolutionAimPrefix()
        {
            if (!Settings.ModEnabled.Value)
                return true;

            if (ScopeLifecycle.IsCurrentOrPendingOpticBypassed() || ScopeLifecycle.IsLastOpticNameBypassed())
            {
                PiPDisablerPlugin.DebugLogInfo("[DERPCompat] Allowing DERP.");
                return true;
            }

            PiPDisablerPlugin.DebugLogInfo("[DERPCompat] No DERP today");
            return false;
        }
    }
}
