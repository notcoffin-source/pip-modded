using System.Reflection;
using EFT;
using EFT.CameraControl;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using System.Linq;

namespace PiPDisabler.Patches
{
    internal sealed class OpticSightOnEnablePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(OpticSight), "OnEnable");

        [PatchPostfix]
        private static void Postfix(OpticSight __instance)
        {
            // Always cache the enabled optic (so it's ready if mod is toggled on later)
            PiPDisablerPlugin.DebugLogInfo(
                $"[Patch] OnEnable: '{(__instance != null ? __instance.name : "null")}' " +
                $"enabled={__instance?.enabled} frame={Time.frameCount}");

            if (!Settings.ModEnabled.Value) return;
            ScopeLifecycle.OnOpticEnabled(__instance);
        }
    }

    internal sealed class OpticSightOnDisablePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(OpticSight), "OnDisable");

        [PatchPostfix]
        private static void Postfix(OpticSight __instance)
        {
            PiPDisablerPlugin.DebugLogInfo(
                $"[Patch] OnDisable: '{(__instance != null ? __instance.name : "null")}' " +
                $"frame={Time.frameCount}");

            if (!Settings.ModEnabled.Value) return;
            ScopeLifecycle.OnOpticDisabled(__instance);
        }
    }

    internal sealed class TacticalRangeFinderOnEnablePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(TacticalRangeFinderController), "OnEnable");

        [PatchPostfix]
        private static void Postfix(TacticalRangeFinderController __instance)
        {
            if (!Settings.ModEnabled.Value) return;
            if (__instance == null) return;

            var opticSight = ResolveRangeFinderOptic(__instance.transform);

            PiPDisablerPlugin.DebugLogInfo(
                $"[Patch] TacticalRangeFinder OnEnable: optic='{opticSight?.name ?? "null"}' " +
                $"path='{GetPath(opticSight != null ? opticSight.transform : null)}' frame={Time.frameCount}");

            ScopeLifecycle.RestoreBypassedOpticState(opticSight,
                reason: "tactical rangefinder enable");
        }

        private static OpticSight ResolveRangeFinderOptic(Transform rangeFinderTransform)
        {
            if (rangeFinderTransform == null) return null;

            Transform itemRoot = null;
            for (var t = rangeFinderTransform; t != null; t = t.parent)
            {
                if (t.name == "item")
                {
                    itemRoot = t;
                    break;
                }
            }

            var searchRoot = itemRoot != null ? itemRoot : rangeFinderTransform.root;
            var optics = searchRoot.GetComponentsInChildren<OpticSight>(true);
            if (optics == null || optics.Length == 0)
                return null;

            OpticSight best = null;
            int bestScore = int.MinValue;
            for (int i = 0; i < optics.Length; i++)
            {
                var optic = optics[i];
                if (optic == null) continue;

                string path = GetPath(optic.transform);
                int score = 0;
                if (optic.isActiveAndEnabled) score += 100;
                if (path.IndexOf("optic_camera", System.StringComparison.OrdinalIgnoreCase) >= 0) score += 50;
                if (optic.CameraData != null) score += 10;
                if (optic.ScopeData != null) score += 10;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = optic;
                }
            }

            return best;
        }

        private static string GetPath(Transform transform)
        {
            if (transform == null) return "null";

            string path = transform.name;
            for (var t = transform.parent; t != null; t = t.parent)
                path = t.name + "/" + path;
            return path;
        }
    }

    internal sealed class TacticalRangeFinderIgnoreLocalBodyPatch : ModulePatch
    {
        private static readonly RaycastHit[] RaycastHits = new RaycastHit[64];

        private static readonly FieldInfo DistanceOutputFormatField =
            AccessTools.Field(typeof(TacticalRangeFinderController), "_distanceOutputFormat");
        private static readonly FieldInfo NoDistanceTextField =
            AccessTools.Field(typeof(TacticalRangeFinderController), "_noDistanceText");
        private static readonly FieldInfo TextOnDisplayField =
            AccessTools.Field(typeof(TacticalRangeFinderController), "_textOnDisplay");
        private static readonly FieldInfo BoneToCastRayField =
            AccessTools.Field(typeof(TacticalRangeFinderController), "_boneToCastRay");
        private static readonly FieldInfo RayStartOffsetField =
            AccessTools.Field(typeof(TacticalRangeFinderController), "_rayStartOffset");
        private static readonly FieldInfo MaxCastDistanceField =
            AccessTools.Field(typeof(TacticalRangeFinderController), "_maxCastDistance");
        private static readonly FieldInfo MaskField =
            AccessTools.Field(typeof(TacticalRangeFinderController), "_mask");
        private static readonly MethodInfo SetMonospaceTextMethod =
            AccessTools.Method(typeof(GClass1673), "SetMonospaceText");

        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(TacticalRangeFinderController), "method_0");

        [PatchPrefix]
        private static bool Prefix(TacticalRangeFinderController __instance)
        {
            if (!Settings.ModEnabled.Value) return true;
            if (__instance == null) return true;

            try
            {
                var bone = BoneToCastRayField?.GetValue(__instance) as Transform;
                if (bone == null) return true;

                float rayStartOffset = GetFieldFloat(RayStartOffsetField, __instance, 0f);
                float maxCastDistance = GetFieldFloat(MaxCastDistanceField, __instance, 2500f);
                int mask = GetMaskBits(__instance);

                Vector3 origin = bone.position + bone.forward * rayStartOffset;
                int hitCount = Physics.RaycastNonAlloc(new Ray(origin, bone.forward), RaycastHits, maxCastDistance, mask);
                SortHitsByDistance(RaycastHits, hitCount);

                for (int i = 0; i < hitCount; i++)
                {
                    RaycastHit hit = RaycastHits[i];
                    if (IsLocalPlayerBodyHit(hit.collider))
                        continue;

                    SetDisplayText(__instance, FormatDistance(__instance, hit.distance));
                    return false;
                }

                SetDisplayText(__instance, GetNoDistanceText(__instance));
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static bool IsLocalPlayerBodyHit(Collider collider)
        {
            if (collider == null) return false;

            try
            {
                var player = Helpers.GetLocalPlayer();
                return player != null && player.HasBodyPartCollider(collider);
            }
            catch
            {
                return false;
            }
        }

        private static void SortHitsByDistance(RaycastHit[] hits, int count)
        {
            for (int i = 1; i < count; i++)
            {
                RaycastHit current = hits[i];
                int j = i - 1;
                while (j >= 0 && hits[j].distance > current.distance)
                {
                    hits[j + 1] = hits[j];
                    j--;
                }
                hits[j + 1] = current;
            }
        }

        private static string FormatDistance(TacticalRangeFinderController instance, float distance)
        {
            int format = GetFieldInt(DistanceOutputFormatField, instance, 0);
            if (format == 0)
                return Mathf.RoundToInt(distance).ToString("D4");

            float clamped = Mathf.Clamp(distance, 0f, 999f);
            return clamped.ToString("000.0");
        }

        private static string GetNoDistanceText(TacticalRangeFinderController instance)
        {
            object value = NoDistanceTextField?.GetValue(instance);
            return value as string ?? "----";
        }

        private static void SetDisplayText(TacticalRangeFinderController instance, string text)
        {
            object textField = TextOnDisplayField?.GetValue(instance);
            if (textField == null) return;

            try
            {
                SetMonospaceTextMethod?.Invoke(null, new object[] { textField, text, true });
            }
            catch
            {
                var textProperty = textField.GetType().GetProperty("text");
                textProperty?.SetValue(textField, text, null);
            }
        }

        private static int GetFieldInt(FieldInfo field, TacticalRangeFinderController instance, int fallback)
        {
            if (field == null) return fallback;
            object value = field.GetValue(instance);
            return value != null ? System.Convert.ToInt32(value) : fallback;
        }

        private static float GetFieldFloat(FieldInfo field, TacticalRangeFinderController instance, float fallback)
        {
            if (field == null) return fallback;
            object value = field.GetValue(instance);
            return value is float f ? f : fallback;
        }

        private static int GetMaskBits(TacticalRangeFinderController instance)
        {
            if (MaskField == null) return Physics.DefaultRaycastLayers;

            object value = MaskField.GetValue(instance);
            if (value is LayerMask layerMask)
                return layerMask.value;

            return value is int mask ? mask : Physics.DefaultRaycastLayers;
        }
    }

    internal sealed class ChangeAimingModePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(Player.FirearmController), "ChangeAimingMode");

        [PatchPostfix]
        private static void Postfix()
        {
            if (!Settings.ModEnabled.Value) return;

            PiPDisablerPlugin.DebugLogInfo(
                $"[Patch] ChangeAimingMode frame={Time.frameCount}");
            ScopeLifecycle.CheckAndUpdate("ChangeAimingMode");
            ScopeLifecycle.OnSetScopeMode();
        }
    }

    /// <summary>
    /// Postfix on Player.FirearmController.SetScopeMode(FirearmScopeStateStruct[]).
    /// Fires after EFT applies the new scope/mode state to SightComponent, so
    /// ScopeLifecycle re-applies FOV change immediately.
    /// </summary>
    internal sealed class SetScopeModePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            // FirearmController is an inner class of Player; find SetScopeMode by name and
            // parameter type (FirearmScopeStateStruct[]) to avoid ambiguity.
            var fcType = typeof(Player.FirearmController);
            var method = fcType.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m =>
                    m.Name == "SetScopeMode"
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType.IsArray);

            if (method == null)
                PiPDisablerPlugin.DebugLogInfo("[Patch] SetScopeMode: target method not found");

            return method;
        }

        [PatchPostfix]
        private static void Postfix()
        {
            if (!Settings.ModEnabled.Value) return;

            PiPDisablerPlugin.DebugLogInfo(
                $"[Patch] SetScopeMode frame={Time.frameCount}");
            ScopeLifecycle.OnSetScopeMode();
        }
    }

    /// <summary>
    /// Postfix on Player.OnSetInHands(GEventArgs9).
    /// Slot/weapon switches flow through this path; re-sync scope state so ADS
    /// enter logic does not depend on manual slot toggling.
    /// </summary>
    internal sealed class PlayerOnSetInHandsPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(Player), "OnSetInHands");

        [PatchPostfix]
        private static void Postfix(Player __instance, GEventArgs9 eventArgs)
        {
            if (!Settings.ModEnabled.Value) return;
            if (__instance == null || eventArgs == null || eventArgs.Status != CommandStatus.Succeed) return;

            var localPlayer = Helpers.GetLocalPlayer();
            if (!ReferenceEquals(__instance, localPlayer)) return;

            PiPDisablerPlugin.DebugLogInfo(
                $"[Patch] OnSetInHands frame={Time.frameCount} item='{eventArgs.Item?.TemplateId ?? "null"}'");

            if (ScopeLifecycle.IsScoped)
                ScopeLifecycle.ForceExit();

            ScopeLifecycle.SyncState();
        }
    }
}
