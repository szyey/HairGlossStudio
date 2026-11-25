using System;
using System.Collections.Generic;
using UnityEngine;

namespace HairGlossStudio.Editor
{
    /// <summary>
    /// Analyzes hair mesh UVs, 3D vertical orientation, and main texture gradients
    /// to synthesize a coherent 2D hair strand flow field.
    /// </summary>
    public static class HairFlowAnalyzer
    {
        public class AnalysisResult
        {
            public int width;
            public int height;
            public Vector2[] flowField;
            public float[] occupancyMask;
            public float[] paddedOccupancyMask;
            public float[] rootTipMap;
            public float[] luminanceMap;
            public Color[] baseColorMap;
            public Color averageBaseColor;
        }

        public class AnalysisCache
        {
            private readonly Dictionary<string, AnalysisResult> results = new Dictionary<string, AnalysisResult>();

            public AnalysisResult GetOrAnalyze(Mesh mesh, int submeshIndex, Texture2D mainTex, int resolution, float flowAnalyzeWeight)
            {
                string key = GetAnalysisCacheKey(mesh, submeshIndex, mainTex, resolution, flowAnalyzeWeight);
                if (!results.TryGetValue(key, out AnalysisResult result))
                {
                    result = AnalyzeFlowField(mesh, submeshIndex, mainTex, resolution, flowAnalyzeWeight);
                    results.Add(key, result);
                }
                return result;
            }

            public void Clear() { results.Clear(); }
        }

        public static string GetAnalysisCacheKey(Mesh mesh, int submeshIndex, Texture2D mainTex, int resolution, float flowAnalyzeWeight)
        {
            return FormattableString.Invariant($"{(mesh != null ? mesh.GetInstanceID() : 0)}:{submeshIndex}:{(mainTex != null ? mainTex.GetInstanceID() : 0)}:{Mathf.Clamp(resolution, 64, 2048)}:{flowAnalyzeWeight:R}");
        }

        private static readonly float[] GaussianWeights7 =
        {
            1f / 64f, 6f / 64f, 15f / 64f, 20f / 64f, 15f / 64f, 6f / 64f, 1f / 64f
        };

        public static AnalysisResult AnalyzeFlowField(
            Mesh mesh,
            int submeshIndex,
            Texture2D mainTex,
            int resolution,
            float flowAnalyzeWeight)
        {
            int size = Mathf.Clamp(resolution, 64, 2048);
            int pixelCount = size * size;

            Color[] baseColors = ReadTexturePixelsSafe(mainTex, size, size);
            float[] luminance = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                Color c = baseColors[i];
                luminance[i] = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            }

            Vector2[] macroFlow = new Vector2[pixelCount];
            float[] occupancy = new float[pixelCount];
            float[] paddedOccupancy = new float[pixelCount];
            float[] rootTipMap = new float[pixelCount];

            bool hasMeshFlow = RasterizeMeshFlow(mesh, submeshIndex, size, macroFlow, occupancy, rootTipMap);

            if (!hasMeshFlow)
            {
                for (int y = 0; y < size; y++)
                {
                    float v = (y + 0.5f) / size;
                    float rootTip = 1f - v;
                    for (int x = 0; x < size; x++)
                    {
                        int idx = y * size + x;
                        macroFlow[idx] = new Vector2(0f, -1f);
                        occupancy[idx] = 1f;
                        paddedOccupancy[idx] = 1f;
                        rootTipMap[idx] = rootTip;
                    }
                }
            }
            else
            {
                Array.Copy(occupancy, paddedOccupancy, pixelCount);
                DilateMeshData(size, macroFlow, paddedOccupancy, rootTipMap, 8);

                for (int i = 0; i < pixelCount; i++)
                {
                    if (macroFlow[i].sqrMagnitude > 1e-6f)
                    {
                        macroFlow[i].Normalize();
                    }
                    else
                    {
                        macroFlow[i] = new Vector2(0f, -1f);
                    }
                }
            }

            // Compute Structure Tensor from main texture luminance when enabled
            Vector2[] finalFlow = macroFlow;
            if (mainTex != null && flowAnalyzeWeight > 0.001f)
            {
                finalFlow = new Vector2[pixelCount];
                ComputeStructureTensorFlow(size, luminance, macroFlow, flowAnalyzeWeight, finalFlow);
            }

            Color avgColor = ComputeAverageOccupiedColor(baseColors, occupancy);

            return new AnalysisResult
            {
                width = size,
                height = size,
                flowField = finalFlow,
                occupancyMask = occupancy,
                paddedOccupancyMask = paddedOccupancy,
                rootTipMap = rootTipMap,
                luminanceMap = luminance,
                baseColorMap = baseColors,
                averageBaseColor = avgColor
            };
        }

