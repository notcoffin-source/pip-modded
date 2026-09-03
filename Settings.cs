using BepInEx.Configuration;
using System.Collections.Generic;
using UnityEngine;

namespace PiPDisabler
{
    public static class Settings
    {
        public static ConfigFile Config;
        public static List<ConfigEntryBase> ConfigEntries = new List<ConfigEntryBase>();

        // --- 0. Global ---
        public static ConfigEntry<bool> ModEnabled;
        public static ConfigEntry<KeyCode> ModToggleKey;

        // --- General ---
        public static ConfigEntry<bool> AutoDisableForVariableScopes;
        public static ConfigEntry<string> AutoBypassNameContains;
        public static ConfigEntry<string> ScopeBlacklistNames;
        public static ConfigEntry<KeyCode> ScopeBlacklistToggleEntryKey;
        public static ConfigEntry<string> ScopeWhitelistNames;
        public static ConfigEntry<KeyCode> ScopeWhitelistToggleEntryKey;
        public static ConfigEntry<float> ScopeAlignmentAngleTolerance;
        public static ConfigEntry<float> PostSprintAimGateDuration;
        public static ConfigEntry<bool> BypassDuringStanceTransitions;
        public static ConfigEntry<float> PostStanceAimGateDuration;
        public static ConfigEntry<bool> FOVFixBehaviour;
        public static ConfigEntry<float> GlobalScopeScalingMultiplier;
        public static ConfigEntry<float> GlobalReticleScalingMultiplier;
        
        // --- Optimization ---
        public static ConfigEntry<float> AutoLodBiasMultiplier;
        public static ConfigEntry<bool> KeepScopedLodBiasUntilInventory;

        // --- Mesh Surgery ---
        public static ConfigEntry<float> ReloadBypassModifier;
        public static ConfigEntry<bool> BypassDuringReload;
        public static ConfigEntry<float> PlaneOffsetMeters;
        public static ConfigEntry<string> PlaneNormalAxis;
        public static ConfigEntry<float> Plane1Radius;
        public static ConfigEntry<float> Plane1OffsetMeters;
        public static ConfigEntry<float> Plane2Position;
        public static ConfigEntry<float> Plane2Radius;
        public static ConfigEntry<float> Plane3Position;
        public static ConfigEntry<float> Plane3Radius;
        public static ConfigEntry<float> Plane4Position;
        public static ConfigEntry<float> Plane4Radius;
        public static ConfigEntry<float> CutStartOffset;
        public static ConfigEntry<float> CutLength;
        public static ConfigEntry<float> NearPreserveDepth;
        public static ConfigEntry<float> ReticleBaseSize;
        public static ConfigEntry<float> MeshReticleMinScale;
        public static ConfigEntry<float> MeshReticleMaxScale;
        public static ConfigEntry<float> MeshReticleNormalizedScale;
        public static ConfigEntry<bool> MeshReticleMinimumStrokeEnabled;
        public static ConfigEntry<float> MeshReticleMinimumStrokePixels;
        public static ConfigEntry<bool> ExpandSearchToWeaponRoot;
        public static ConfigEntry<bool> DebugShowHousingMask;

        // --- Custom Mesh Surgery settings (per-scope authoring) ---
        public static ConfigEntry<KeyCode> SaveCustomMeshSurgerySettingsKey;
        public static ConfigEntry<KeyCode> DeleteCustomMeshSurgerySettingsKey;
        public static ConfigEntry<float> CustomPlaneOffsetMeters;
        public static ConfigEntry<float> CustomPlane1Radius;
        public static ConfigEntry<float> CustomPlane1OffsetMeters;
        public static ConfigEntry<float> CustomPlane2Position;
        public static ConfigEntry<float> CustomPlane2Radius;
        public static ConfigEntry<float> CustomPlane3Position;
        public static ConfigEntry<float> CustomPlane3Radius;
        public static ConfigEntry<float> CustomPlane4Position;
        public static ConfigEntry<float> CustomPlane4Radius;
        public static ConfigEntry<float> CustomCutStartOffset;
        public static ConfigEntry<float> CustomCutLength;
        public static ConfigEntry<float> CustomNearPreserveDepth;
        public static ConfigEntry<float> CustomReticleBaseSize;
        public static ConfigEntry<float> CustomReticleSizeMultiplier;
        public static ConfigEntry<float> CustomMeshReticleMinScale;
        public static ConfigEntry<float> CustomMeshReticleMaxScale;
        public static ConfigEntry<float> CustomWeaponScaleMinMagnification;
        public static ConfigEntry<float> CustomWeaponScaleMaxMagnification;
        public static ConfigEntry<float> CustomWeaponScaleMultiplier;
        public static ConfigEntry<float> CustomVisualRecoilCompensation;
        public static ConfigEntry<float> CustomVignetteOpacity;
        public static ConfigEntry<float> CustomVignetteRadius;
        public static ConfigEntry<float> CustomVignetteSoftness;
        public static ConfigEntry<bool> CustomExpandSearchToWeaponRoot;

