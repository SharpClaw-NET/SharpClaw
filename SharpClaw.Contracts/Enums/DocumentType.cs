namespace SharpClaw.Contracts.Enums;

/// <summary>
/// Identifies the document format for a <c>DocumentSessionDB</c>.
/// Determines which tools and backend engines are available.
/// </summary>
public enum DocumentType
{
    /// <summary>.xlsx, .xlsm — handled by ClosedXML (file-based) or COM Interop (live).</summary>
    Spreadsheet = 0,

    /// <summary>.csv — handled by CsvHelper.</summary>
    Csv = 1,

    /// <summary>.docx — reserved for future Word document support.</summary>
    Document = 2,

    /// <summary>.pptx — reserved for future PowerPoint support.</summary>
    Presentation = 3,
}
