using SharpClaw.Application.Infrastructure.Models.Access;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Infrastructure.Models.Resources;

/// <summary>
/// A registered document file path that agents can operate on.
/// Follows the <see cref="EditorSessionDB"/> resource pattern.
/// </summary>
public class DocumentSessionDB : BaseEntity
{
    /// <summary>Display name (e.g. "Q3 Budget", "Client Invoice").</summary>
    public string Name { get; set; } = "";

    /// <summary>Absolute path to the document file.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>
    /// Document type — determines which tools are available.
    /// Auto-detected from file extension during registration.
    /// </summary>
    public DocumentType DocumentType { get; set; }

    /// <summary>Optional description for the agent.</summary>
    public string? Description { get; set; }

    public ICollection<DocumentSessionAccessDB> Accesses { get; set; } = [];
}