        // --- Scope Effects ---
        public static ConfigEntry<bool>  VignetteEnabled;
        public static ConfigEntry<float> VignetteOpacity;
        public static ConfigEntry<float> VignetteRadius;
        public static ConfigEntry<float> VignetteSoftness;
        public static ConfigEntry<bool>  ScopeShadowEnabled;
        public static ConfigEntry<bool>  DebugShowScopeShadowMask;
        public static ConfigEntry<bool>  ScopeShadowPersistOnUnscope;
        public static ConfigEntry<float> ScopeShadowOpacity;
        public static ConfigEntry<bool>  OutsideScopeBlurEnabled;
        public static ConfigEntry<int>   OutsideScopeBlurDownsample;
        public static ConfigEntry<int>   OutsideScopeBlurIterations;
        public static ConfigEntry<float> OutsideScopeBlurRadius;
        public static ConfigEntry<float> OutsideScopeBlurOpacity;
        public static ConfigEntry<float> OutsideScopeBlurDarkening;
        public static ConfigEntry<bool>  OutsideScopeBlurRadialGateEnabled;
        public static ConfigEntry<float> OutsideScopeBlurRadialGateStart;
        public static ConfigEntry<float> OutsideScopeBlurRadialGateSoftness;
        public static ConfigEntry<bool>  NvgLensFocalBlurEnabled;
        public static ConfigEntry<float> NvgLensFocalBlurRadiusMultiplier;

        // --- Weapon Scaling ---
        public static ConfigEntry<float> ManualWeaponScale;
        public static ConfigEntry<float> VisualRecoilCompensation;
        // --- Zoom / FOV ---
        public static ConfigEntry<float> BaselineFOV;
        public static ConfigEntry<float> FovAnimationDuration;
        public static ConfigEntry<float> ManualLodBias;
        public static ConfigEntry<bool> SuppressFireModeSwitchMovement;
        public static ConfigEntry<bool> SuppressMagnificationSwitchMovement;
        public static ConfigEntry<bool> ScaleSwayWithCameraFov;
        public static ConfigEntry<float> SwayStrength;
        public static ConfigEntry<bool> ForceRecoilReturnToZero;
        // --- Debug ---
        public static ConfigEntry<bool> DebugLogging;
        public static ConfigEntry<bool> DebugReticleAfterEverything;


