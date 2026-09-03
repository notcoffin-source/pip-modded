using BepInEx.Bootstrap;
using HarmonyLib;

namespace PiPDisabler.Patches
{
    internal static class FOVFixCompat
    {
        private const string FOVFixPluginGuid = "com.fontaine.fovfix";
        private const string HarmonyId = "com.fiodor.pipdisabler.fovfixcompat";

        public static void Enable()
        {
            if (!Chainloader.PluginInfos.ContainsKey(FOVFixPluginGuid))
            {
                PiPDisablerPlugin.DebugLogInfo("[FOVFixCompat] FOVFix not installed, no patching O.o");
                return;
            }

            var h = new Harmony(HarmonyId);

            void Patch(string target, string prefix)
            {
                var m = AccessTools.Method(target);
                h.Patch(m, prefix: new HarmonyMethod(typeof(FOVFixCompat), prefix));
            }

            Patch("FOVFix.FovController:ChangeMainCamFOV", nameof(SkipVoidWhenPiPActive));
            Patch("FOVFix.PwaWeaponParamsPatch:PatchPostfix", nameof(SkipVoidWhenPiPActive));
            Patch("FOVFix.SetPlayerAimingPatch:PatchPostfix", nameof(SkipVoidWhenPiPActive));
            Patch("FOVFix.FreeLookPatch:Prefix", nameof(SkipBoolPatchWhenPiPActive));
            Patch("FOVFix.LerpCameraPatch:Prefix", nameof(SkipBoolPatchWhenPiPActive));
            Patch("FOVFix.CalculateScaleValueByFovPatch:Prefix", nameof(SkipBoolPatchWhenPiPActive));

        }

        private static bool SkipVoidWhenPiPActive()
            => !Settings.ModEnabled.Value || !ScopeLifecycle.IsScoped || ScopeLifecycle.IsModBypassedForCurrentScope;

        private static bool SkipBoolPatchWhenPiPActive(ref bool __result)
        {
            if (!Settings.ModEnabled.Value || !ScopeLifecycle.IsScoped || ScopeLifecycle.IsModBypassedForCurrentScope)
                return true;

            __result = true;
            return false;
        }
    }
}