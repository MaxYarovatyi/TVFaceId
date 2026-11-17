using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DatabaseBuilder.Models
{
    public sealed class PersonEntry
    (
        string wikipediaArticle,
        string name,
        string country,
        string role
    )
    {
        public string WikipediaArticle { get; } = wikipediaArticle;
        public string Name { get; } = name;
        public string Country { get; } = country;
        public string Role { get; private set; } = role;
        public string ImageUrl { get; set; } = string.Empty;
        public List<string> ImageUrls { get; } = [];

        public void AppendRole(string extra)
        {
            if (!Role.Contains(extra))
                Role = $"{Role} / {extra}";
        }
    }



    public sealed record CuratedPerson
    (
        string Name,
        string Country,
        string Role,
        string Wikipedia
    );
}