using System;
using System.Collections.Generic;
using EFT.CameraControl;
using UnityEngine;

namespace PiPDisabler
{
    /// <summary>
    /// Makes scope lens surfaces invisible by replacing their live mesh with an empty mesh.
    ///
    /// Why previous approaches failed:
    ///   - renderer.enabled = false:      EFT re-enables it, or CommandBuffer ignores it
    ///   - forceRenderingOff = true:       CommandBuffer/Graphics.DrawMesh bypasses this
    ///   - gameObject.SetActive(false):    EFT re-activates, or object was already inactive
    ///   - gameObject.layer = 31:          Graphics.DrawMesh ignores layers
    ///   - material swap to transparent:   CommandBuffer uses cached material reference
    ///
    /// What works: set MeshFilter.mesh to an EMPTY mesh.
    ///   No vertices = no triangles = nothing to draw.
    ///   CommandBuffer, Graphics.DrawMesh, DrawRenderer ALL need geometry.
    ///   Zero geometry = zero rendering. Period.
    ///
    /// Cached original lens meshes are reused by the reticle stencil pass while ADS.
    /// </summary>
    internal static class LensTransparency
    {
        internal struct LensMaskEntry
        {
            public Renderer Renderer;
            public Mesh Mesh;
        }

        private struct HiddenEntry
        {
            public MeshFilter Filter;
            public SkinnedMeshRenderer Skinned;
            public Mesh OriginalMesh;
            public Renderer Renderer;
            public bool WasForceOff;
        }

        private static readonly List<HiddenEntry> _hidden = new List<HiddenEntry>(16);
        private static Mesh _emptyMesh;

        private static Mesh GetEmptyMesh()
        {
            if (_emptyMesh != null) return _emptyMesh;
            _emptyMesh = new Mesh();
            _emptyMesh.name = "EmptyLensMesh";
            // Zero vertices, zero triangles. Nothing to render.
            return _emptyMesh;
        }

        /// <summary>
        /// Full cleanup for shutdown or mod disable.
        /// </summary>
        public static void FullRestoreAll()
        {
            RestoreAll();
        }

        /// <summary>
        /// Called on scope enter. Finds and empties ALL lens surfaces.
        /// Searches from an expanded root to catch glass/lens meshes above scopeRoot.
        /// </summary>
        public static void HideAllLensSurfaces(OpticSight os)
        {
            if (os == null) return;

            Transform searchRoot = FindScopeSearchRoot(os.transform);

            // Always dump hierarchy on first enter
            DumpHierarchy(searchRoot);

            // Kill only ACTIVE lens renderers (prevents nuking inactive sibling modes on hybrids)
            var allRenderers = searchRoot.GetComponentsInChildren<Renderer>(true);
            int killed = 0;
            foreach (var r in allRenderers)
            {
                if (r == null) continue;
                if (!r.gameObject.activeInHierarchy) continue;
                if (IsLensSurfaceRenderer(r) && !ShouldSkipForCollimator(r))
                {
                    KillMesh(r);
                    killed++;
                }
            }

            // Also kill the specific LensRenderer (belt-and-suspenders)
            try
            {
                var lens = os.LensRenderer;
                if (lens != null && lens.gameObject.activeInHierarchy && !ShouldSkipForCollimator(lens))
                {
                    KillMesh(lens);
                    killed++;
                }
            }
            catch { }

            PiPDisablerPlugin.DebugLogInfo(
                $"[LensTransparency] Destroyed geometry on {killed} lens surfaces (searchRoot='{searchRoot.name}')");
        }

        /// <summary>
        /// Per-frame: re-apply the empty mesh if EFT restores geometry.
        /// Accepts an optional exclusion renderer so this can safely run every frame while scoped.
        /// </summary>
        public static void EnsureHidden(Renderer excludeRenderer = null)
        {
            var emptyMesh = GetEmptyMesh();
            for (int i = 0; i < _hidden.Count; i++)
            {
                var e = _hidden[i];
                if (excludeRenderer != null && e.Renderer == excludeRenderer)
                    continue;

                if (e.Skinned != null && e.Skinned.sharedMesh != emptyMesh)
                {
                    e.Skinned.sharedMesh = emptyMesh;
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[LensTransparency] Re-emptied skinned mesh on '{e.Skinned.gameObject.name}'");
                }
                else if (e.Filter != null && e.Filter.sharedMesh != emptyMesh)
                {
                    e.Filter.sharedMesh = emptyMesh;
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[LensTransparency] Re-emptied mesh on '{e.Filter.gameObject.name}'");
                }
                if (e.Renderer != null)
                {
                    // Re-enforce forceRenderingOff (lightweight bool check, no alloc)
                    if (!e.Renderer.forceRenderingOff)
                        e.Renderer.forceRenderingOff = true;
                }
            }
        }

