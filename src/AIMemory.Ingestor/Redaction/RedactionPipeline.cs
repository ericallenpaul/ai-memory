using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using AIMemory.Ingestor.Configuration;

namespace AIMemory.Ingestor.Redaction;

public class RedactionPipeline : IRedactionPipeline
{
    private readonly ILogger<RedactionPipeline> _logger;
    private readonly RedactionConfig _config;
    private readonly List<(Regex Pattern, string Replacement)> _rules;
    private readonly List<string> _excludedExtensions = [".pem", ".pfx", ".key"];
    private readonly List<string> _excludedDirNames = ["secrets", "private"];

    public bool IsEnabled => _config.Mode != "off";

    public RedactionPipeline(RedactionConfig config, ILogger<RedactionPipeline> logger)
    {
        _config = config;
        _logger = logger;
        _rules = BuildRules();
    }

    public string Redact(string content)
    {
        if (!IsEnabled || string.IsNullOrEmpty(content))
            return content;

        var result = content;
        foreach (var (pattern, replacement) in _rules)
        {
            result = pattern.Replace(result, replacement);
        }

        return result;
    }

    public bool ShouldExcludeFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (_excludedExtensions.Contains(ext)) return true;

        var fileName = Path.GetFileName(filePath).ToLowerInvariant();
        if (fileName.StartsWith(".env")) return true;

        var dirParts = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (dirParts.Any(d => _excludedDirNames.Contains(d.ToLowerInvariant()))) return true;

        return _config.Exclusions.Any(exc => filePath.Contains(exc, StringComparison.OrdinalIgnoreCase));
    }

    private static List<(Regex Pattern, string Replacement)> BuildRules()
    {
        return
        [
            (new Regex(@"sk-[a-zA-Z0-9]{20,}", RegexOptions.Compiled), "[REDACTED:API_KEY]"),
            (new Regex(@"anthropic-[a-zA-Z0-9\-]{20,}", RegexOptions.Compiled), "[REDACTED:API_KEY]"),
            (new Regex(@"Bearer\s+[a-zA-Z0-9\-._~+/]+=*", RegexOptions.Compiled), "[REDACTED:BEARER]"),
            (new Regex(@"-----BEGIN\s+.*?PRIVATE KEY-----[\s\S]*?-----END\s+.*?PRIVATE KEY-----", RegexOptions.Compiled), "[REDACTED:PRIVATE_KEY]"),
            (new Regex(@"(?i)(password|passwd|pwd)\s*[=:]\s*\S+", RegexOptions.Compiled), "[REDACTED:PASSWORD]"),
            (new Regex(@"(?i)Password=[^;]+", RegexOptions.Compiled), "[REDACTED:CONNSTRING]"),
            (new Regex(@"AKIA[A-Z0-9]{16}", RegexOptions.Compiled), "[REDACTED:AWS_KEY]"),
            (new Regex(@"(?i)(OPENAI_API_KEY|ANTHROPIC_API_KEY|OPENROUTER_API_KEY|GOOGLE_API_KEY|AWS_SECRET_ACCESS_KEY)\s*=\s*\S+", RegexOptions.Compiled), "[REDACTED:ENV_SECRET]"),
        ];
    }
}
