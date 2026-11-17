using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Core.Inference
{
    public sealed class FaceDetector : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly string _inputName;

        private const int InputSize = 640;
        private const float ScoreThreshold = 0.6f;
        private const float NmsThreshold = 0.4f;
        private const int NumAnchors = 2;
        private static readonly int[] Strides = [8, 16, 32];

        public FaceDetector(string modelPath, int inferenceThreads = 1)
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

        public List<DetectedFace> Detect(byte[] bgraData, int width, int height)
        {
            var (tensor, scale, padX, padY) = Preprocess(bgraData, width, height);

            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
            using var outputs = _session.Run(inputs);

            return Postprocess(outputs, scale, padX, padY, width, height);
        }


        private static (DenseTensor<float> tensor, float scale, int padX, int padY) Preprocess(byte[] bgraData, int width, int height)
        {
            float scale = Math.Min((float)InputSize / width, (float)InputSize / height);
            int newW = (int)(width * scale);
            int newH = (int)(height * scale);
            int padX = (InputSize - newW) / 2;
            int padY = (InputSize - newH) / 2;

            var tensor = new DenseTensor<float>([1, 3, InputSize, InputSize]);

            float padVal = (0f - 127.5f) / 128f;

            for (int y = 0; y < InputSize; y++)
            {
                for (int x = 0; x < InputSize; x++)
                {
                    int srcX = (int)((x - padX) / scale);
                    int srcY = (int)((y - padY) / scale);

                    if (srcX < 0 || srcX >= width || srcY < 0 || srcY >= height)
                    {
                        tensor[0, 0, y, x] = padVal;
                        tensor[0, 1, y, x] = padVal;
                        tensor[0, 2, y, x] = padVal;
                        continue;
                    }

                    int idx = (srcY * width + srcX) * 4; //BGRA layout

                    // SCRFD waits for RGB, normalize: (px-127.5)/128
                    tensor[0, 0, y, x] = (bgraData[idx + 2] - 127.5f) / 128f; // R
                    tensor[0, 1, y, x] = (bgraData[idx + 1] - 127.5f) / 128f; // G
                    tensor[0, 2, y, x] = (bgraData[idx + 0] - 127.5f) / 128f; // B
                }
            }

            return (tensor, scale, padX, padY);
        }


        private static List<DetectedFace> Postprocess(
            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
            float scale,
            int padX,
            int padY,
            int origW,
            int origH
        )
        {
            var groups = new Dictionary<int, Dictionary<int, float[]>>();

            foreach (var o in outputs)
            {
                var t = o.AsTensor<float>();
                int n = t.Dimensions[0];
                int c = t.Dimensions[1];
                if (!groups.ContainsKey(n)) groups[n] = [];
                groups[n][c] = t.ToArray();
            }

            var candidates = new List<(RectangleF box, float score, PointF[] kps)>();

            for (int si = 0; si < Strides.Length; si++)
            {
                int stride = Strides[si];
                int gridSize = InputSize / stride;
                int totalN = gridSize * gridSize * NumAnchors;

                if (!groups.TryGetValue(totalN, out var group)) continue;
                if (!group.TryGetValue(1, out float[] scores)) continue;
                if (!group.TryGetValue(4, out float[] bboxes)) continue;
                group.TryGetValue(10, out float[]? kpss);

                for (int idx = 0; idx < totalN; idx++)
                {
                    float score = scores[idx];
                    if (score < ScoreThreshold) continue;

                    int ancorIdx = idx / NumAnchors;
                    float ax = (ancorIdx % gridSize) * stride;
                    float ay = (ancorIdx / gridSize) * stride;

                    float x1 = ax - bboxes[idx * 4 + 0] * stride;
                    float y1 = ay - bboxes[idx * 4 + 1] * stride;
                    float x2 = ax + bboxes[idx * 4 + 2] * stride;
                    float y2 = ay + bboxes[idx * 4 + 3] * stride;

                    x1 = Math.Clamp((x1 - padX) / scale, 0, origW);
                    y1 = Math.Clamp((y1 - padY) / scale, 0, origH);
                    x2 = Math.Clamp((x2 - padX) / scale, 0, origW);
                    y2 = Math.Clamp((y2 - padY) / scale, 0, origH);

                    var kps = new PointF[5];
                    if (kpss is not null)
                    {
                        for (int k = 0; k < 5; k++)
                        {
                            float kx = (ax + kpss[idx * 10 + k * 2] * stride - padX) / scale;
                            float ky = (ay + kpss[idx * 10 + k * 2 + 1] * stride - padY) / scale;
                            kps[k] = new PointF(Math.Clamp(kx, 0, origW), Math.Clamp(ky, 0, origH));
                        }
                    }

                    candidates.Add((new RectangleF(x1, y1, x2 - x1, y2 - y1), score, kps));
                }
            }

            return ApplyNms(candidates);

        }

        private static List<DetectedFace> ApplyNms(List<(RectangleF box, float score, PointF[] kps)> faces)
        {
            if (faces.Count == 0) return [];

            var sorted = faces.OrderByDescending(f => f.score).ToArray();
            var suppressed = new bool[sorted.Length];
            var result = new List<DetectedFace>();

            for (int i = 0; i < sorted.Length; i++)
            {
                if (suppressed[i]) continue;
                result.Add(new DetectedFace(sorted[i].box, sorted[i].score, sorted[i].kps));

                for (int j = i + 1; j < sorted.Length; j++)
                {
                    if (!suppressed[j] && IoU(sorted[i].box, sorted[j].box) > NmsThreshold)
                        suppressed[j] = true;
                }
            }
            return result;
        }

        private static float IoU(RectangleF a, RectangleF b)
        {
            float ix1 = Math.Max(a.Left, b.Left);
            float iy1 = Math.Max(a.Top, b.Top);
            float ix2 = Math.Min(a.Right, b.Right);
            float iy2 = Math.Min(a.Bottom, b.Bottom);

            if (ix2 <= ix1 || iy2 <= iy1) return 0f;

            float inter = (ix2 - ix1) * (iy2 - iy1);
            return inter / (a.Width * a.Height + b.Width * b.Height - inter);
        }



        public void Dispose() => _session.Dispose();
    }
}