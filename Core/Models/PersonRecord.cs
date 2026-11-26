using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Core.Models
{
    public sealed class PersonRecord
    {
        public required string Name { get; init; }
        public required string Country { get; init; }
        public required string Role { get; init; }
        public IReadOnlyList<float[]> Embeddings { get; init; } = [];
        public string PhotoPath { get; init; } = string.Empty;
    }
}
