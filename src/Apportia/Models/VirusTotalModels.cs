using System.Text.Json.Serialization;

namespace Apportia.Models;

public sealed class VtStats
{
    [JsonPropertyName("malicious")] public int Malicious { get; set; }
    [JsonPropertyName("suspicious")] public int Suspicious { get; set; }
    [JsonPropertyName("undetected")] public int Undetected { get; set; }
    [JsonPropertyName("harmless")] public int Harmless { get; set; }
    [JsonPropertyName("timeout")] public int Timeout { get; set; }

    [JsonPropertyName("confirmed-timeout")]
    public int ConfirmedTimeout { get; set; }

    [JsonPropertyName("failure")] public int Failure { get; set; }
    [JsonPropertyName("type-unsupported")] public int TypeUnsupported { get; set; }
}

public sealed class VtEngineResult
{
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("engine_name")] public string? EngineName { get; set; }
    [JsonPropertyName("engine_version")] public string? EngineVersion { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("method")] public string? Method { get; set; }
    [JsonPropertyName("engine_update")] public string? EngineUpdate { get; set; }
}

public sealed class VtTotalVotes
{
    [JsonPropertyName("harmless")] public int Harmless { get; set; }
    [JsonPropertyName("malicious")] public int Malicious { get; set; }
}

public sealed class VtSandboxVerdict
{
    [JsonPropertyName("category")] public string? Category { get; set; }

    [JsonPropertyName("malware_classification")]
    public List<string>? MalwareClassification { get; set; }

    [JsonPropertyName("sandbox_name")] public string? SandboxName { get; set; }
    [JsonPropertyName("confidence")] public int? Confidence { get; set; }
}

public sealed class VtTrid
{
    [JsonPropertyName("file_type")] public string? FileType { get; set; }
    [JsonPropertyName("probability")] public double Probability { get; set; }
}

public sealed class VtSignatureInfo
{
    [JsonPropertyName("product")] public string? Product { get; set; }
    [JsonPropertyName("verified")] public string? Verified { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("signers")] public string? Signers { get; set; }
    [JsonPropertyName("signing date")] public string? SigningDate { get; set; }
    [JsonPropertyName("copyright")] public string? Copyright { get; set; }
    [JsonPropertyName("original name")] public string? OriginalName { get; set; }
    [JsonPropertyName("internal name")] public string? InternalName { get; set; }
    [JsonPropertyName("file version")] public string? FileVersion { get; set; }
}

public sealed class VtAttributes
{
    [JsonPropertyName("last_analysis_stats")]
    public VtStats? LastAnalysisStats { get; set; }

    [JsonPropertyName("last_analysis_results")]
    public Dictionary<string, VtEngineResult>? LastAnalysisResults { get; set; }

    [JsonPropertyName("last_analysis_date")]
    public long LastAnalysisDate { get; set; }

    [JsonPropertyName("meaningful_name")] public string? MeaningfulName { get; set; }
    [JsonPropertyName("names")] public List<string>? Names { get; set; }
    [JsonPropertyName("type_description")] public string? TypeDescription { get; set; }
    [JsonPropertyName("magic")] public string? Magic { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("type_tags")] public List<string>? TypeTags { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("md5")] public string? Md5 { get; set; }
    [JsonPropertyName("creation_date")] public long CreationDate { get; set; }

    [JsonPropertyName("first_submission_date")]
    public long FirstSubmissionDate { get; set; }

    [JsonPropertyName("last_submission_date")]
    public long LastSubmissionDate { get; set; }

    [JsonPropertyName("times_submitted")] public int TimesSubmitted { get; set; }
    [JsonPropertyName("unique_sources")] public int UniqueSources { get; set; }
    [JsonPropertyName("reputation")] public int Reputation { get; set; }
    [JsonPropertyName("total_votes")] public VtTotalVotes? TotalVotes { get; set; }
    [JsonPropertyName("signature_info")] public VtSignatureInfo? SignatureInfo { get; set; }
    [JsonPropertyName("sandbox_verdicts")] public Dictionary<string, VtSandboxVerdict>? SandboxVerdicts { get; set; }
    [JsonPropertyName("trid")] public List<VtTrid>? Trid { get; set; }
}

public sealed class VtData
{
    [JsonPropertyName("attributes")] public VtAttributes? Attributes { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
}

public sealed class VtResponse
{
    [JsonPropertyName("data")] public VtData? Data { get; set; }
}

public sealed class VtStore
{
    [JsonPropertyName("api_key")] public string? ApiKey { get; set; }
    [JsonPropertyName("files")] public Dictionary<string, Dictionary<string, string>> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class VtUploadData
{
    [JsonPropertyName("id")] public string? Id { get; set; }
}

public sealed class VtUploadResponse
{
    [JsonPropertyName("data")] public VtUploadData? Data { get; set; }
}

public sealed class VtAnalysisAttributes
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("stats")] public VtStats? Stats { get; set; }
}

public sealed class VtAnalysisData
{
    [JsonPropertyName("attributes")] public VtAnalysisAttributes? Attributes { get; set; }
}

public sealed class VtFileInfo
{
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
}

public sealed class VtAnalysisMeta
{
    [JsonPropertyName("file_info")] public VtFileInfo? FileInfo { get; set; }
}

public sealed class VtAnalysisResponse
{
    [JsonPropertyName("data")] public VtAnalysisData? Data { get; set; }
    [JsonPropertyName("meta")] public VtAnalysisMeta? Meta { get; set; }
}