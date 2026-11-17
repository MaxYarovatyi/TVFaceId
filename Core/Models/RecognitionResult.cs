using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Core.Models
{
    public record RecognitionResult(
        DetectedFace Face,
        PersonRecord? Person,
        float Confidence
    );
}