        public static Color[] ReadTexturePixelsSafe(Texture2D source, int targetWidth, int targetHeight)
        {
            int count = targetWidth * targetHeight;
            if (source == null)
            {
                var fallback = new Color[count];
                var neutral = new Color(0.5f, 0.5f, 0.5f, 1f);
                for (int i = 0; i < count; i++)
                {
                    fallback[i] = neutral;
                }
                return fallback;
            }

            RenderTexture prev = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D readable = null;
            try
            {
                rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                readable = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false, false);
                readable.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
                readable.Apply(false, false);
                return readable.GetPixels();
            }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
            }
        }

        private static bool RasterizeMeshFlow(
            Mesh mesh,
            int submeshIndex,
            int size,
            Vector2[] macroFlow,
            float[] occupancy,
            float[] rootTipMap)
        {
            if (mesh == null || !mesh.isReadable)
            {
                return false;
            }

            if (submeshIndex < 0 || submeshIndex >= mesh.subMeshCount)
            {
                return false;
            }

            Vector3[] vertices = mesh.vertices;
            Vector2[] uvs = mesh.uv;
            if (vertices == null || uvs == null || uvs.Length != vertices.Length || vertices.Length == 0)
            {
                return false;
            }

            int[] indices = mesh.GetTriangles(submeshIndex);
            if (indices == null || indices.Length < 3)
            {
                return false;
            }

            int vertexCount = vertices.Length;
            int[] parent = new int[vertexCount];
            float[] compMinY = new float[vertexCount];
            float[] compMaxY = new float[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                parent[i] = i;
                compMinY[i] = float.MaxValue;
                compMaxY[i] = float.MinValue;
            }

            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                UnionVertices(parent, indices[t], indices[t + 1]);
                UnionVertices(parent, indices[t + 1], indices[t + 2]);
            }

            float minY = float.MaxValue;
            float maxY = float.MinValue;
            for (int i = 0; i < indices.Length; i++)
            {
                int vIdx = indices[i];
                int root = FindRoot(parent, vIdx);
                float y = vertices[vIdx].y;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                if (y < compMinY[root]) compMinY[root] = y;
                if (y > compMaxY[root]) compMaxY[root] = y;
            }

            float yRange = Mathf.Max(1e-5f, maxY - minY);
            bool anyTriangleRasterized = false;

            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                int i0 = indices[t];
                int i1 = indices[t + 1];
                int i2 = indices[t + 2];

                Vector2 uv0 = uvs[i0];
                Vector2 uv1 = uvs[i1];
                Vector2 uv2 = uvs[i2];

                // Shift out-of-range UDIM tiles back into [0, 1] per triangle
                float shiftU = Mathf.Floor((uv0.x + uv1.x + uv2.x) / 3f);
                float shiftV = Mathf.Floor((uv0.y + uv1.y + uv2.y) / 3f);
                uv0.x -= shiftU; uv1.x -= shiftU; uv2.x -= shiftU;
                uv0.y -= shiftV; uv1.y -= shiftV; uv2.y -= shiftV;

                float y0 = vertices[i0].y;
                float y1 = vertices[i1].y;
                float y2 = vertices[i2].y;

                // Gradient of (-y) in UV space points from top (root) to bottom (tip)
                float du1 = uv1.x - uv0.x;
                float dv1 = uv1.y - uv0.y;
                float dh1 = -(y1 - y0);

                float du2 = uv2.x - uv0.x;
                float dv2 = uv2.y - uv0.y;
                float dh2 = -(y2 - y0);

                float uvDet = du1 * dv2 - du2 * dv1;
                if (Mathf.Abs(uvDet) < 1e-8f)
                {
                    continue;
                }

                float gradU = (dh1 * dv2 - dh2 * dv1) / uvDet;
                float gradV = (du1 * dh2 - du2 * dh1) / uvDet;
                Vector2 triFlow = new Vector2(gradU, gradV);
                if (triFlow.sqrMagnitude > 1e-8f)
                {
                    triFlow.Normalize();
                }
                else
                {
                    triFlow = new Vector2(0f, -1f);
                }

                float rt0 = ComputeVertexRootTip(i0, vertices, parent, compMinY, compMaxY, minY, yRange);
                float rt1 = ComputeVertexRootTip(i1, vertices, parent, compMinY, compMaxY, minY, yRange);
                float rt2 = ComputeVertexRootTip(i2, vertices, parent, compMinY, compMaxY, minY, yRange);

                Vector2 p0 = uv0 * size;
                Vector2 p1 = uv1 * size;
                Vector2 p2 = uv2 * size;

                int minPx = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x))), 0, size - 1);
                int maxPx = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x))), 0, size - 1);
                int minPy = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y))), 0, size - 1);
                int maxPy = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y))), 0, size - 1);

                float area = EdgeFunction(p0, p1, p2);
                if (Mathf.Abs(area) < 1e-5f)
                {
                    continue;
                }

                float invArea = 1f / area;
                for (int py = minPy; py <= maxPy; py++)
                {
                    for (int px = minPx; px <= maxPx; px++)
                    {
                        Vector2 p = new Vector2(px + 0.5f, py + 0.5f);
                        float w0 = EdgeFunction(p1, p2, p) * invArea;
                        float w1 = EdgeFunction(p2, p0, p) * invArea;
                        float w2 = EdgeFunction(p0, p1, p) * invArea;

                        if (w0 >= -0.001f && w1 >= -0.001f && w2 >= -0.001f)
                        {
                            int idx = py * size + px;
                            macroFlow[idx] += triFlow;
                            occupancy[idx] = 1f;
                            rootTipMap[idx] = Mathf.Clamp01(w0 * rt0 + w1 * rt1 + w2 * rt2);
                            anyTriangleRasterized = true;
                        }
                    }
                }
            }

            return anyTriangleRasterized;
        }

        private static int FindRoot(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        private static void UnionVertices(int[] parent, int a, int b)
        {
            int ra = FindRoot(parent, a);
            int rb = FindRoot(parent, b);
            if (ra != rb)
            {
                parent[rb] = ra;
            }
        }

        private static float ComputeVertexRootTip(
            int vertexIndex,
            Vector3[] vertices,
            int[] parent,
            float[] compMinY,
            float[] compMaxY,
            float globalMinY,
            float globalYRange)
        {
            int r = FindRoot(parent, vertexIndex);
            float cRange = compMaxY[r] - compMinY[r];
            if (cRange >= globalYRange * 0.08f)
            {
                return 1f - Mathf.Clamp01((vertices[vertexIndex].y - compMinY[r]) / cRange);
            }
            return 1f - Mathf.Clamp01((vertices[vertexIndex].y - globalMinY) / globalYRange);
        }

        private static float EdgeFunction(Vector2 a, Vector2 b, Vector2 c)
        {
            return (c.x - a.x) * (b.y - a.y) - (c.y - a.y) * (b.x - a.x);
        }

        private static void DilateMeshData(
            int size,
            Vector2[] flow,
            float[] paddedOccupancy,
            float[] rootTipMap,
            int iterations)
        {
            int count = size * size;
            Vector2[] nextFlow = new Vector2[count];
            float[] nextOcc = new float[count];
            float[] nextRootTip = new float[count];
            Vector2[] originalFlow = flow;
            float[] originalOcc = paddedOccupancy;
            float[] originalRootTip = rootTipMap;

            for (int iter = 0; iter < iterations; iter++)
            {
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int idx = y * size + x;
                        nextFlow[idx] = flow[idx];
                        nextOcc[idx] = paddedOccupancy[idx];
                        nextRootTip[idx] = rootTipMap[idx];
                        if (paddedOccupancy[idx] > 0.5f)
                        {
                            continue;
                        }

                        Vector2 flowAccum = Vector2.zero;
                        float rtAccum = 0f;
                        int neighbors = 0;

                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int ny = y + dy;
                            if (ny < 0 || ny >= size) continue;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx;
                                if (nx < 0 || nx >= size) continue;

                                int nIdx = ny * size + nx;
                                if (paddedOccupancy[nIdx] > 0.5f)
                                {
                                    flowAccum += flow[nIdx];
                                    rtAccum += rootTipMap[nIdx];
                                    neighbors++;
                                }
                            }
                        }

                        if (neighbors > 0)
                        {
                            nextFlow[idx] = flowAccum / neighbors;
                            nextRootTip[idx] = rtAccum / neighbors;
                            nextOcc[idx] = 1f;
                        }
                    }
                }

                (flow, nextFlow) = (nextFlow, flow);
                (paddedOccupancy, nextOcc) = (nextOcc, paddedOccupancy);
                (rootTipMap, nextRootTip) = (nextRootTip, rootTipMap);
            }
            if (!ReferenceEquals(flow, originalFlow))
            {
                Array.Copy(flow, originalFlow, count);
                Array.Copy(paddedOccupancy, originalOcc, count);
                Array.Copy(rootTipMap, originalRootTip, count);
            }
        }

        private static void ComputeStructureTensorFlow(
            int size,
            float[] luminance,
            Vector2[] macroFlow,
            float flowAnalyzeWeight,
            Vector2[] outputFlow)
        {
            int count = size * size;
            float[] jxx = new float[count];
            float[] jxy = new float[count];
            float[] jyy = new float[count];

            // Sobel gradient
            for (int y = 0; y < size; y++)
            {
                int ym = Mathf.Max(0, y - 1);
                int yp = Mathf.Min(size - 1, y + 1);

                for (int x = 0; x < size; x++)
                {
                    int xm = Mathf.Max(0, x - 1);
                    int xp = Mathf.Min(size - 1, x + 1);

                    float l00 = luminance[ym * size + xm];
                    float l10 = luminance[ym * size + x];
                    float l20 = luminance[ym * size + xp];

                    float l01 = luminance[y * size + xm];
                    float l21 = luminance[y * size + xp];

                    float l02 = luminance[yp * size + xm];
                    float l12 = luminance[yp * size + x];
                    float l22 = luminance[yp * size + xp];

                    float gx = (-l00 + l20 - 2f * l01 + 2f * l21 - l02 + l22) * 0.125f;
                    float gy = (-l00 - 2f * l10 - l20 + l02 + 2f * l12 + l22) * 0.125f;

                    int idx = y * size + x;
                    jxx[idx] = gx * gx;
                    jxy[idx] = gx * gy;
                    jyy[idx] = gy * gy;
                }
            }

            // Separable Gaussian smoothing on structure tensor
            float[] scratch = new float[count];
            GaussianBlur7(size, jxx, scratch);
            GaussianBlur7(size, jxy, scratch);
            GaussianBlur7(size, jyy, scratch);

            for (int i = 0; i < count; i++)
            {
                float xx = jxx[i];
                float xy = jxy[i];
                float yy = jyy[i];

                float trace = xx + yy;
                float diff = xx - yy;
                float disc = Mathf.Sqrt(diff * diff + 4f * xy * xy);

                float lambda1 = 0.5f * (trace + disc);
                float lambda2 = 0.5f * (trace - disc);

                // Minor eigenvector = along hair strands (orthogonal to intensity gradient)
                float phi = 0.5f * Mathf.Atan2(2f * xy, diff);
                Vector2 strandVec = new Vector2(-Mathf.Sin(phi), Mathf.Cos(phi));

                Vector2 mFlow = macroFlow[i];
                if (Vector2.Dot(strandVec, mFlow) < 0f)
                {
                    strandVec = -strandVec;
                }

                float coherence = (lambda1 - lambda2) / (lambda1 + lambda2 + 1e-6f);
                float energyWeight = Mathf.Clamp01(trace * 40f);
                float blend = Mathf.Clamp01(flowAnalyzeWeight * coherence * energyWeight);

                Vector2 combined = Vector2.Lerp(mFlow, strandVec, blend);
                outputFlow[i] = combined.sqrMagnitude > 1e-6f ? combined.normalized : mFlow;
            }
        }

        private static void GaussianBlur7(int size, float[] data, float[] scratch)
        {
            // Horizontal pass
            for (int y = 0; y < size; y++)
            {
                int row = y * size;
                for (int x = 0; x < size; x++)
                {
                    float sum = 0f;
                    for (int k = -3; k <= 3; k++)
                    {
                        int sx = Mathf.Clamp(x + k, 0, size - 1);
                        sum += data[row + sx] * GaussianWeights7[k + 3];
                    }
                    scratch[row + x] = sum;
                }
            }

            // Vertical pass
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float sum = 0f;
                    for (int k = -3; k <= 3; k++)
                    {
                        int sy = Mathf.Clamp(y + k, 0, size - 1);
                        sum += scratch[sy * size + x] * GaussianWeights7[k + 3];
                    }
                    data[y * size + x] = sum;
                }
            }
        }

        private static Color ComputeAverageOccupiedColor(Color[] colors, float[] occupancy)
        {
            Vector3 accum = Vector3.zero;
            float weight = 0f;
            for (int i = 0; i < colors.Length; i++)
            {
                if (occupancy[i] > 0.5f && colors[i].a > 0.1f)
                {
                    accum += new Vector3(colors[i].r, colors[i].g, colors[i].b);
                    weight += 1f;
                }
            }

            if (weight <= 0f)
            {
                return Color.white;
            }

            accum /= weight;
            return new Color(accum.x, accum.y, accum.z, 1f);
        }
    }
}
