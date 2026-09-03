using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PiPDisabler
{
    public static class MeshPlaneCutter
    {
        public enum KeepSide { Positive, Negative }

        public static Mesh MakeReadableMeshCopy(Mesh nonReadableMesh)
        {
            if (nonReadableMesh == null) return null;

            Mesh meshCopy = new Mesh();
            meshCopy.indexFormat = nonReadableMesh.indexFormat;

            // Copy vertex buffer from GPU
            GraphicsBuffer verticesBuffer = nonReadableMesh.GetVertexBuffer(0);
            int totalSize = verticesBuffer.stride * verticesBuffer.count;
            byte[] data = new byte[totalSize];
            verticesBuffer.GetData(data);
            meshCopy.SetVertexBufferParams(nonReadableMesh.vertexCount, nonReadableMesh.GetVertexAttributes());
            meshCopy.SetVertexBufferData(data, 0, 0, totalSize);
            verticesBuffer.Release();

            // Copy index buffer from GPU
            meshCopy.subMeshCount = nonReadableMesh.subMeshCount;
            GraphicsBuffer indexesBuffer = nonReadableMesh.GetIndexBuffer();
            int tot = indexesBuffer.stride * indexesBuffer.count;
            byte[] indexesData = new byte[tot];
            indexesBuffer.GetData(indexesData);
            meshCopy.SetIndexBufferParams(indexesBuffer.count, nonReadableMesh.indexFormat);
            meshCopy.SetIndexBufferData(indexesData, 0, 0, tot);
            indexesBuffer.Release();

            // Restore submesh structure
            uint currentIndexOffset = 0;
            for (int i = 0; i < meshCopy.subMeshCount; i++)
            {
                uint subMeshIndexCount = nonReadableMesh.GetIndexCount(i);
                meshCopy.SetSubMesh(i, new SubMeshDescriptor((int)currentIndexOffset, (int)subMeshIndexCount));
                currentIndexOffset += subMeshIndexCount;
            }

            meshCopy.RecalculateNormals();
            meshCopy.RecalculateBounds();

            return meshCopy;
        }

        public static bool CutMeshDirect(
            Mesh mesh,
            Transform meshTransform,
            Vector3 planePointWorld,
            Vector3 planeNormalWorld,
            KeepSide keepSide,
            float epsilon = 1e-5f)
        {
            if (mesh == null) return false;

            Vector3 pL = meshTransform.InverseTransformPoint(planePointWorld);
            Vector3 nL = meshTransform.InverseTransformDirection(planeNormalWorld).normalized;

            var verts = mesh.vertices;
            var norms = mesh.normals;
            var tangs = mesh.tangents;
            var uvs   = mesh.uv;

            bool hasNormals  = norms != null && norms.Length == verts.Length;
            bool hasTangents = tangs != null && tangs.Length == verts.Length;
            bool hasUV       = uvs   != null && uvs.Length   == verts.Length;

            int subMeshCount = mesh.subMeshCount;

            var outVerts = new List<Vector3>(verts.Length);
            var outNorms = hasNormals  ? new List<Vector3>(verts.Length) : null;
            var outTangs = hasTangents ? new List<Vector4>(verts.Length) : null;
            var outUVs   = hasUV       ? new List<Vector2>(verts.Length) : null;

            var keptMap = new Dictionary<int, int>(verts.Length);

            var outTris = new List<int>[subMeshCount];
            for (int s = 0; s < subMeshCount; s++)
                outTris[s] = new List<int>(mesh.GetTriangles(s).Length);

            float SideValue(Vector3 v) => Vector3.Dot(nL, v - pL);

            int AddVertexFromOld(int oldIndex)
            {
                if (keptMap.TryGetValue(oldIndex, out int ni))
                    return ni;

                int newIndex = outVerts.Count;
                outVerts.Add(verts[oldIndex]);
                if (hasNormals) outNorms.Add(norms[oldIndex]);
                if (hasTangents) outTangs.Add(tangs[oldIndex]);
                if (hasUV) outUVs.Add(uvs[oldIndex]);
                keptMap[oldIndex] = newIndex;
                return newIndex;
            }

            int AddInterpolatedVertex(int a, int b, float t01)
            {
                int newIndex = outVerts.Count;
                outVerts.Add(Vector3.LerpUnclamped(verts[a], verts[b], t01));
                if (hasNormals)
                    outNorms.Add(Vector3.SlerpUnclamped(norms[a], norms[b], t01).normalized);
                if (hasTangents)
                    outTangs.Add(Vector4.LerpUnclamped(tangs[a], tangs[b], t01));
                if (hasUV)
                    outUVs.Add(Vector2.LerpUnclamped(uvs[a], uvs[b], t01));
                return newIndex;
            }

            bool Keep(float side) =>
                keepSide == KeepSide.Positive ? side >= -epsilon : side <= epsilon;

            for (int s = 0; s < subMeshCount; s++)
            {
                int[] tris = mesh.GetTriangles(s);
                for (int i = 0; i < tris.Length; i += 3)
                {
                    int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
                    float d0 = SideValue(verts[i0]);
                    float d1 = SideValue(verts[i1]);
                    float d2 = SideValue(verts[i2]);
                    bool k0 = Keep(d0), k1 = Keep(d1), k2 = Keep(d2);
                    int keptCount = (k0 ? 1 : 0) + (k1 ? 1 : 0) + (k2 ? 1 : 0);

                    if (keptCount == 3)
                    {
                        outTris[s].Add(AddVertexFromOld(i0));
                        outTris[s].Add(AddVertexFromOld(i1));
                        outTris[s].Add(AddVertexFromOld(i2));
                    }
                    else if (keptCount == 0)
                    {
                        continue;
                    }
                    else
                    {
                        int[] idx = { i0, i1, i2 };
                        float[] d = { d0, d1, d2 };
                        bool[] k = { k0, k1, k2 };

                        float IntersectT(int a, int b)
                        {
                            float denom = d[b] - d[a];
                            if (Mathf.Abs(denom) < 1e-12f) return 0.5f;
                            return Mathf.Clamp01(-d[a] / denom);
                        }

                        if (keptCount == 1)
                        {
                            int a = k[0] ? 0 : (k[1] ? 1 : 2);
                            int b = (a + 1) % 3;
                            int c = (a + 2) % 3;

                            int va = AddVertexFromOld(idx[a]);
                            int vAB = AddInterpolatedVertex(idx[a], idx[b], IntersectT(a, b));
                            int vAC = AddInterpolatedVertex(idx[a], idx[c], IntersectT(a, c));

                            outTris[s].Add(va);
                            outTris[s].Add(vAB);
                            outTris[s].Add(vAC);
                        }
                        else
                        {
                            int a = !k[0] ? 0 : (!k[1] ? 1 : 2);
                            int b = (a + 1) % 3;
                            int c = (a + 2) % 3;

                            int vb = AddVertexFromOld(idx[b]);
                            int vc = AddVertexFromOld(idx[c]);
                            int vBA = AddInterpolatedVertex(idx[b], idx[a], IntersectT(b, a));
                            int vCA = AddInterpolatedVertex(idx[c], idx[a], IntersectT(c, a));

                            outTris[s].Add(vb);
                            outTris[s].Add(vc);
                            outTris[s].Add(vCA);

                            outTris[s].Add(vb);
                            outTris[s].Add(vCA);
                            outTris[s].Add(vBA);
                        }
                    }
                }
            }

            mesh.Clear();
            mesh.SetVertices(outVerts);
            if (hasNormals)  mesh.SetNormals(outNorms);
            if (hasTangents) mesh.SetTangents(outTangs);
            if (hasUV)       mesh.SetUVs(0, outUVs);

            mesh.subMeshCount = subMeshCount;
            for (int s = 0; s < subMeshCount; s++)
                mesh.SetTriangles(outTris[s], s, true);

            mesh.RecalculateBounds();
            if (!hasNormals) mesh.RecalculateNormals();

            return true;
        }

        public static bool CutMeshFrustum(
            Mesh mesh,
            Transform meshTransform,
            Vector3 centerWorld,
            Vector3 axisWorld,
            float nearRadius,
            float farRadius,
            float startOffset,
            float length,
            bool keepInside,
            float midRadius = 0f,
            float midPosition = 0.5f,
            float nearPreserveDepth = 0f,
            float plane3Radius = 0f,
            float plane3Position = 0.66f,
            float plane4Position = 1f,
            float epsilon = 1e-5f)
        {
            if (mesh == null || nearRadius <= 0 || length <= 0) return false;
            if (farRadius <= 0) farRadius = nearRadius;
            Vector3 cL = meshTransform.InverseTransformPoint(centerWorld);
            Vector3 aL = meshTransform.InverseTransformDirection(axisWorld).normalized;

            Vector3 lossyScale = meshTransform.lossyScale;
            float avgScale = (Mathf.Abs(lossyScale.x) + Mathf.Abs(lossyScale.y) + Mathf.Abs(lossyScale.z)) / 3f;
            float localNearR = avgScale > 0.001f ? nearRadius / avgScale : nearRadius;
            float localFarR  = avgScale > 0.001f ? farRadius / avgScale : farRadius;
            float localMidR  = midRadius > 0f ? (avgScale > 0.001f ? midRadius / avgScale : midRadius) : 0f;
            float localStart = avgScale > 0.001f ? startOffset / avgScale : startOffset;
            float localLen   = avgScale > 0.001f ? length / avgScale : length;
            float localMidPos = Mathf.Clamp01(midPosition);
            float localP3Pos = Mathf.Clamp01(plane3Position);
            float localP4Pos = Mathf.Clamp01(plane4Position);
            float localP3R = plane3Radius > 0f ? (avgScale > 0.001f ? plane3Radius / avgScale : plane3Radius) : 0f;
            float localPreserve = nearPreserveDepth > 0f ? (avgScale > 0.001f ? nearPreserveDepth / avgScale : nearPreserveDepth) : 0f;
            float _cNearR = localNearR, _cFarR = localFarR, _cMidR = localMidR;
            float _cMidPos = localMidPos, _cP3R = localP3R, _cP3Pos = localP3Pos, _cP4Pos = localP4Pos;
            float _cStart = localStart, _cLen = localLen;
            float _cPreserve = localPreserve;

            var verts = mesh.vertices;
            var norms = mesh.normals;
            var tangs = mesh.tangents;
            var uvs   = mesh.uv;

            bool hasNormals  = norms != null && norms.Length == verts.Length;
            bool hasTangents = tangs != null && tangs.Length == verts.Length;
            bool hasUV       = uvs   != null && uvs.Length   == verts.Length;

            int subMeshCount = mesh.subMeshCount;

            var outVerts = new List<Vector3>(verts.Length);
            var outNorms = hasNormals  ? new List<Vector3>(verts.Length) : null;
            var outTangs = hasTangents ? new List<Vector4>(verts.Length) : null;
            var outUVs   = hasUV       ? new List<Vector2>(verts.Length) : null;

            var keptMap = new Dictionary<int, int>(verts.Length);

            var outTris = new List<int>[subMeshCount];
            for (int s = 0; s < subMeshCount; s++)
                outTris[s] = new List<int>(mesh.GetTriangles(s).Length);

            bool IsInsideFrustum(Vector3 v)
            {
                Vector3 diff = v - cL;
                float axialDist = Vector3.Dot(diff, aL);
                float cutStart = -_cStart;
                float cutEnd = cutStart + _cLen;

                if (axialDist < cutStart - epsilon || axialDist > cutEnd + epsilon)
                    return false;
                if (_cPreserve > 0f && axialDist < cutStart + _cPreserve)
                    return false;
                float t = (_cLen > epsilon) ? Mathf.Clamp01((axialDist - cutStart) / _cLen) : 0f;
                float radiusAtDepth = RadiusAtT4(t, _cNearR, _cMidR, _cMidPos, _cP3R, _cP3Pos, _cFarR, _cP4Pos);

                Vector3 projected = axialDist * aL;
                float perpDist = (diff - projected).magnitude;

                return perpDist <= radiusAtDepth + epsilon;
            }

            int AddVertexFromOld(int oldIndex)
            {
                if (keptMap.TryGetValue(oldIndex, out int ni))
                    return ni;
                int newIndex = outVerts.Count;
                outVerts.Add(verts[oldIndex]);
                if (hasNormals) outNorms.Add(norms[oldIndex]);
                if (hasTangents) outTangs.Add(tangs[oldIndex]);
                if (hasUV) outUVs.Add(uvs[oldIndex]);
                keptMap[oldIndex] = newIndex;
                return newIndex;
            }

            for (int s = 0; s < subMeshCount; s++)
            {
                int[] tris = mesh.GetTriangles(s);
                for (int i = 0; i < tris.Length; i += 3)
                {
                    int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
                    bool in0 = IsInsideFrustum(verts[i0]);
                    bool in1 = IsInsideFrustum(verts[i1]);
                    bool in2 = IsInsideFrustum(verts[i2]);

                    bool keepTri;
                    if (keepInside)
                        keepTri = in0 || in1 || in2; // keep if any vertex inside
                    else
                        keepTri = !in0 && !in1 && !in2;
                    if (keepTri)
                    {
                        outTris[s].Add(AddVertexFromOld(i0));
                        outTris[s].Add(AddVertexFromOld(i1));
                        outTris[s].Add(AddVertexFromOld(i2));
                    }
                }
            }

            long triCount = 0;
            for (int s = 0; s < subMeshCount; s++) triCount += outTris[s].Count;
            if (triCount == 0) return false;

            mesh.Clear();
            mesh.SetVertices(outVerts);
            if (hasNormals)  mesh.SetNormals(outNorms);
            if (hasTangents) mesh.SetTangents(outTangs);
            if (hasUV)       mesh.SetUVs(0, outUVs);

            mesh.subMeshCount = subMeshCount;
            for (int s = 0; s < subMeshCount; s++)
                mesh.SetTriangles(outTris[s], s, true);

            mesh.RecalculateBounds();
            if (!hasNormals) mesh.RecalculateNormals();

            return true;
        }


        public static float RadiusAtT4(float t, float plane1R, float plane2R, float plane2Pos,
            float plane3R, float plane3Pos, float plane4R, float plane4Pos)
        {
            t = Mathf.Clamp01(t);

            float p1 = 0f;
            float p2 = Mathf.Clamp01(plane2Pos);
            float p3 = Mathf.Clamp01(plane3Pos);
            float p4 = Mathf.Clamp01(plane4Pos);

            float r1 = Mathf.Max(0f, plane1R);
            float r2 = Mathf.Max(0f, plane2R);
            float r3 = plane3R > 0f ? Mathf.Max(0f, plane3R) : r2;
            float r4 = Mathf.Max(0f, plane4R);

            if (p2 < p1) p2 = p1;
            if (p3 < p2) p3 = p2;
            if (p4 < p3) p4 = p3;

            if (t <= p2)
            {
                float seg = p2 > 1e-5f ? t / p2 : 0f;
                return Mathf.Lerp(r1, r2, seg);
            }
            if (t <= p3)
            {
                float denom = p3 - p2;
                float seg = denom > 1e-5f ? (t - p2) / denom : 0f;
                return Mathf.Lerp(r2, r3, seg);
            }
            {
                float denom = p4 - p3;
                float seg = denom > 1e-5f ? (t - p3) / denom : 1f;
                return Mathf.Lerp(r3, r4, seg);
            }
        }

        public static float RadiusAtT(float t, float nearR, float midR, float midPos, float farR)
        {
            return RadiusAtT4(t, nearR, midR, midPos, 0f, midPos, farR, 1f);
        }

        public static bool CutMeshCylinder(
            Mesh mesh,
            Transform meshTransform,
            Vector3 centerWorld,
            Vector3 axisWorld,
            float radius,
            bool keepInside,
            float epsilon = 1e-5f)
        {
            return CutMeshFrustum(mesh, meshTransform, centerWorld, axisWorld,
                radius, radius, 0f, 999f, keepInside,
                midRadius: 0f, midPosition: 0.5f, nearPreserveDepth: 0f, epsilon: epsilon);
        }
    }
}