        /// <summary>
        /// Restore all. Called on scope exit.
        /// </summary>
        public static void RestoreAll()
        {
            if (_hidden.Count == 0) return;

            for (int i = 0; i < _hidden.Count; i++)
            {
                var e = _hidden[i];
                try
                {
                    // Restore original mesh geometry so the lens body is visible again.
                    if (e.Skinned != null && e.OriginalMesh != null)
                    {
                        e.Skinned.sharedMesh = e.OriginalMesh;
                        PiPDisablerPlugin.DebugLogInfo(
                            $"[LensTransparency] Restored skinned mesh on '{e.Skinned.gameObject.name}' → {e.OriginalMesh.vertexCount} verts");
                    }
                    else if (e.Filter != null && e.OriginalMesh != null)
                    {
                        e.Filter.sharedMesh = e.OriginalMesh;
                        PiPDisablerPlugin.DebugLogInfo(
                            $"[LensTransparency] Restored mesh on '{e.Filter.gameObject.name}' → {e.OriginalMesh.vertexCount} verts");
                    }

                    if (e.Renderer != null)
                    {
                        e.Renderer.forceRenderingOff = e.WasForceOff;
                        PiPDisablerPlugin.DebugLogInfo(
                            $"[LensTransparency] Restored renderer '{e.Renderer.gameObject.name}' forceOff={e.WasForceOff}");
                    }
                }
                catch { }
            }

            PiPDisablerPlugin.DebugLogInfo($"[LensTransparency] Restored {_hidden.Count} lens meshes");
            _hidden.Clear();
        }

        // ===== Core =====

        private static void KillMesh(Renderer r)
        {
            if (r == null) return;

            // Already tracked?
            for (int i = 0; i < _hidden.Count; i++)
                if (_hidden[i].Renderer == r) return;

            var mf = r.GetComponent<MeshFilter>();
            var smr = r as SkinnedMeshRenderer;
            Mesh origMesh = null;

            if (smr != null)
            {
                origMesh = smr.sharedMesh;
                smr.sharedMesh = GetEmptyMesh();
            }
            else if (mf != null)
            {
                origMesh = mf.sharedMesh;
                mf.sharedMesh = GetEmptyMesh();
            }

            var entry = new HiddenEntry
            {
                Filter = mf,
                Skinned = smr,
                OriginalMesh = origMesh,
                Renderer = r,
                WasForceOff = r.forceRenderingOff,
            };
            _hidden.Add(entry);

            // Keep renderer enabled state untouched; hybrid sights can manage this
            // dynamically between optic/collimator modes.
            r.forceRenderingOff = true;

            PiPDisablerPlugin.DebugLogInfo(
                $"[LensTransparency] MESH DESTROYED: '{r.gameObject.name}' " +
                $"(had {(origMesh != null ? origMesh.vertexCount.ToString() : "?")} verts → 0) " +
                $"MeshFilter={(mf != null ? "yes" : "NO")}, Skinned={(smr != null ? "yes" : "NO")}");
        }

        /// <summary>
        /// Determines if a Renderer is a lens/glass surface that should be hidden.
        ///
        /// Detection layers:
        ///   1. GameObject name patterns (linza, backlens, back_lens, glass, frontlens, front_linza)
        ///   2. Mesh name patterns (glass, linza)
        ///   3. Shader name patterns (CW FX/OpticSight, CW FX/BackLens)
        ///   4. Material name patterns (linza, glass, lens)
        ///
        /// Uses OrdinalIgnoreCase to avoid ToLowerInvariant() string allocations.
        /// </summary>
        internal static bool IsLensSurfaceRenderer(Renderer r)
        {
            // --- Layer 1: GameObject name ---
            var goName = r.gameObject.name;
            if (!string.IsNullOrEmpty(goName))
            {
                if (ContainsCI(goName, "linza") ||
                    ContainsCI(goName, "backlens") || ContainsCI(goName, "back_lens") ||
                    ContainsCI(goName, "frontlens") || ContainsCI(goName, "front_lens") ||
                    ContainsCI(goName, "front_linza"))
                    return true;

                // "glass" only when it looks like a scope lens (not "fiberglass" etc.)
                if (ContainsCI(goName, "glass") &&
                    (ContainsCI(goName, "lod") || ContainsCI(goName, "scope") || ContainsCI(goName, "optic")))
                    return true;
            }

            // --- Layer 2: Mesh name ---
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                var meshName = mf.sharedMesh.name;
                if (!string.IsNullOrEmpty(meshName))
                {
                    if (ContainsCI(meshName, "linza") ||
                        ContainsCI(meshName, "_glass_") ||
                        ContainsCI(meshName, "_glass_lod"))
                        return true;
                }
            }

