namespace eZBERP_AI_IDE.Services;

public sealed class SalesforceValidationService
{
    private readonly SalesforceCliService _sfCli;

    public SalesforceValidationService(SalesforceCliService sfCli)
    {
        _sfCli = sfCli;
    }

    public async Task<ValidationResult> ValidateDeploymentAsync(string repoPath, string orgAlias)
    {
        return await ValidateDeploymentAsync(repoPath, orgAlias, Array.Empty<string>());
    }

    public async Task<ValidationResult> ValidateDeploymentAsync(string repoPath, string orgAlias, IEnumerable<string> relativeFilePaths)
    {
        try
        {
            var files = relativeFilePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var output = files.Count == 0
                ? await _sfCli.RunCommandAsync(repoPath, $"project deploy start --dry-run --target-org {orgAlias}")
                : (await _sfCli.ValidateFilesToOrgAsync(repoPath, orgAlias, files, 10)).CombinedOutput;
            
            var isSuccess = !output.Contains("Error", StringComparison.OrdinalIgnoreCase) && 
                            !output.Contains("Failed", StringComparison.OrdinalIgnoreCase);

            return new ValidationResult(isSuccess, output);
        }
        catch (Exception ex)
        {
            return new ValidationResult(false, $"Validation failed to run: {ex.Message}");
        }
    }

    public async Task<ValidationResult> ValidateApexAsync(string repoPath, string orgAlias, string className)
    {
        try
        {
            // Run tests for a specific class if requested
            var output = await _sfCli.RunCommandAsync(repoPath, $"apex run test --class-names {className} --target-org {orgAlias} --wait 10");
            
            var isSuccess = output.Contains("Pass", StringComparison.OrdinalIgnoreCase) && 
                            !output.Contains("Fail", StringComparison.OrdinalIgnoreCase);

            return new ValidationResult(isSuccess, output);
        }
        catch (Exception ex)
        {
            return new ValidationResult(false, $"Apex validation failed: {ex.Message}");
        }
    }
}

public sealed record ValidationResult(bool IsSuccess, string Output);