        public static void Init(ConfigFile config)
        {
            // --- General ---
            ConfigEntries.Add(ModEnabled = config.Bind("General", "Mod Enabled", true,
                new ConfigDescription(
                    "Master ON/OFF switch for the entire mod.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = false})));
            ConfigEntries.Add(ModToggleKey = config.Bind("General", "Mod Toggle Key", KeyCode.None,
                new ConfigDescription(
                    "Toggle key for master mod enable/disable.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = false})));
            ConfigEntries.Add(AutoDisableForVariableScopes = config.Bind("General", "Auto Disable For NV/Thermals", true,
                new ConfigDescription(
                    "Automatically disable the mod for thermal/night vision scopes.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(AutoBypassNameContains = config.Bind("General", "Auto Bypass Name Contains", "d-evo; scope_ags_npz_pag17_2,7x",
                new ConfigDescription(
                    "Semi-colon separated list. Any scope whose object name or scope key contains one of these gets bypassed",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(ScopeBlacklistNames = config.Bind("General", "Scope Blacklist Names", "",
                new ConfigDescription(
                    "Semi-colon separated list of scope keys that should bypass the mod.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(ScopeBlacklistToggleEntryKey = config.Bind("General", "Scope Blacklist Toggle Entry Key", KeyCode.None,
                new ConfigDescription(
                    "When pressed while scoped, add/remove the current scope to the blacklist.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(ScopeWhitelistNames = config.Bind("General", "Scope Whitelist Names", "",
                new ConfigDescription(
                    "Semi-colon separated list of allowed scopes. Empty list = whitelist ignored.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(ScopeWhitelistToggleEntryKey = config.Bind("General", "Scope Whitelist Toggle Entry Key", KeyCode.None,
                new ConfigDescription(
                    "When pressed while scoped, add/remove the current scope to the whitelist.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(FOVFixBehaviour = config.Bind("General", "FOV Fix Behaviour", false,
                new ConfigDescription(
                    "If this is enabled, 1x FOV will be set to the game settings FOV",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(GlobalScopeScalingMultiplier = config.Bind("General", "Global Scope Scaling Multiplier", 1f,
                new ConfigDescription(
                    "Multiplies the scaling of all scopes, useful if you feel like the scopes are too small or too large.",
                    new AcceptableValueRange<float>(0.25f, 3f),
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(GlobalReticleScalingMultiplier = config.Bind("General", "Global Reticle Scaling Multiplier", 1f,
                new ConfigDescription(
                    "Multiplies the scaling of all reticles, useful if you feel like the reticles are too small or too large.",
                    new AcceptableValueRange<float>(0.5f, 2f),
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(ScopeAlignmentAngleTolerance = config.Bind("Hacks", "ADS Scope Alignment Angle Tolerance", 2f,
                new ConfigDescription(
                    "Maximum angle in degrees between the active scope aim axis and the main camera before the mod activates after sprinting or stance transitions.",
                    new AcceptableValueRange<float>(0f, 15f),
                    new ConfigurationManagerAttributes { IsAdvanced = true, ShowRangeAsPercent = false })));
            ConfigEntries.Add(PostSprintAimGateDuration = config.Bind("Hacks", "Post Sprint ADS Gate Duration", 1f,
                new ConfigDescription(
                    "How long after sprinting the scope alignment gate should be enforced.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = true, ShowRangeAsPercent = false })));
            ConfigEntries.Add(BypassDuringStanceTransitions = config.Bind("Hacks", "Bypass During Stance Transitions", true,
                new ConfigDescription(
                    "Bypass the mod while changing between standing and prone.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(PostStanceAimGateDuration = config.Bind("Hacks", "Post Stance ADS Gate Duration", 1f,
                new ConfigDescription(
                    "How long after standing/prone transitions the scope alignment gate should be enforced.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = true, ShowRangeAsPercent = false })));

            // --- Zoom ---
            ConfigEntries.Add(BaselineFOV = config.Bind("General", "Baseline FOV", 35f,
                new ConfigDescription(
                    "What to use when calculating magnified FOV (if set to 50 then 2x will be 25°).\n" +
                    "Be aware that 1x is always forced to 35° for stepped optics.",
                    new AcceptableValueRange<float>(20f, 35f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(FovAnimationDuration = config.Bind("General", "FOV Animation Duration", 1f,
                new ConfigDescription(
                    "Duration of the FOV transitions during magnification changes.",
                    new AcceptableValueRange<float>(0f, 10f),
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(BypassDuringReload = config.Bind("Hacks", "Bypass during reload", true,
                new ConfigDescription(
                    "Bypass the mod while reloading",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(ReloadBypassModifier = config.Bind("Hacks", "Reload Bypass Modifier", 0.2f,
                new ConfigDescription(
                    "Changes the duration of the reload bypass, higher values lower the duration.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = true, ShowRangeAsPercent = false })));
            ConfigEntries.Add(ScaleSwayWithCameraFov = config.Bind("Hacks", "Scale Sway With Camera FOV", true,
                new ConfigDescription(
                    "Lowers weapon sway while scoped in proportion to the current FOV.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(SwayStrength = config.Bind("Hacks", "Sway Modifier", 0.3f,
                new ConfigDescription(
                    "Changes the intensity of the sway reduction.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = true, ShowRangeAsPercent = true })));
            ConfigEntries.Add(ManualWeaponScale = config.Bind("Hacks", "Manual Weapon Scale", 1f, //To remove
                new ConfigDescription(
                    "Direct ribcage compensation scale while scoped. Higher values make the weapon appear smaller.",
                    new AcceptableValueRange<float>(0.1f, 5f),
                    new ConfigurationManagerAttributes { IsAdvanced = true, ShowRangeAsPercent = false })));
            ConfigEntries.Add(VisualRecoilCompensation = config.Bind("Hacks", "Visual Recoil Compensation", 0f, //To remove
                new ConfigDescription(
                    "Render-only screen-plane camera offset toward the optic camera for scope recoil/alignment at low FOV. Negative values invert direction. 0 = disabled.",
                    new AcceptableValueRange<float>(-2f, 2f),
                    new ConfigurationManagerAttributes { IsAdvanced = true, ShowRangeAsPercent = false })));
            ConfigEntries.Add(KeepScopedLodBiasUntilInventory = config.Bind("Graphics", "Keep scoped LOD Bias until inventory is opened", false,
                new ConfigDescription(
                    "When enabled, the scoped LOD bias stays active after leaving the scope and is only restored when opening inventory/loot or after a successful inventory item transfer.\n" +
                    "When disabled, LOD bias is restored immediately on scope exit.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(ManualLodBias = config.Bind("Graphics", "Manual LOD Bias", 0f,
                new ConfigDescription(
                    "Manual LOD bias while scoped.\n" +
                    "0 = auto (Magnification * Auto LOD bias multiplier). \n High values will impact performance !",
                    new AcceptableValueRange<float>(0f, 20f),
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(AutoLodBiasMultiplier = config.Bind("Graphics", "Auto LOD bias multiplier", 0.5f,
                new ConfigDescription(
                    "Self explanatory \n High values will impact performance !",
                    new AcceptableValueRange<float>(0.01f, 20f),
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(SuppressFireModeSwitchMovement = config.Bind("Hacks", "Suppress Fire Mode Switch Movement", true,
                new ConfigDescription(
                    "Prevents the weapon from playing the fire-mode switch movement animation while still changing fire mode.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(SuppressMagnificationSwitchMovement = config.Bind("Hacks", "Suppress Magnification Switch Movement", true,
                new ConfigDescription(
                    "Prevents the weapon from playing the scope magnification switch movement animation while still changing magnification.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(ForceRecoilReturnToZero = config.Bind("Hacks", "Force Recoil Return To Zero", true,
                new ConfigDescription(
                    "Forces the weapon's post-recoil hand rotation rest point back to zero instead of using Tarkov's small randomized offset.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));

            // --- Mesh Surgery (ON by default, Cylinder mode) --- //Needs global cleanup, config name standardization.
            ConfigEntries.Add(PlaneOffsetMeters = config.Bind("Global Mesh Surgery settings", "PlaneOffsetMeters", 0.001f,
                new ConfigDescription(
                    "Offset applied along plane normal (meters).",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(Plane1Radius = config.Bind("Global Mesh Surgery settings", "Plane1Radius", 0.011f,
                new ConfigDescription(
                    "Near radius (meters) of the cylindrical/cone cut (camera side).\n" +
                    "Typical scope lens radius is ~0.01-0.02m.",
                    new AcceptableValueRange<float>(0.001f, 0.1f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(Plane1OffsetMeters = config.Bind("Global Mesh Surgery settings", "Plane1OffsetMeters", 0f,
                new ConfigDescription(
                    "Offset (meters) for plane 1 from linza/backLens origin along bore axis.",
                    new AcceptableValueRange<float>(-0.02f, 0.02f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(Plane2Position = config.Bind("Global Mesh Surgery settings", "Plane2Position", 0.1138498f,
                new ConfigDescription(
                    "Plane 2 profile position (0..1) anchored from the near side.\n" +
                    "Changing CutLength keeps this plane at the same world-space depth from near.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(Plane2Radius = config.Bind("Global Mesh Surgery settings", "Plane2Radius", 0.0186338f,
                new ConfigDescription(
                    "Radius (meters) at plane 2.",
                    new AcceptableValueRange<float>(0f, 0.2f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(Plane3Position = config.Bind("Global Mesh Surgery settings", "Plane3Position", 0.55f,
                new ConfigDescription(
                    "Normalized depth of plane 3 from near (0) to far (1).",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(Plane3Radius = config.Bind("Global Mesh Surgery settings", "Plane3Radius", 0.2f,
                new ConfigDescription(
                    "Radius (meters) at plane 3.",
                    new AcceptableValueRange<float>(0f, 0.2f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(Plane4Position = config.Bind("Global Mesh Surgery settings", "Plane4Position", 1f,
                new ConfigDescription(
                    "Normalized depth of plane 4 from near (0) to far (1). Usually 1.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(Plane4Radius = config.Bind("Global Mesh Surgery settings", "Plane4Radius", 0.2f,
                new ConfigDescription(
                    "Radius (meters) at plane 4 (away from player).",
                    new AcceptableValueRange<float>(0f, 0.2f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(CutStartOffset = config.Bind("Global Mesh Surgery settings", "CutStartOffset", 0.04084507f,
                new ConfigDescription(
                    "How far behind the backLens (toward the camera) the near cut plane starts.\n" +
                    "The near plane is fixed at: backLens - (offset × boreAxis).\n" +
                    "0 = starts exactly at the backLens. 0.05 = 5cm behind it (catches interior tube geometry).\n" +
                    "Changing CutLength does NOT move this plane.",
                    new AcceptableValueRange<float>(0f, 0.3f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(CutLength = config.Bind("Global Mesh Surgery settings", "CutLength", 0.755493f,
                new ConfigDescription(
                    "How far forward from the near plane the cut extends (toward the objective).\n" +
                    "The far plane is at: nearPlane + (length × boreAxis).\n" +
                    "Only the far plane moves when you change this value.",
                    new AcceptableValueRange<float>(0.01f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(NearPreserveDepth = config.Bind("Global Mesh Surgery settings", "NearPreserveDepth", 0.02549295f,
                new ConfigDescription(
                    "Depth (meters) from the near cut plane where NO geometry is cut.\n" +
                    "Preserves the eyepiece housing closest to the camera so you\n" +
                    "keep a thin ring framing the reticle while ADSing.\n" +
                    "0.01 = 1cm ring (default). 0.02-0.04 = thicker ring.\n" +
                    "0 = disabled (cut starts at the near plane — removes everything).",
                    new AcceptableValueRange<float>(0f, 0.15f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(ReticleBaseSize = config.Bind("Global Mesh Surgery settings", "Reticle Base Size", 0.030f,
                new ConfigDescription(
                    "Size of the reticle",
                    new AcceptableValueRange<float>(0f, 0.2f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(MeshReticleMinScale = config.Bind("Global Mesh Surgery settings", "Mesh Reticle Min Scale", 0f,
                new ConfigDescription(
                    "Minimum final mesh reticle scale after normalization and zoom. 0 = disabled.",
                    new AcceptableValueRange<float>(0f, 20f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(MeshReticleMaxScale = config.Bind("Global Mesh Surgery settings", "Mesh Reticle Max Scale", 0f,
                new ConfigDescription(
                    "Maximum final mesh reticle scale after normalization and zoom. 0 = disabled.",
                    new AcceptableValueRange<float>(0f, 20f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(MeshReticleNormalizedScale = config.Bind("Global Mesh Surgery settings", "Mesh Reticle Normalized Scale", 36.67718f,
                new ConfigDescription(
                    "Multiplier applied after mesh reticle bounds normalization.",
                    new AcceptableValueRange<float>(0.01f, 100f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(MeshReticleMinimumStrokeEnabled = config.Bind("Global Mesh Surgery settings", "Mesh Reticle Minimum Stroke Enabled", true,
                new ConfigDescription(
                    "Draws mesh reticles with a small screen-space dilation so thin details survive downscaling.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(MeshReticleMinimumStrokePixels = config.Bind("Global Mesh Surgery settings", "Mesh Reticle Minimum Stroke Pixels", 1.963615f,
                new ConfigDescription(
                    "Approximate minimum visible thickness for mesh reticles, in screen pixels.",
                    new AcceptableValueRange<float>(0f, 4f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(ExpandSearchToWeaponRoot = config.Bind("Global Mesh Surgery settings", "ExpandSearchToWeaponRoot", true, //Needs to be hardset
                new ConfigDescription(
                    "Make the meshcutter run for the whole weapon.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(DebugShowHousingMask = config.Bind("Debug", "DebugShowHousingMask", false,
                new ConfigDescription(
                    "Render a red overlay wherever the lens stencil mask is.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));

            // --- Custom Mesh Surgery settings ---
            ConfigEntries.Add(SaveCustomMeshSurgerySettingsKey = config.Bind("Per scope settings", "Save custom settings key", KeyCode.None,
                new ConfigDescription(
                    "When pressed while scoped, saves all values from this category for the active scope key into custom_mesh_surgery_settings.json.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(DeleteCustomMeshSurgerySettingsKey = config.Bind("Per scope settings", "DeleteCustomMeshSurgerySettingsKey", KeyCode.None,
                new ConfigDescription(
                    "When pressed while scoped, delete custom mesh surgery settings for the active scope key from custom_mesh_surgery_settings.json.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(CustomPlaneOffsetMeters = config.Bind("Per scope settings", "PlaneOffsetMeters", 0.001f,
                new ConfigDescription(
                    "Custom per-scope plane offset applied along plane normal (meters).",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(CustomPlane1Radius = config.Bind("Per scope settings", "Plane1Radius", 0.011f,
                new ConfigDescription(
                    "Custom per-scope near radius in meters.",
                    new AcceptableValueRange<float>(0.001f, 0.1f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(CustomPlane1OffsetMeters = config.Bind("Per scope settings", "Plane1OffsetMeters", 0f,
                new ConfigDescription(
                    "Custom per-scope plane 1 offset in meters.",
                    new AcceptableValueRange<float>(-0.02f, 0.02f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(CustomPlane2Position = config.Bind("Per scope settings", "Plane2Position", 0.1138498f,
                new ConfigDescription(
                    "Custom per-scope plane 2 position (0..1), anchored from near when CutLength changes.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(CustomPlane2Radius = config.Bind("Per scope settings", "Plane2Radius", 0.0186338f,
                new ConfigDescription(
                    "Custom per-scope plane 2 radius in meters.",
                    new AcceptableValueRange<float>(0f, 0.2f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(CustomPlane3Position = config.Bind("Per scope settings", "Plane3Position", 0.55f,
                new ConfigDescription(
                    "Custom per-scope plane 3 depth (0..1).",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(CustomPlane3Radius = config.Bind("Per scope settings", "Plane3Radius", 0.2f,
                new ConfigDescription(
                    "Custom per-scope plane 3 radius in meters.",
                    new AcceptableValueRange<float>(0f, 0.2f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(CustomPlane4Position = config.Bind("Per scope settings", "Plane4Position", 1f,
                new ConfigDescription(
                    "Custom per-scope plane 4 depth (0..1).",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(CustomPlane4Radius = config.Bind("Per scope settings", "Plane4Radius", 0.2f,
                new ConfigDescription(
                    "Custom per-scope plane 4 radius in meters.",
                    new AcceptableValueRange<float>(0f, 0.2f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(CustomCutStartOffset = config.Bind("Per scope settings", "CutStartOffset", 0.04084507f,
                new ConfigDescription(
                    "Custom per-scope cut start offset in meters.",
                    new AcceptableValueRange<float>(-0.2f, 0.2f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(CustomCutLength = config.Bind("Per scope settings", "CutLength", 0.755493f,
                new ConfigDescription(
                    "Custom per-scope cut length in meters.",
                    new AcceptableValueRange<float>(0.01f, 4f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(CustomNearPreserveDepth = config.Bind("Per scope settings", "NearPreserveDepth", 0.02549295f,
                new ConfigDescription(
                    "Custom per-scope near preserve depth in meters.",
                    new AcceptableValueRange<float>(0f, 0.2f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(CustomReticleBaseSize = config.Bind("Per scope settings", "Reticle Size", 0.030f,
                new ConfigDescription(
                    "Per-scope reticle size for scopes with a non scaling reticle. Needs to be saved to take effect.",
                    new AcceptableValueRange<float>(0f, 0.2f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(CustomReticleSizeMultiplier = config.Bind("Per scope settings", "Reticle Size Multiplier", 1f,
                new ConfigDescription(
                    "Multiplier applied to the per-scope reticle size. 1 = unchanged. Needs to be saved to take effect.",
                    new AcceptableValueRange<float>(0.01f, 10f),
                    new ConfigurationManagerAttributes { IsAdvanced = false, ShowRangeAsPercent = false })));
            ConfigEntries.Add(CustomMeshReticleMinScale = config.Bind("Per scope settings", "Variable Reticle Minimum Size", 0f,
                new ConfigDescription(
                    "Custom per-scope smallest reticle size. If either max or min is set to 0 then this doesn't do anything. Needs to be saved to take effect.",
                    new AcceptableValueRange<float>(0f, 20f),
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(CustomMeshReticleMaxScale = config.Bind("Per scope settings", "Variable Reticle Maximum Size", 0f,
                new ConfigDescription(
                    "Custom per-scope biggest reticle size. If either max or min is set to 0 then this doesn't do anything. Needs to be saved to take effect.",
                    new AcceptableValueRange<float>(0f, 20f),
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(CustomWeaponScaleMinMagnification = config.Bind("Per scope settings", "Weapon Scale Min Magnification", 1f,
                new ConfigDescription(
                    "Custom per-scope ribcage scale at minimum magnification. 0 = use global Manual Weapon Scale. Needs to be saved to take effect. Higher values make the weapon appear smaller.",
                    new AcceptableValueRange<float>(0f, 50f),
                    new ConfigurationManagerAttributes { IsAdvanced = true, ShowRangeAsPercent = false })));
            ConfigEntries.Add(CustomWeaponScaleMaxMagnification = config.Bind("Per scope settings", "Weapon Scale Max Magnification", 5f,
                new ConfigDescription(
                    "Custom per-scope ribcage scale at maximum magnification. 0 = use global Manual Weapon Scale. Needs to be saved to take effect. Higher values make the weapon appear smaller.",
                    new AcceptableValueRange<float>(0f, 50f),
                    new ConfigurationManagerAttributes { IsAdvanced = true, ShowRangeAsPercent = false })));
            ConfigEntries.Add(CustomWeaponScaleMultiplier = config.Bind("Per scope settings", "Weapon Scale Multiplier", 1f,
                new ConfigDescription(
                    "Multiplier applied to both per-scope weapon scale limits. 1 = unchanged. Needs to be saved to take effect.",
                    new AcceptableValueRange<float>(0.01f, 10f),
                    new ConfigurationManagerAttributes { IsAdvanced = false, ShowRangeAsPercent = false })));
            ConfigEntries.Add(CustomVisualRecoilCompensation = config.Bind("Per scope settings", "Visual Recoil Compensation", 0f,
                new ConfigDescription(
                    "Custom per-scope visual recoil compensation. 0 = use global Visual Recoil Compensation. Needs to be saved to take effect. Negative values invert direction.",
                    new AcceptableValueRange<float>(-2f, 2f),
                    new ConfigurationManagerAttributes { IsAdvanced = true, ShowRangeAsPercent = false })));
            ConfigEntries.Add(CustomVignetteOpacity = config.Bind("Per scope settings", "Vignette Opacity", 0f,
                new ConfigDescription(
                    "Custom per-scope vignette opacity. 0 = use Scope Effects default.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(CustomVignetteRadius = config.Bind("Per scope settings", "Vignette Radius", 0f,
                new ConfigDescription(
                    "Custom per-scope vignette radius. 0 = use Scope Effects default.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(CustomVignetteSoftness = config.Bind("Per scope settings", "Vignette Softness", 0f,
                new ConfigDescription(
                    "Custom per-scope vignette softness. 0 = use Scope Effects default.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(CustomExpandSearchToWeaponRoot = config.Bind("Per scope settings", "ExpandSearchToWeaponRoot", true,
                new ConfigDescription(
                    "Custom per-scope search root expansion to Weapon_root.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));

            // --- Scope Effects ---
            ConfigEntries.Add(VignetteEnabled = config.Bind("Scope Effects", "Vignette", true,
                new ConfigDescription(
                    "Render a circular vignette ring around the scope aperture.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(VignetteOpacity = config.Bind("Scope Effects", "Vignette Opacity", 1f,
                new ConfigDescription(
                    "Maximum opacity of the lens vignette ring (0=invisible, 1=full black).",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(VignetteRadius = config.Bind("Scope Effects", "Vignette Radius", 0.47f,
                new ConfigDescription(
                    "Inner clear radius of the lens vignette (0=center starts darkening, 0.5=outer half darkened, 1=edge only).",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(VignetteSoftness = config.Bind("Scope Effects", "Vignette Softness", 0.80f,
                new ConfigDescription(
                    "Fraction of the vignette radius used for the gradient falloff (0=hard edge, 1=full gradient).",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = false })));

            ConfigEntries.Add(ScopeShadowEnabled = config.Bind("Scope Effects", "Scope Shadow", false,
                new ConfigDescription(
                    "Overlay a fullscreen black shadow everywhere except the lens mask.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(DebugShowScopeShadowMask = config.Bind("Debug", "DebugShowScopeShadowMask", false,
                new ConfigDescription(
                    "Render the shadow lens mask as a green overlay for debugging.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(ScopeShadowPersistOnUnscope = config.Bind("Scope Effects", "ScopeShadow Persist On Unscope", false,
                new ConfigDescription(
                    "Keep the scope shadow visible after leaving ADS until the FOV restore finishes, useful when FOV animation set to 0.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(ScopeShadowOpacity = config.Bind("Scope Effects", "ScopeShadow Opacity", 0.82f,
                new ConfigDescription(
                    "Maximum opacity of the scope shadow overlay.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(OutsideScopeBlurEnabled = config.Bind("General", "Depth of field", true,
                new ConfigDescription(
                    "Apply a masked dual Kawase blur outside the visible scope lens.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(OutsideScopeBlurDownsample = config.Bind("Scope Effects", "Depth of field Blur Downsample", 1,
                new ConfigDescription(
                    "Downsample divisor for outside-scope blur. Higher values are faster but softer/blockier.",
                    new AcceptableValueRange<int>(1, 4),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(OutsideScopeBlurIterations = config.Bind("Scope Effects", "Depth of field Blur Iterations", 3,
                new ConfigDescription(
                    "Dual Kawase chain depth outside the scope lens.",
                    new AcceptableValueRange<int>(1, 6),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(OutsideScopeBlurRadius = config.Bind("Scope Effects", "Depth of field Blur Radius", 2f,
                new ConfigDescription(
                    "Dual Kawase sample offset in current-pass pixels.",
                    new AcceptableValueRange<float>(0.25f, 6f),
                    new ConfigurationManagerAttributes { IsAdvanced = true, ShowRangeAsPercent = false })));
            ConfigEntries.Add(OutsideScopeBlurOpacity = config.Bind("Scope Effects", "Depth of field Blur Opacity", 1f,
                new ConfigDescription(
                    "Blend strength of the blurred outside-scope image.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(OutsideScopeBlurDarkening = config.Bind("Scope Effects", "Depth of field Blur Darkening", 0.3f,
                new ConfigDescription(
                    "Extra darkening applied to the blurred outside-scope image.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(OutsideScopeBlurRadialGateEnabled = config.Bind("Scope Effects", "Depth of field Blur Radial Gate", true,
                new ConfigDescription(
                    "Fade in outside-scope blur by screen distance from the lens center.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            ConfigEntries.Add(OutsideScopeBlurRadialGateStart = config.Bind("Scope Effects", "Depth of field Blur Radial Gate Start", 3f,
                new ConfigDescription(
                    "Distance from lens center, in lens radii, where outside-scope blur starts.",
                    new AcceptableValueRange<float>(0.5f, 3f),
                    new ConfigurationManagerAttributes { IsAdvanced = true, ShowRangeAsPercent = false })));
            ConfigEntries.Add(OutsideScopeBlurRadialGateSoftness = config.Bind("Scope Effects", "Depth of field Blur Radial Gate Softness", 0.6f,
                new ConfigDescription(
                    "Fade width for the radial blur gate, in lens radii.",
                    new AcceptableValueRange<float>(0.01f, 2f),
                    new ConfigurationManagerAttributes { IsAdvanced = true, ShowRangeAsPercent = false })));
            ConfigEntries.Add(NvgLensFocalBlurEnabled = config.Bind("General", "NVG Lens Blur", false,
                new ConfigDescription(
                    "Blurs the image in the lens to mimic IRL NVG behaviour.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(NvgLensFocalBlurRadiusMultiplier = config.Bind("Scope Effects", "NVG Lens Blur Multiplier", 2.5f,
                new ConfigDescription(
                    "Multiplier for the NVG lens blur, higher means more blurrr.",
                    new AcceptableValueRange<float>(1f, 6f),
                    new ConfigurationManagerAttributes { IsAdvanced = false, ShowRangeAsPercent = false })));
            // --- Debug ---
            ConfigEntries.Add(DebugLogging = config.Bind("Debug", "Debug logging", false,
                new ConfigDescription(
                    "Enable verbose diagnostic logging.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = false })));
            ConfigEntries.Add(DebugReticleAfterEverything = config.Bind("Debug", "Draw reticle after everything", false,
                new ConfigDescription(
                    "Legacy setting. Reticles now always draw after everything and use the after-NVG reticle shader.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));
            RegisterVisualEffectLiveUpdates();
            RecalcOrder();
        }

        private static void RegisterVisualEffectLiveUpdates()
        {
            CustomVignetteOpacity.SettingChanged += OnCustomVisualEffectSettingChanged;
            CustomVignetteRadius.SettingChanged += OnCustomVisualEffectSettingChanged;
            CustomVignetteSoftness.SettingChanged += OnCustomVisualEffectSettingChanged;

            VignetteEnabled.SettingChanged += OnVisualEffectSettingChanged;
            VignetteOpacity.SettingChanged += OnVisualEffectSettingChanged;
            VignetteRadius.SettingChanged += OnVisualEffectSettingChanged;
            VignetteSoftness.SettingChanged += OnVisualEffectSettingChanged;
            ScopeShadowEnabled.SettingChanged += OnVisualEffectSettingChanged;
            ScopeShadowOpacity.SettingChanged += OnVisualEffectSettingChanged;
            OutsideScopeBlurEnabled.SettingChanged += OnVisualEffectSettingChanged;
            OutsideScopeBlurDownsample.SettingChanged += OnVisualEffectSettingChanged;
            OutsideScopeBlurIterations.SettingChanged += OnVisualEffectSettingChanged;
            OutsideScopeBlurRadius.SettingChanged += OnVisualEffectSettingChanged;
            OutsideScopeBlurOpacity.SettingChanged += OnVisualEffectSettingChanged;
            OutsideScopeBlurDarkening.SettingChanged += OnVisualEffectSettingChanged;
            OutsideScopeBlurRadialGateEnabled.SettingChanged += OnVisualEffectSettingChanged;
            OutsideScopeBlurRadialGateStart.SettingChanged += OnVisualEffectSettingChanged;
            OutsideScopeBlurRadialGateSoftness.SettingChanged += OnVisualEffectSettingChanged;
            NvgLensFocalBlurEnabled.SettingChanged += OnVisualEffectSettingChanged;
            NvgLensFocalBlurRadiusMultiplier.SettingChanged += OnVisualEffectSettingChanged;
        }

        private static void OnCustomVisualEffectSettingChanged(object sender, System.EventArgs args)
        {
            PerScopeMeshSurgerySettings.SaveActiveScopeVisualSettings();
            ScopeEffectsRenderer.RefreshSettings();
        }

        private static void OnVisualEffectSettingChanged(object sender, System.EventArgs args)
        {
            ScopeEffectsRenderer.RefreshSettings();
        }

        private static void RecalcOrder()
        {
            // Set the Order field for all settings, to avoid unnecessary changes when adding new settings
            int settingOrder = ConfigEntries.Count;
            foreach (var entry in ConfigEntries)
            {
                ConfigurationManagerAttributes attributes = entry.Description.Tags[0] as ConfigurationManagerAttributes;
                if (attributes != null)
                {
                    attributes.Order = settingOrder;
                }

                settingOrder--;
            }
        }

    }
}