            // --- Layer 3 & 4: Shader name + Material name ---
            try
            {
                var mats = r.sharedMaterials;
                if (mats != null)
                {
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null) continue;

                        var shaderName = m.shader?.name ?? "";
                        if (shaderName == "CW FX/OpticSight" ||
                            shaderName == "CW FX/BackLens" ||
                            shaderName.IndexOf("OpticSight", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            shaderName.IndexOf("BackLens", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;

                        // Material name (e.g. "_LOD0_linza", "*_lens*")
                        var matName = m.name ?? "";
                        if (!string.IsNullOrEmpty(matName))
                        {
                            if (ContainsCI(matName, "linza") || ContainsCI(matName, "_lens"))
                                return true;
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        /// <summary>Zero-allocation case-insensitive Contains.</summary>
        private static bool ContainsCI(string haystack, string needle)
        {
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikeLensName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            return ContainsCI(name, "linza")
                   || ContainsCI(name, "lens")
                   || ContainsCI(name, "glass");
        }


        private static Transform FindScopeSearchRoot(Transform t)
        {
            if (t == null) return null;

            // Prefer a parent that contains the mode_* branches (common in hybrid sights)
            Transform best = t;
            Transform cur = t;

            for (int depth = 0; cur != null && depth < 10; depth++, cur = cur.parent)
            {
                string n = cur.name ?? "";

                // Typical container name
                if (ContainsCI(n, "mod_scope"))
                    best = cur;

                // Or a parent that contains mode_* children
                int modeChildren = 0;
                for (int i = 0; i < cur.childCount; i++)
                {
                    var c = cur.GetChild(i);
                    if (c != null && c.name != null &&
                        c.name.StartsWith("mode_", StringComparison.OrdinalIgnoreCase))
                        modeChildren++;
                }
                if (modeChildren >= 1)
                    best = cur;

                // Don’t climb into the whole weapon/player hierarchy
                if (ContainsCI(n, "weapon") || ContainsCI(n, "player") || ContainsCI(n, "hands"))
                    break;
            }

            return best ?? t;
        }

        private static bool ShouldSkipForCollimator(Renderer r)
        {
            if (r == null) return true;

            try
            {
                if (r.GetComponentInParent<CollimatorSight>(true) != null)
                    return true;
            }
            catch { }

            try
            {
                var mats = r.sharedMaterials;
                if (mats != null)
                {
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var shaderName = mats[i]?.shader?.name;
                        if (!string.IsNullOrEmpty(shaderName) &&
                            shaderName.IndexOf("Collimator", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Returns cached lens meshes for the stencil pass while the live lens renderers
        /// stay empty-meshed during ADS.
        /// </summary>
        public static List<LensMaskEntry> CollectLensMaskEntries(OpticSight os)
        {
            var result = new List<LensMaskEntry>();
            if (os == null) return result;

            Transform searchRoot = FindScopeSearchRoot(os.transform);
            for (int i = 0; i < _hidden.Count; i++)
            {
                var entry = _hidden[i];
                if (entry.Renderer == null || entry.OriginalMesh == null) continue;
                if (!entry.Renderer.gameObject.activeInHierarchy) continue;
                if (entry.Renderer.transform != searchRoot && !entry.Renderer.transform.IsChildOf(searchRoot)) continue;

                result.Add(new LensMaskEntry
                {
                    Renderer = entry.Renderer,
                    Mesh = entry.OriginalMesh,
                });

                PiPDisablerPlugin.DebugLogInfo(
                    $"[LensTransparency] LensMask +mesh: go='{entry.Renderer.gameObject.name}'" +
                    $" mesh='{entry.OriginalMesh.name}' verts={entry.OriginalMesh.vertexCount}");
            }

            PiPDisablerPlugin.DebugLogInfo(
                $"[LensTransparency] CollectLensMaskEntries: {result.Count} entry(s)" +
                $" (searchRoot='{searchRoot?.name ?? "null"}' from '{os.name}')");

            return result;
        }

        /// <summary>
        /// Returns all active, non-lens renderers in the scope hierarchy.
        /// These are the scope housing/body meshes that should act as a stencil
        /// mask so the reticle is hidden wherever the housing covers screen-centre.
        /// </summary>
        public static List<Renderer> CollectHousingRenderers(OpticSight os)
        {
            var result = new List<Renderer>();
            if (os == null) return result;

            Transform searchRoot = FindScopeSearchRoot(os.transform);
            var allRenderers = searchRoot.GetComponentsInChildren<Renderer>(true);

            foreach (var r in allRenderers)
            {
                if (r == null) continue;
                if (!r.gameObject.activeInHierarchy) continue;
                if (IsLensSurfaceRenderer(r)) continue;
                if (ShouldSkipForCollimator(r)) continue;

                // Must have real geometry (lens renderers are already empty-meshed, but
                // check explicitly so we don't add zero-vert renderers to the mask pass).
                var mf  = r.GetComponent<MeshFilter>();
                var smr = r as SkinnedMeshRenderer;
                Mesh mesh = mf?.sharedMesh ?? smr?.sharedMesh;
                if (mesh == null || mesh.vertexCount == 0) continue;

                string meshName = mesh.name ?? string.Empty;
                string goName = r.gameObject.name ?? string.Empty;

                if (ContainsCI(meshName, "LOD1"))
                {
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[LensTransparency] HousingMask -skip LOD1 mesh: go='{goName}' mesh='{meshName}'");
                    continue;
                }

                if (!IsForcedScopeBodyMaskRenderer(goName, meshName) &&
                    (LooksLikeLensName(meshName) || LooksLikeLensName(goName)))
                {
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[LensTransparency] HousingMask -skip lens-like mesh: go='{goName}' mesh='{meshName}'");
                    continue;
                }

                result.Add(r);
                PiPDisablerPlugin.DebugLogInfo(
                    $"[LensTransparency] HousingMask +renderer: go='{r.gameObject.name}'" +
                    $" mesh='{mesh.name}' verts={mesh.vertexCount}" +
                    $" shader='{(r.sharedMaterial?.shader?.name ?? "null")}'");
            }

            PiPDisablerPlugin.DebugLogInfo(
                $"[LensTransparency] CollectHousingRenderers: {result.Count} renderer(s)" +
                $" (searchRoot='{searchRoot?.name ?? "null"}' from '{os.name}')");

            return result;
        }

        private static bool IsForcedScopeBodyMaskRenderer(string goName, string meshName)
        {
            return string.Equals(goName, "scope_lens_front.006", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(meshName, "scope_lens_front.006", StringComparison.OrdinalIgnoreCase);
        }

        // ===== Diagnostics =====

        private static bool _dumpedOnce;
        private static void DumpHierarchy(Transform root)
        {
            if (_dumpedOnce)
                return;
            _dumpedOnce = true;

            PiPDisablerPlugin.DebugLogInfo(
                $"[LensTransparency] === SCOPE HIERARCHY DUMP: '{root.name}' ===");

            var allRenderers = root.GetComponentsInChildren<Renderer>(true);
            foreach (var r in allRenderers)
            {
                if (r == null) continue;

                string matInfo = "";
                try
                {
                    var mats = r.sharedMaterials;
                    if (mats != null && mats.Length > 0)
                    {
                        var parts = new List<string>();
                        for (int i = 0; i < mats.Length; i++)
                        {
                            var m = mats[i];
                            if (m == null) { parts.Add("null"); continue; }
                            parts.Add($"{m.name}[shader={m.shader?.name ?? "?"}]");
                        }
                        matInfo = string.Join("; ", parts);
                    }
                }
                catch { matInfo = "(error)"; }

                var mf = r.GetComponent<MeshFilter>();
                int verts = -1;
                string meshName = "";
                if (mf != null && mf.sharedMesh != null)
                {
                    verts = mf.sharedMesh.vertexCount;
                    meshName = mf.sharedMesh.name ?? "";
                }

                bool isLens = IsLensSurfaceRenderer(r);
                string path = GetRelativePath(r.transform, root);

                PiPDisablerPlugin.DebugLogInfo(
                    $"[LensTransparency]   {(isLens ? "★LENS★" : "      ")} " +
                    $"'{path}' mesh='{meshName}' verts={verts} enabled={r.enabled} " +
                    $"active={r.gameObject.activeSelf} layer={r.gameObject.layer} " +
                    $"mats=[{matInfo}]");
            }

            PiPDisablerPlugin.DebugLogInfo(
                $"[LensTransparency] === END DUMP ({allRenderers.Length} renderers) ===");
        }

        private static string GetRelativePath(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (var cur = t; cur != null && cur != root; cur = cur.parent)
                parts.Add(cur.name ?? "?");
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
