using CommandLine;

public class Options
{
    [Option('t', "tag", Required = false, HelpText = "Target tag version. Defaults to the latest tag.")]
    public string? TargetTag { get; set; } = null;

    [Option('r', "repo", Required = false, HelpText = "Path to the repository directory. Defaults to the current working directory.")]
    public string? RepositoryDirectory { get; set; } = null;

    [Option('o', "openai-key", Required = false, HelpText = "OpenAI API key. Defaults to the value of the OPENAI_API_KEY environment variable.")]
    public string? OpenAiApiKey { get; set; } = null;

    [Option('g', "github-key", Required = false, HelpText = "GitHub API personal access token. Defaults to the value of the GITHUB_API_KEY environment variable.")]
    public string? GitHubApiKey { get; set; } = null;
}
