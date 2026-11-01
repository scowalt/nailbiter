namespace backend.Models;

public sealed class ProHypeEvidenceChange
{
    public long Revision { get; set; }
    public string Kind { get; set; } = "";
    public string? BeforeSubject { get; set; }
    public string? AfterSubject { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
}

public sealed class ProHypeRevision
{
    public int Id { get; set; } = 1;
    public long Revision { get; set; }
}
