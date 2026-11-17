using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Core.Inference
{
    public sealed class FaceRecognizer : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly string _inputName;

        private static readonly float[,] Canonical =
        {
            { 38.2946f, 51.6963f },
            { 73.5318f, 51.5014f },
            { 56.0252f, 71.7366f },
            { 41.5493f, 92.3655f },
            { 70.7299f, 92.2041f }
        };

        public FaceRecognizer(string modelPath, int inferenceThreads = 1)
        {
            var opts = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = inferenceThreads,
                InterOpNumThreads = 1
            };
            _session = new InferenceSession(modelPath, opts);
            _inputName = _session.InputMetadata.Keys.First();
        }

        public float[] GetEmbedding(byte[] bgraData, int width, int height, PointF[] landmarks)
        {
            var M = EstimateSimilarityTransform(landmarks, Canonical);
            byte[] face = WarpAffine112(bgraData, width, height, M);
            var tensor = BuildTensor(face);

            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
            using var outputs = _session.Run(inputs);

            float[] embedding = outputs.First().AsEnumerable<float>().ToArray();
            return L2Normalize(embedding);
        }

        public static float CosineSimilarity(float[] a, float[] b)
        {
            float dot = 0f;
            for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
            return (dot + 1f) / 2f;
        }


        private static float[,] EstimateSimilarityTransform(PointF[] src, float[,] dst)
        {
            double[,] WtW = new double[4, 4];
            double[] Wtq = new double[4];

            for (int i = 0; i < src.Length; i++)
            {
                double x = src[i].X, y = src[i].Y;
                double xd = dst[i, 0], yd = dst[i, 1];

                double[] r1 = [x, -y, 1, 0];
                double[] r2 = [y, x, 0, 1];

                for (int j = 0; j < 4; j++)
                {
                    Wtq[j] += r1[j] * xd + r2[j] * yd;
                    for (int k = 0; k < 4; k++)
                        WtW[j, k] += r1[j] * r1[k] + r2[j] * r2[k];
                }
            }

            double[] p = SolveLinear4x4(WtW, Wtq);
            float a = (float)p[0], b = (float)p[1];
            float tx = (float)p[2], ty = (float)p[3];

            return new float[,] { { a, -b, tx }, { b, a, ty } };
        }

        private static double[] SolveLinear4x4(double[,] A, double[] b)
        {
            const int n = 4;
            var aug = new double[n, n + 1];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) aug[i, j] = A[i, j];
                aug[i, n] = b[i];
            }

            for (int col = 0; col < n; col++)
            {
                int pivot = col;
                for (int row = col + 1; row < n; row++)
                    if (Math.Abs(aug[row, col]) > Math.Abs(aug[pivot, col]))
                        pivot = row;

                for (int j = 0; j <= n; j++)
                    (aug[col, j], aug[pivot, j]) = (aug[pivot, j], aug[col, j]);

                for (int row = col + 1; row < n; row++)
                {
                    double f = aug[row, col] / aug[col, col];
                    for (int j = col; j <= n; j++)
                        aug[row, j] -= f * aug[col, j];
                }
            }

            var x = new double[n];
            for (int i = n - 1; i >= 0; i--)
            {
                x[i] = aug[i, n];
                for (int j = i + 1; j < n; j++) x[i] -= aug[i, j] * x[j];
                x[i] /= aug[i, i];
            }
            return x;
        }


        private static byte[] WarpAffine112(byte[] bgraData, int srcW, int srcH, float[,] M)
        {
            float a = M[0, 0], b = M[1, 0], tx = M[0, 2], ty = M[1, 2];
            float invDet = 1f / (a * a + b * b);

            const int size = 112;
            var rgb = new byte[size * size * 3];

            for (int dy = 0; dy < size; dy++)
            {
                for (int dx = 0; dx < size; dx++)
                {
                    float cx = dx - tx, cy = dy - ty;
                    float sx = (a * cx + b * cy) * invDet;
                    float sy = (-b * cx + a * cy) * invDet;

                    int x0 = (int)MathF.Floor(sx), y0 = (int)MathF.Floor(sy);
                    int x1 = x0 + 1, y1 = y0 + 1;
                    float wx = sx - x0, wy = sy - y0;

                    x0 = Math.Clamp(x0, 0, srcW - 1);
                    y0 = Math.Clamp(y0, 0, srcH - 1);
                    x1 = Math.Clamp(x1, 0, srcW - 1);
                    y1 = Math.Clamp(y1, 0, srcH - 1);

                    int p00 = (y0 * srcW + x0) * 4; // BGRA offsets
                    int p10 = (y0 * srcW + x1) * 4;
                    int p01 = (y1 * srcW + x0) * 4;
                    int p11 = (y1 * srcW + x1) * 4;

                    int outIdx = (dy * size + dx) * 3;
                    int[] bgraOrder = [2, 1, 0]; // R, G, B з BGRA
                    for (int ch = 0; ch < 3; ch++)
                    {
                        int s = bgraOrder[ch];
                        float v = (1 - wx) * (1 - wy) * bgraData[p00 + s]
                                + wx * (1 - wy) * bgraData[p10 + s]
                                + (1 - wx) * wy * bgraData[p01 + s]
                                + wx * wy * bgraData[p11 + s];
                        rgb[outIdx + ch] = (byte)Math.Clamp(v, 0f, 255f);
                    }
                }
            }
            return rgb;
        }
        private static DenseTensor<float> BuildTensor(byte[] faceRgb)
        {
            var tensor = new DenseTensor<float>([1, 3, 112, 112]);
            for (int y = 0; y < 112; y++)
                for (int x = 0; x < 112; x++)
                {
                    int idx = (y * 112 + x) * 3;
                    tensor[0, 0, y, x] = (faceRgb[idx] - 127.5f) / 128f; // R
                    tensor[0, 1, y, x] = (faceRgb[idx + 1] - 127.5f) / 128f; // G
                    tensor[0, 2, y, x] = (faceRgb[idx + 2] - 127.5f) / 128f; // B
                }
            return tensor;
        }

        private static float[] L2Normalize(float[] v)
        {
            float norm = MathF.Sqrt(v.Sum(x => x * x));
            return norm > 1e-10f ? v.Select(x => x / norm).ToArray() : v;
        }

        public void Dispose() => _session.Dispose();

    }
}