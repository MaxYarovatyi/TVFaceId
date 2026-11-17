using Core.Imaging;
using Core.Inference;

namespace DatabaseBuilder.Services
{
    public sealed class PhotoValidator : IDisposable
    {
        public sealed record Candidate(byte[] Bytes, float[] Embedding);

        private readonly FaceDetector _detector;
        private readonly FaceRecognizer _recognizer;
        private readonly float _threshold;

        public PhotoValidator(string detModel, string recModel, float clusterThreshold = 0.5f)
        {
            _detector = new FaceDetector(detModel);
            _recognizer = new FaceRecognizer(recModel);
            _threshold = clusterThreshold;
        }

        public List<Candidate> Filter(List<byte[]> images, string label)
        {
            var all = new List<Candidate>();
            for (int i = 0; i < images.Count; i++)
            {
                try
                {
                    var (bgra, w, h) = ImageLoader.LoadAsBgra(images[i]);
                    var faces = _detector.Detect(bgra, w, h)
                        .OrderByDescending(f => f.BoundingBox.Width * f.BoundingBox.Height).ToList();

                    if (faces.Count == 0) { Console.WriteLine($"   - {label}: image {i + 1} no face"); continue; }
                    var main = faces[0];
                    if (main.BoundingBox.Width < 64) { Console.WriteLine($"   - {label}: image {i + 1} face too small"); continue; }
                    if (faces.Count > 1 &&
                        faces[1].BoundingBox.Width * faces[1].BoundingBox.Height >
                        0.5f * main.BoundingBox.Width * main.BoundingBox.Height)
                    { Console.WriteLine($"   - {label}: image {i + 1} ambiguous (several faces)"); continue; }

                    all.Add(new Candidate(images[i], _recognizer.GetEmbedding(bgra, w, h, main.Landmarks)));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ! {label}: image {i + 1}: {ex.Message}");
                }
            }

            if (all.Count <= 1) return all;

            // medoid: the candidate most others agree with; ties go to the earliest (official source first)
            int best = 0; // anchor = first surviving image (official, else Wikipedia lead)
            
            var kept = new List<Candidate>();
            for (int j = 0; j < all.Count; j++)
            {
                float s = Sim(all[best], all[j]);
                if (j == best || s >= _threshold) kept.Add(all[j]);
                else Console.WriteLine($"   - {label}: dropped image (sim {s:F2})");
            }
            if (kept.Count == 1) Console.WriteLine($"   ? {label}: nothing agrees with the first image");
            return kept;
        }

        private static float Sim(Candidate a, Candidate b) =>
            FaceRecognizer.CosineSimilarity(a.Embedding, b.Embedding);

        public void Dispose()
        {
            _detector.Dispose();
            _recognizer.Dispose();
        }
    }
}