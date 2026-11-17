using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Web.Models
{
    public sealed record FaceResult
    (
        string Name,
        string Country,
        string Role,
        int Confidence,
        string? PhotoUrl
    );
}