using System;

namespace PersonalBATranscriber.Core.Models;

public class GlossaryTerm
{
    public string TermId { get; set; } = Guid.NewGuid().ToString("N");
    public string TermText { get; set; } = string.Empty;
    public string Category { get; set; } = "General"; // PERSON, PROJECT, TECH, ACRONYM
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;
}
