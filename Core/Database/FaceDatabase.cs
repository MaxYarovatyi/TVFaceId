using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Core.Inference;
using Core.Models;
using Core.Imaging;
using System.Text.Json;

namespace Core.Database
{
    public sealed class FaceDatabase
    {
        // Replaced as a whole when loading finishes, so FindBest never sees a half-built list.
        private volatile IReadOnlyList<PersonRecord> _persons = [];
        private volatile bool _loaded;

        private readonly FaceDetector _detector;
        private readonly FaceRecognizer _recognizer;
        private readonly ILogger<FaceDatabase> _logger;

        public FaceDatabase(FaceDetector detector, FaceRecognizer recognizer, ILogger<FaceDatabase> logger)
        {
            _detector = detector;
            _recognizer = recognizer;
            _logger = logger;
        }

        /// <summary>True once <see cref="Load"/> has finished (even if the folder was missing).</summary>
        public bool IsLoaded => _loaded;

        public int PersonCount => _persons.Count;

        public void Load(string facesFolder)
        {
            var loaded = new List<PersonRecord>();

            if (!Directory.Exists(facesFolder))
            {
                _logger.LogWarning("Faces folder not found: {Path}", facesFolder);
            }
            else
            {
                foreach (var dir in Directory.EnumerateDirectories(facesFolder))
                {
                    try
                    {
                        var record = ProcessPersonFolder(dir, facesFolder);
                        if (record is null) continue;
                        loaded.Add(record);
                        _logger.LogInformation(" + {Name} | {Country} | {Role} ({N} photo(s))",
                            record.Name, record.Country, record.Role, record.Embeddings.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(" x {Dir}: {Err}", Path.GetFileName(dir), ex.Message);
                    }
                }
            }

            _persons = loaded;
            _loaded = true;
            _logger.LogInformation("Database ready: {Count} person(s)", loaded.Count);
        }

        private sealed record PersonMeta(string Name, string Country, string Role);
        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        private PersonRecord? ProcessPersonFolder(string dir, string root)
        {
            var metaPath = Path.Combine(dir, "person.json");
            if (!File.Exists(metaPath)) return null;
            var meta = JsonSerializer.Deserialize<PersonMeta>(File.ReadAllText(metaPath), JsonOpts);
            if (meta is null) return null;

            var embeddings = new List<float[]>();
            string? firstPhoto = null;

            var images = Directory.EnumerateFiles(dir)
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f);

            foreach (var file in images)
            {
                var (bgra, w, h) = ImageLoader.LoadAsBgra(file);
                var faces = _detector.Detect(bgra, w, h);
                if (faces.Count == 0) continue;
                var face = faces.MaxBy(f => f.BoundingBox.Width * f.BoundingBox.Height)!;
                embeddings.Add(_recognizer.GetEmbedding(bgra, w, h, face.Landmarks));
                firstPhoto ??= file;
            }

            if (embeddings.Count == 0)
            {
                _logger.LogWarning(" ! No usable face in {Dir}", Path.GetFileName(dir));
                return null;
            }

            return new PersonRecord
            {
                Name = meta.Name,
                Country = meta.Country,
                Role = meta.Role,
                PhotoPath = Path.GetRelativePath(root, firstPhoto!).Replace('\\', '/'),
                Embeddings = embeddings
            };
        }

        public (PersonRecord? Person, float Similarity) FindBest
        (
            DetectedFace face,
            byte[] bgraData,
            int width,
            int height,
            float threshold = 0.71f
        )
        {
            var persons = _persons;
            if (persons.Count == 0) return (null, 0f);

            float[] query = _recognizer.GetEmbedding(bgraData, width, height, face.Landmarks);
            float bestSim = float.MinValue;
            PersonRecord? bestPerson = null;

            foreach (var person in persons)
            {
                foreach (var emb in person.Embeddings)
                {
                    float sim = FaceRecognizer.CosineSimilarity(query, emb);
                    if (sim > bestSim) { bestSim = sim; bestPerson = person; }
                }
            }

            return bestSim >= threshold
                ? (bestPerson, bestSim)
                : (null, bestSim);
        }
    }
}
