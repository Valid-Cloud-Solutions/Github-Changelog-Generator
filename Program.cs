using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Text;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommandLine;
using Octokit;
using System.Text.Json;

class ChangelogGenerator
{
    static async Task Main(string[] args)
    {
        await Parser.Default.ParseArguments<Options>(args)
        .WithParsedAsync(async options =>
        {
            options.RepositoryDirectory = options.RepositoryDirectory ?? Directory.GetCurrentDirectory();
            // Change to the specified repository directory
            Directory.SetCurrentDirectory(options.RepositoryDirectory);

            options.TargetTag = options.TargetTag ?? GetLatestTag();
            options.OpenAiApiKey = options.OpenAiApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            options.GitHubApiKey = options.GitHubApiKey ?? Environment.GetEnvironmentVariable("GITHUB_API_KEY");


            if (string.IsNullOrEmpty(options.TargetTag))
            {
                Console.WriteLine("No tags found in the repository.");
                return;
            }

            if (string.IsNullOrWhiteSpace(options.OpenAiApiKey))
            {
                Console.WriteLine("Enter your Open API key:");
                options.OpenAiApiKey = Console.ReadLine();
            }

            if (string.IsNullOrWhiteSpace(options.GitHubApiKey))
            {
                Console.WriteLine("Enter your GitHub personal access token:");
                options.GitHubApiKey = Console.ReadLine();
            }

            await Run(options);
        });
    }

    static async Task Run(Options options)
    {
        // Change to the specified repository directory
        if (string.IsNullOrWhiteSpace(options.RepositoryDirectory))
            throw new ArgumentException("Repository directory is required.");
        Directory.SetCurrentDirectory(options.RepositoryDirectory);

        // Fetch the previous tag and commit messages
        if (string.IsNullOrWhiteSpace(options.TargetTag))
            throw new ArgumentException("Target tag is required.");
        string? previousTag = GetPreviousTag(options.TargetTag);
        if (previousTag == null)
        {
            Console.WriteLine("No previous tag found. Using the first commit as the base.");
            return;
        }

        var commitMessages = GetCommitMessages(previousTag, options.TargetTag);
        if (!commitMessages.Any())
        {
            Console.WriteLine("No commit messages found between the specified tags.");
            return;
        }

        // Extract PR numbers
        var prNumbers = ExtractPullRequestNumbers(commitMessages);

        // get owner and repository information
        var (owner, repo) = GetRepositoryInfo();
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        {
            Console.WriteLine("Failed to determine repository owner and name from the remote URL.");
            return;
        }

        // build Github Client
        var client = new GitHubClient(new ProductHeaderValue("ChangelogGenerator"));
        client.Credentials = new Credentials(options.GitHubApiKey);

        // build OpenAI Client
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.OpenAiApiKey}");

        var aiResponses = new List<LlmResponse>();
        var queue = prNumbers.ToList().Select(async prNumber =>
        {
            var details = await fetchPullRequestContext(client, owner, repo, prNumber);
            if (details != null)
            {
                var summary = await SummarizeWithLLM(httpClient, prNumber, details);
                if (summary != null)
                {
                    aiResponses.Add(summary);
                }
            }
        });
        await Task.WhenAll(queue);

        var responses = await MakeLLMResponsesUnique(httpClient, aiResponses);

        if(!validateResponses(aiResponses, responses))
        {
            Console.WriteLine("The responses are not unique. Please try again.");
            return;
        }
        if (responses == null)
        {
            Console.WriteLine(FormatMarkdown(aiResponses));
        }
        else
        {
            Console.WriteLine(FormatMarkdown(responses));
        }

    }

    static bool validateResponses(IEnumerable<LlmResponse>? set1, IEnumerable<LlmResponse>? set2)
    {
        var set1PRs = set1?.Select(response => response.pullRequest).ToHashSet();
        var set2PRs = set2?.Select(response => response.pullRequest).ToHashSet();
        if(set1PRs == null || set2PRs == null) return true;

        var missingInSet2PRs = set1PRs.Except(set2PRs).ToList();
        var missingInSet1PRs = set2PRs.Except(set1PRs).ToList();

        return !(missingInSet2PRs.Any() || missingInSet1PRs.Any());
    }

    static string FormatMarkdown(IEnumerable<LlmResponse> responses)
    {
        return responses.OrderBy(x => x.pullRequest).Aggregate(new StringBuilder(), (builder, response) =>
        {
            builder.AppendLine($"- {response.emoji}  [{response.sentence}](#{response.pullRequest})");
            return builder;
        }).ToString();
    }

    static (string? owner, string? repo) GetRepositoryInfo()
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "remote get-url origin",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var remoteUrl = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            // Parse the remote URL to extract owner and repository name
            // Supports HTTPS and SSH GitHub URLs
            var httpsRegex = new Regex(@"https:\/\/github\.com\/([^\/]+)\/([^\/\.]+)");
            var sshRegex = new Regex(@"git@github\.com:([^\/]+)\/([^\/\.]+)");

            Match match;
            if (remoteUrl.StartsWith("https://"))
            {
                match = httpsRegex.Match(remoteUrl);
            }
            else if (remoteUrl.StartsWith("git@"))
            {
                match = sshRegex.Match(remoteUrl);
            }
            else
            {
                Console.WriteLine("Remote URL does not match expected GitHub formats.");
                return (null, null);
            }

            if (match.Success)
            {
                var owner = match.Groups[1].Value;
                var repo = match.Groups[2].Value;
                return (owner, repo);
            }

            Console.WriteLine("Remote URL does not match expected GitHub formats.");
            return (null, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error retrieving remote URL: {ex.Message}");
            return (null, null);
        }
    }

    static string? GetLatestTag()
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "describe --tags --abbrev=0",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            string? latestTag = process.StandardOutput.ReadLine();
            process.WaitForExit();
            return latestTag;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching the latest tag: {ex.Message}");
            return null;
        }
    }

    static string? GetPreviousTag(string currentTag)
    {
        try
        {
            // Fetch tags sorted by creation date (annotated tags are sorted by commit date)
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "tag --sort=creatordate",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var tags = process.StandardOutput.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            process.WaitForExit();

            // Find the index of the current tag in the sorted list
            int currentIndex = tags.IndexOf(currentTag);

            // Return the previous tag if it exists
            return currentIndex > 0 ? tags[currentIndex - 1] : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching the previous tag: {ex.Message}");
            return null;
        }
    }

    static string[] GetCommitMessages(string previousTag, string currentTag)
    {
        try
        {
            string range = previousTag != null ? $"{previousTag}..{currentTag}" : currentTag;

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"log {range} --pretty=format:\"%s\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error executing git command: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    static int[] ExtractPullRequestNumbers(string[] commitMessages)
    {
        var regex = new Regex(@"Merge pull request #(\d+)");
        return commitMessages
            .Select(msg => regex.Match(msg))
            .Where(match => match.Success)
            .Select(match => int.Parse(match.Groups[1].Value))
            .ToArray();
    }

    static List<int> ExtractLinkedIssues(string prBody)
    {
        var linkedIssues = new List<int>();
        if (string.IsNullOrEmpty(prBody)) return linkedIssues;

        // Regex to find linked issues in PR body (e.g., "Fixes #123" or "Closes #456")
        //var issueRegex = new Regex(@"(?:Fixes|Closes|Resolves|Related to) #(\d+)", RegexOptions.IgnoreCase);
        var issueRegex = new Regex(@"#(\d+)", RegexOptions.IgnoreCase);
        var matches = issueRegex.Matches(prBody);
        foreach (Match match in matches)
        {
            if (int.TryParse(match.Groups[1].Value, out var issueNumber))
            {
                linkedIssues.Add(issueNumber);
            }
        }

        return linkedIssues;
    }

    static async Task<string?> fetchPullRequestContext(GitHubClient client, string owner, string repo, int prNumber)
    {
        var contextBuilder = new System.Text.StringBuilder();
        try
        {
            // Fetch PR details
            var pullRequest = await client.PullRequest.Get(owner, repo, prNumber);
            contextBuilder.AppendLine($"PR #{prNumber}: {pullRequest.Title}");
            contextBuilder.AppendLine(pullRequest.Body);
            contextBuilder.AppendLine();

            // Fetch commits
            var commits = await client.PullRequest.Commits(owner, repo, prNumber);
            contextBuilder.AppendLine("Commits:");
            foreach (var commit in commits)
            {
                contextBuilder.AppendLine($"- {commit.Commit.Message}");
            }
            contextBuilder.AppendLine();

            // Fetch file changes
            var files = await client.PullRequest.Files(owner, repo, prNumber);
            contextBuilder.AppendLine("Files Changed:");
            foreach (var file in files)
            {
                contextBuilder.AppendLine($"- {file.FileName}: {file.Status}");
            }
            contextBuilder.AppendLine();

            // Fetch comments
            var comments = await client.Issue.Comment.GetAllForIssue(owner, repo, prNumber);
            contextBuilder.AppendLine("Comments:");
            foreach (var comment in comments)
            {
                contextBuilder.AppendLine($"- {comment.User.Login}: {comment.Body}");
            }
            contextBuilder.AppendLine();

            // Linked issues
            contextBuilder.AppendLine("Linked Issues:");
            var linkedIssues = ExtractLinkedIssues(pullRequest.Body);
            foreach (var issueNumber in linkedIssues)
            {
                var issue = await client.Issue.Get(owner, repo, issueNumber);
                contextBuilder.AppendLine($"Issue #{issueNumber}: {issue.Title}");
                contextBuilder.AppendLine(issue.Body);
                contextBuilder.AppendLine();

                var issueComments = await client.Issue.Comment.GetAllForIssue(owner, repo, issueNumber);
                contextBuilder.AppendLine("Issue Comments:");
                foreach (var issueComment in issueComments)
                {
                    contextBuilder.AppendLine($"- {issueComment.User.Login}: {issueComment.Body}");
                }
                contextBuilder.AppendLine();
            }

            contextBuilder.AppendLine(new string('-', 80)); // Separator'
            return contextBuilder.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch details for PR #{prNumber}: {ex.Message}");
            return null;
        }
    }

    static async Task<string?> fetchPullRequestDetails(GitHubClient client, string owner, string repo, int prNumber)
    {
        try
        {
            var pullRequest = await client.PullRequest.Get(owner, repo, prNumber);
            var result = $"- #{prNumber}: {pullRequest.Title}";
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch details for PR #{prNumber}: {ex.Message}");
            return null;
        }
    }

    static async Task<LlmResponse?> SummarizeWithLLM(HttpClient httpClient, int prNumber, string context)
    {
        int maxRetries = 3;
        int retryCount = 0;

        while (retryCount < maxRetries)
        {
            try
            {
                var requestPayload = new
                {
                    model = "gpt-4o-mini",
                    messages = new[]
                    {
                        new { role = "system", content = "You are a helpful assistant summarizing pull request details into a changelog for business use. Please provide the output as a plain JSON object without any additional text, formatting, or explanation. The JSON should have two properties: 'sentence' and 'emoji'. The 'sentence' property should contain a one-sentence summary, limited to 80 characters, of the pull request and its associated issues, that is focused on the business value, without including issue or PR numbers. The 'emoji' property should contain a single relevant unicode emoji that represents the changelog entry scentence." },
                        new { role = "user", content = context }
                    }
                };

                var payload = JsonConvert.SerializeObject(requestPayload);
                var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync("https://api.openai.com/v1/chat/completions", content);

                if (response.IsSuccessStatusCode)
                {
                    var responseData = await response.Content.ReadFromJsonAsync<JsonDocument>();
                    var jsonResult = responseData?.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                    if (jsonResult == null)
                    {
                        throw new Exception("Failed to parse the JSON response from the LLM API.");
                    }
                    try
                    {
                        var result = JsonConvert.DeserializeObject<LlmResponse>(jsonResult);
                        if (result == null)
                        {
                            throw new Exception("Failed to parse the JSON response from the LLM API.");
                        }
                        result.pullRequest = prNumber;
                        if (!IsEmoji(result.emoji))
                        {
                            throw new Exception($"The emoji provided is not a valid Unicode emoji. Is {result.emoji}.");
                        }
                        return result;
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"JSON parsing failed during the LLM response handling.", ex);
                    }
                }
                else
                {
                    throw new Exception($"Failed to summarize with LLM. Status Code: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                retryCount++;
                if (retryCount > maxRetries)
                {
                    throw new Exception($"Request failed after {maxRetries} attempts.", ex);
                }
            }
        }

        return null;
    }

    static async Task<IEnumerable<LlmResponse>?> MakeLLMResponsesUnique(HttpClient client, IEnumerable<LlmResponse> responses)
    {
        List<LlmResponse> uniqueResponses;

        var jsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented, // Pretty-print the JSON
            StringEscapeHandling = StringEscapeHandling.Default // Preserve raw Unicode characters
        };
        var outputJson = JsonConvert.SerializeObject(responses, jsonSettings);

        // ==========================

        int maxRetries = 3;
        int retryCount = 0;

        while (retryCount < maxRetries)
        {
            uniqueResponses = new List<LlmResponse>();
            try
            {
                var requestPayload = new
                {
                    model = "gpt-4o-mini",
                    messages = new[]
                    {
                        new { role = "system", content = "You are a helpful assistant summarizing pull request details into a changelog for business use. Provided is a JSON object with each entry in the changelog. Your job is to ensure that all emojis are unique in the JSON object. If there is a problem with the emoji, please provide a new one and update the JSON object. Please provide the output as a plain JSON object without any additional text, formatting, or explanation. The 'emoji' property should contain a single relevant unicode emoji that represents the changelog entry scentence. Do not modify any other properties on the JSON object." },
                        new { role = "user", content = outputJson }
                    }
                };

                var payload = JsonConvert.SerializeObject(requestPayload);
                var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

                var response = await client.PostAsync("https://api.openai.com/v1/chat/completions", content);

                if (response.IsSuccessStatusCode)
                {
                    var responseData = await response.Content.ReadFromJsonAsync<JsonDocument>();
                    var jsonResult = responseData?.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                    if (jsonResult == null)
                    {
                        throw new Exception("Failed to parse the JSON response from the LLM API.");
                    }
                    try
                    {
                        var results = JsonConvert.DeserializeObject<List<LlmResponse>>(jsonResult);
                        if (results == null)
                        {
                            throw new Exception("Failed to parse the JSON response from the LLM API.");
                        }
                        foreach (var result in results)
                        {
                            if (!IsEmoji(result.emoji))
                            {
                                throw new Exception($"The emoji provided is not a valid Unicode emoji. Is {result.emoji}.");
                            }
                            uniqueResponses.Add(result);
                        }
                        return uniqueResponses;
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"JSON parsing failed during the LLM response handling.", ex);
                    }
                }
                else
                {
                    throw new Exception($"Failed to summarize with LLM. Status Code: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                retryCount++;
                if (retryCount > maxRetries)
                {
                    throw new Exception($"Request failed after {maxRetries} attempts.", ex);
                }
            }
        }

        return null;
    }

    static bool IsEmoji(string? input)
    {
        var EmojiPattern = @"[#*0-9]\uFE0F?\u20E3|\u00A9\uFE0F?|[\u00AE\u203C\u2049\u2122\u2139\u2194-\u2199\u21A9\u21AA]\uFE0F?|[\u231A\u231B]|[\u2328\u23CF]\uFE0F?|[\u23E9-\u23EC]|[\u23ED-\u23EF]\uFE0F?|\u23F0|[\u23F1\u23F2]\uFE0F?|\u23F3|[\u23F8-\u23FA\u24C2\u25AA\u25AB\u25B6\u25C0\u25FB\u25FC]\uFE0F?|[\u25FD\u25FE]|[\u2600-\u2604\u260E\u2611]\uFE0F?|[\u2614\u2615]|\u2618\uFE0F?|\u261D(?:\uD83C[\uDFFB-\uDFFF]|\uFE0F)?|[\u2620\u2622\u2623\u2626\u262A\u262E\u262F\u2638-\u263A\u2640\u2642]\uFE0F?|[\u2648-\u2653]|[\u265F\u2660\u2663\u2665\u2666\u2668\u267B\u267E]\uFE0F?|\u267F|\u2692\uFE0F?|\u2693|[\u2694-\u2697\u2699\u269B\u269C\u26A0]\uFE0F?|\u26A1|\u26A7\uFE0F?|[\u26AA\u26AB]|[\u26B0\u26B1]\uFE0F?|[\u26BD\u26BE\u26C4\u26C5]|\u26C8\uFE0F?|\u26CE|[\u26CF\u26D1]\uFE0F?|\u26D3(?:\u200D\uD83D\uDCA5|\uFE0F(?:\u200D\uD83D\uDCA5)?)?|\u26D4|\u26E9\uFE0F?|\u26EA|[\u26F0\u26F1]\uFE0F?|[\u26F2\u26F3]|\u26F4\uFE0F?|\u26F5|[\u26F7\u26F8]\uFE0F?|\u26F9(?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?|\uFE0F(?:\u200D[\u2640\u2642]\uFE0F?)?)?|[\u26FA\u26FD]|\u2702\uFE0F?|\u2705|[\u2708\u2709]\uFE0F?|[\u270A\u270B](?:\uD83C[\uDFFB-\uDFFF])?|[\u270C\u270D](?:\uD83C[\uDFFB-\uDFFF]|\uFE0F)?|\u270F\uFE0F?|[\u2712\u2714\u2716\u271D\u2721]\uFE0F?|\u2728|[\u2733\u2734\u2744\u2747]\uFE0F?|[\u274C\u274E\u2753-\u2755\u2757]|\u2763\uFE0F?|\u2764(?:\u200D(?:\uD83D\uDD25|\uD83E\uDE79)|\uFE0F(?:\u200D(?:\uD83D\uDD25|\uD83E\uDE79))?)?|[\u2795-\u2797]|\u27A1\uFE0F?|[\u27B0\u27BF]|[\u2934\u2935\u2B05-\u2B07]\uFE0F?|[\u2B1B\u2B1C\u2B50\u2B55]|[\u3030\u303D\u3297\u3299]\uFE0F?|\uD83C(?:[\uDC04\uDCCF]|[\uDD70\uDD71\uDD7E\uDD7F]\uFE0F?|[\uDD8E\uDD91-\uDD9A]|\uDDE6\uD83C[\uDDE8-\uDDEC\uDDEE\uDDF1\uDDF2\uDDF4\uDDF6-\uDDFA\uDDFC\uDDFD\uDDFF]|\uDDE7\uD83C[\uDDE6\uDDE7\uDDE9-\uDDEF\uDDF1-\uDDF4\uDDF6-\uDDF9\uDDFB\uDDFC\uDDFE\uDDFF]|\uDDE8\uD83C[\uDDE6\uDDE8\uDDE9\uDDEB-\uDDEE\uDDF0-\uDDF7\uDDFA-\uDDFF]|\uDDE9\uD83C[\uDDEA\uDDEC\uDDEF\uDDF0\uDDF2\uDDF4\uDDFF]|\uDDEA\uD83C[\uDDE6\uDDE8\uDDEA\uDDEC\uDDED\uDDF7-\uDDFA]|\uDDEB\uD83C[\uDDEE-\uDDF0\uDDF2\uDDF4\uDDF7]|\uDDEC\uD83C[\uDDE6\uDDE7\uDDE9-\uDDEE\uDDF1-\uDDF3\uDDF5-\uDDFA\uDDFC\uDDFE]|\uDDED\uD83C[\uDDF0\uDDF2\uDDF3\uDDF7\uDDF9\uDDFA]|\uDDEE\uD83C[\uDDE8-\uDDEA\uDDF1-\uDDF4\uDDF6-\uDDF9]|\uDDEF\uD83C[\uDDEA\uDDF2\uDDF4\uDDF5]|\uDDF0\uD83C[\uDDEA\uDDEC-\uDDEE\uDDF2\uDDF3\uDDF5\uDDF7\uDDFC\uDDFE\uDDFF]|\uDDF1\uD83C[\uDDE6-\uDDE8\uDDEE\uDDF0\uDDF7-\uDDFB\uDDFE]|\uDDF2\uD83C[\uDDE6\uDDE8-\uDDED\uDDF0-\uDDFF]|\uDDF3\uD83C[\uDDE6\uDDE8\uDDEA-\uDDEC\uDDEE\uDDF1\uDDF4\uDDF5\uDDF7\uDDFA\uDDFF]|\uDDF4\uD83C\uDDF2|\uDDF5\uD83C[\uDDE6\uDDEA-\uDDED\uDDF0-\uDDF3\uDDF7-\uDDF9\uDDFC\uDDFE]|\uDDF6\uD83C\uDDE6|\uDDF7\uD83C[\uDDEA\uDDF4\uDDF8\uDDFA\uDDFC]|\uDDF8\uD83C[\uDDE6-\uDDEA\uDDEC-\uDDF4\uDDF7-\uDDF9\uDDFB\uDDFD-\uDDFF]|\uDDF9\uD83C[\uDDE6\uDDE8\uDDE9\uDDEB-\uDDED\uDDEF-\uDDF4\uDDF7\uDDF9\uDDFB\uDDFC\uDDFF]|\uDDFA\uD83C[\uDDE6\uDDEC\uDDF2\uDDF3\uDDF8\uDDFE\uDDFF]|\uDDFB\uD83C[\uDDE6\uDDE8\uDDEA\uDDEC\uDDEE\uDDF3\uDDFA]|\uDDFC\uD83C[\uDDEB\uDDF8]|\uDDFD\uD83C\uDDF0|\uDDFE\uD83C[\uDDEA\uDDF9]|\uDDFF\uD83C[\uDDE6\uDDF2\uDDFC]|\uDE01|\uDE02\uFE0F?|[\uDE1A\uDE2F\uDE32-\uDE36]|\uDE37\uFE0F?|[\uDE38-\uDE3A\uDE50\uDE51\uDF00-\uDF20]|[\uDF21\uDF24-\uDF2C]\uFE0F?|[\uDF2D-\uDF35]|\uDF36\uFE0F?|[\uDF37-\uDF43]|\uDF44(?:\u200D\uD83D\uDFEB)?|[\uDF45-\uDF4A]|\uDF4B(?:\u200D\uD83D\uDFE9)?|[\uDF4C-\uDF7C]|\uDF7D\uFE0F?|[\uDF7E-\uDF84]|\uDF85(?:\uD83C[\uDFFB-\uDFFF])?|[\uDF86-\uDF93]|[\uDF96\uDF97\uDF99-\uDF9B\uDF9E\uDF9F]\uFE0F?|[\uDFA0-\uDFC1]|\uDFC2(?:\uD83C[\uDFFB-\uDFFF])?|\uDFC3(?:\u200D(?:[\u2640\u2642](?:\u200D\u27A1\uFE0F?|\uFE0F(?:\u200D\u27A1\uFE0F?)?)?|\u27A1\uFE0F?)|\uD83C[\uDFFB-\uDFFF](?:\u200D(?:[\u2640\u2642](?:\u200D\u27A1\uFE0F?|\uFE0F(?:\u200D\u27A1\uFE0F?)?)?|\u27A1\uFE0F?))?)?|\uDFC4(?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|[\uDFC5\uDFC6]|\uDFC7(?:\uD83C[\uDFFB-\uDFFF])?|[\uDFC8\uDFC9]|\uDFCA(?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|[\uDFCB\uDFCC](?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?|\uFE0F(?:\u200D[\u2640\u2642]\uFE0F?)?)?|[\uDFCD\uDFCE]\uFE0F?|[\uDFCF-\uDFD3]|[\uDFD4-\uDFDF]\uFE0F?|[\uDFE0-\uDFF0]|\uDFF3(?:\u200D(?:\u26A7\uFE0F?|\uD83C\uDF08)|\uFE0F(?:\u200D(?:\u26A7\uFE0F?|\uD83C\uDF08))?)?|\uDFF4(?:\u200D\u2620\uFE0F?|\uDB40\uDC67\uDB40\uDC62\uDB40(?:\uDC65\uDB40\uDC6E\uDB40\uDC67|\uDC73\uDB40\uDC63\uDB40\uDC74|\uDC77\uDB40\uDC6C\uDB40\uDC73)\uDB40\uDC7F)?|[\uDFF5\uDFF7]\uFE0F?|[\uDFF8-\uDFFF])|\uD83D(?:[\uDC00-\uDC07]|\uDC08(?:\u200D\u2B1B)?|[\uDC09-\uDC14]|\uDC15(?:\u200D\uD83E\uDDBA)?|[\uDC16-\uDC25]|\uDC26(?:\u200D(?:\u2B1B|\uD83D\uDD25))?|[\uDC27-\uDC3A]|\uDC3B(?:\u200D\u2744\uFE0F?)?|[\uDC3C-\uDC3E]|\uDC3F\uFE0F?|\uDC40|\uDC41(?:\u200D\uD83D\uDDE8\uFE0F?|\uFE0F(?:\u200D\uD83D\uDDE8\uFE0F?)?)?|[\uDC42\uDC43](?:\uD83C[\uDFFB-\uDFFF])?|[\uDC44\uDC45]|[\uDC46-\uDC50](?:\uD83C[\uDFFB-\uDFFF])?|[\uDC51-\uDC65]|[\uDC66\uDC67](?:\uD83C[\uDFFB-\uDFFF])?|\uDC68(?:\u200D(?:[\u2695\u2696\u2708]\uFE0F?|\u2764\uFE0F?\u200D\uD83D(?:\uDC8B\u200D\uD83D)?\uDC68|\uD83C[\uDF3E\uDF73\uDF7C\uDF93\uDFA4\uDFA8\uDFEB\uDFED]|\uD83D(?:\uDC66(?:\u200D\uD83D\uDC66)?|\uDC67(?:\u200D\uD83D[\uDC66\uDC67])?|[\uDC68\uDC69]\u200D\uD83D(?:\uDC66(?:\u200D\uD83D\uDC66)?|\uDC67(?:\u200D\uD83D[\uDC66\uDC67])?)|[\uDCBB\uDCBC\uDD27\uDD2C\uDE80\uDE92])|\uD83E(?:\uDDAF(?:\u200D\u27A1\uFE0F?)?|[\uDDB0-\uDDB3]|[\uDDBC\uDDBD](?:\u200D\u27A1\uFE0F?)?))|\uD83C(?:\uDFFB(?:\u200D(?:[\u2695\u2696\u2708]\uFE0F?|\u2764\uFE0F?\u200D\uD83D(?:\uDC8B\u200D\uD83D)?\uDC68\uD83C[\uDFFB-\uDFFF]|\uD83C[\uDF3E\uDF73\uDF7C\uDF93\uDFA4\uDFA8\uDFEB\uDFED]|\uD83D[\uDCBB\uDCBC\uDD27\uDD2C\uDE80\uDE92]|\uD83E(?:\uDD1D\u200D\uD83D\uDC68\uD83C[\uDFFC-\uDFFF]|\uDDAF(?:\u200D\u27A1\uFE0F?)?|[\uDDB0-\uDDB3]|[\uDDBC\uDDBD](?:\u200D\u27A1\uFE0F?)?)))?|\uDFFC(?:\u200D(?:[\u2695\u2696\u2708]\uFE0F?|\u2764\uFE0F?\u200D\uD83D(?:\uDC8B\u200D\uD83D)?\uDC68\uD83C[\uDFFB-\uDFFF]|\uD83C[\uDF3E\uDF73\uDF7C\uDF93\uDFA4\uDFA8\uDFEB\uDFED]|\uD83D[\uDCBB\uDCBC\uDD27\uDD2C\uDE80\uDE92]|\uD83E(?:\uDD1D\u200D\uD83D\uDC68\uD83C[\uDFFB\uDFFD-\uDFFF]|\uDDAF(?:\u200D\u27A1\uFE0F?)?|[\uDDB0-\uDDB3]|[\uDDBC\uDDBD](?:\u200D\u27A1\uFE0F?)?)))?|\uDFFD(?:\u200D(?:[\u2695\u2696\u2708]\uFE0F?|\u2764\uFE0F?\u200D\uD83D(?:\uDC8B\u200D\uD83D)?\uDC68\uD83C[\uDFFB-\uDFFF]|\uD83C[\uDF3E\uDF73\uDF7C\uDF93\uDFA4\uDFA8\uDFEB\uDFED]|\uD83D[\uDCBB\uDCBC\uDD27\uDD2C\uDE80\uDE92]|\uD83E(?:\uDD1D\u200D\uD83D\uDC68\uD83C[\uDFFB\uDFFC\uDFFE\uDFFF]|\uDDAF(?:\u200D\u27A1\uFE0F?)?|[\uDDB0-\uDDB3]|[\uDDBC\uDDBD](?:\u200D\u27A1\uFE0F?)?)))?|\uDFFE(?:\u200D(?:[\u2695\u2696\u2708]\uFE0F?|\u2764\uFE0F?\u200D\uD83D(?:\uDC8B\u200D\uD83D)?\uDC68\uD83C[\uDFFB-\uDFFF]|\uD83C[\uDF3E\uDF73\uDF7C\uDF93\uDFA4\uDFA8\uDFEB\uDFED]|\uD83D[\uDCBB\uDCBC\uDD27\uDD2C\uDE80\uDE92]|\uD83E(?:\uDD1D\u200D\uD83D\uDC68\uD83C[\uDFFB-\uDFFD\uDFFF]|\uDDAF(?:\u200D\u27A1\uFE0F?)?|[\uDDB0-\uDDB3]|[\uDDBC\uDDBD](?:\u200D\u27A1\uFE0F?)?)))?|\uDFFF(?:\u200D(?:[\u2695\u2696\u2708]\uFE0F?|\u2764\uFE0F?\u200D\uD83D(?:\uDC8B\u200D\uD83D)?\uDC68\uD83C[\uDFFB-\uDFFF]|\uD83C[\uDF3E\uDF73\uDF7C\uDF93\uDFA4\uDFA8\uDFEB\uDFED]|\uD83D[\uDCBB\uDCBC\uDD27\uDD2C\uDE80\uDE92]|\uD83E(?:\uDD1D\u200D\uD83D\uDC68\uD83C[\uDFFB-\uDFFE]|\uDDAF(?:\u200D\u27A1\uFE0F?)?|[\uDDB0-\uDDB3]|[\uDDBC\uDDBD](?:\u200D\u27A1\uFE0F?)?)))?))?|\uDC69(?:\u200D(?:[\u2695\u2696\u2708]\uFE0F?|\u2764\uFE0F?\u200D\uD83D(?:\uDC8B\u200D\uD83D)?[\uDC68\uDC69]|\uD83C[\uDF3E\uDF73\uDF7C\uDF93\uDFA4\uDFA8\uDFEB\uDFED]|\uD83D(?:\uDC66(?:\u200D\uD83D\uDC66)?|\uDC67(?:\u200D\uD83D[\uDC66\uDC67])?|\uDC69\u200D\uD83D(?:\uDC66(?:\u200D\uD83D\uDC66)?|\uDC67(?:\u200D\uD83D[\uDC66\uDC67])?)|[\uDCBB\uDCBC\uDD27\uDD2C\uDE80\uDE92])|\uD83E(?:\uDDAF(?:\u200D\u27A1\uFE0F?)?|[\uDDB0-\uDDB3]|[\uDDBC\uDDBD](?:\u200D\u27A1\uFE0F?)?))|\uD83C(?:\uDFFB(?:\u200D(?:[\u2695\u2696\u2708]\uFE0F?|\u2764\uFE0F?\u200D\uD83D(?:[\uDC68\uDC69]\uD83C[\uDFFB-\uDFFF]|\uDC8B\u200D\uD83D[\uDC68\uDC69]\uD83C[\uDFFB-\uDFFF])|\uD83C[\uDF3E\uDF73\uDF7C\uDF93\uDFA4\uDFA8\uDFEB\uDFED]|\uD83D[\uDCBB\uDCBC\uDD27\uDD2C\uDE80\uDE92]|\uD83E(?:\uDD1D\u200D\uD83D[\uDC68\uDC69]\uD83C[\uDFFC-\uDFFF]|\uDDAF(?:\u200D\u27A1\uFE0F?)?|[\uDDB0-\uDDB3]|[\uDDBC\uDDBD](?:\u200D\u27A1\uFE0F?)?)))?|\uDFFC(?:\u200D(?:[\u2695\u2696\u2708]\uFE0F?|\u2764\uFE0F?\u200D\uD83D(?:[\uDC68\uDC69]\uD83C[\uDFFB-\uDFFF]|\uDC8B\u200D\uD83D[\uDC68\uDC69]\uD83C[\uDFFB-\uDFFF])|\uD83C[\uDF3E\uDF73\uDF7C\uDF93\uDFA4\uDFA8\uDFEB\uDFED]|\uD83D[\uDCBB\uDCBC\uDD27\uDD2C\uDE80\uDE92]|\uD83E(?:\uDD1D\u200D\uD83D[\uDC68\uDC69]\uD83C[\uDFFB\uDFFD-\uDFFF]|\uDDAF(?:\u200D\u27A1\uFE0F?)?|[\uDDB0-\uDDB3]|[\uDDBC\uDDBD](?:\u200D\u27A1\uFE0F?)?)))?|\uDFFD(?:\u200D(?:[\u2695\u2696\u2708]\uFE0F?|\u2764\uFE0F?\u200D\uD83D(?:[\uDC68\uDC69]\uD83C[\uDFFB-\uDFFF]|\uDC8B\u200D\uD83D[\uDC68\uDC69]\uD83C[\uDFFB-\uDFFF])|\uD83C[\uDF3E\uDF73\uDF7C\uDF93\uDFA4\uDFA8\uDFEB\uDFED]|\uD83D[\uDCBB\uDCBC\uDD27\uDD2C\uDE80\uDE92]|\uD83E(?:\uDD1D\u200D\uD83D[\uDC68\uDC69]\uD83C[\uDFFB\uDFFC\uDFFE\uDFFF]|\uDDAF(?:\u200D\u27A1\uFE0F?)?|[\uDDB0-\uDDB3]|[\uDDBC\uDDBD](?:\u200D\u27A1\uFE0F?)?)))?|\uDFFE(?:\u200D(?:[\u2695\u2696\u2708]\uFE0F?|\u2764\uFE0F?\u200D\uD83D(?:[\uDC68\uDC69]\uD83C[\uDFFB-\uDFFF]|\uDC8B\u200D\uD83D[\uDC68\uDC69]\uD83C[\uDFFB-\uDFFF])|\uD83C[\uDF3E\uDF73\uDF7C\uDF93\uDFA4\uDFA8\uDFEB\uDFED]|\uD83D[\uDCBB\uDCBC\uDD27\uDD2C\uDE80\uDE92]|\uD83E(?:\uDD1D\u200D\uD83D[\uDC68\uDC69]\uD83C[\uDFFB-\uDFFD\uDFFF]|\uDDAF(?:\u200D\u27A1\uFE0F?)?|[\uDDB0-\uDDB3]|[\uDDBC\uDDBD](?:\u200D\u27A1\uFE0F?)?)))?|\uDFFF(?:\u200D(?:[\u2695\u2696\u2708]\uFE0F?|\u2764\uFE0F?\u200D\uD83D(?:[\uDC68\uDC69]\uD83C[\uDFFB-\uDFFF]|\uDC8B\u200D\uD83D[\uDC68\uDC69]\uD83C[\uDFFB-\uDFFF])|\uD83C[\uDF3E\uDF73\uDF7C\uDF93\uDFA4\uDFA8\uDFEB\uDFED]|\uD83D[\uDCBB\uDCBC\uDD27\uDD2C\uDE80\uDE92]|\uD83E(?:\uDD1D\u200D\uD83D[\uDC68\uDC69]\uD83C[\uDFFB-\uDFFE]|\uDDAF(?:\u200D\u27A1\uFE0F?)?|[\uDDB0-\uDDB3]|[\uDDBC\uDDBD](?:\u200D\u27A1\uFE0F?)?)))?))?|\uDC6A|[\uDC6B-\uDC6D](?:\uD83C[\uDFFB-\uDFFF])?|\uDC6E(?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|\uDC6F(?:\u200D[\u2640\u2642]\uFE0F?)?|[\uDC70\uDC71](?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|\uDC72(?:\uD83C[\uDFFB-\uDFFF])?|\uDC73(?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|[\uDC74-\uDC76](?:\uD83C[\uDFFB-\uDFFF])?|\uDC77(?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|\uDC78(?:\uD83C[\uDFFB-\uDFFF])?|[\uDC79-\uDC7B]|\uDC7C(?:\uD83C[\uDFFB-\uDFFF])?|[\uDC7D-\uDC80]|[\uDC81\uDC82](?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|\uDC83(?:\uD83C[\uDFFB-\uDFFF])?|\uDC84|\uDC85(?:\uD83C[\uDFFB-\uDFFF])?|[\uDC86\uDC87](?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|[\uDC88-\uDC8E]|\uDC8F(?:\uD83C[\uDFFB-\uDFFF])?|\uDC90|\uDC91(?:\uD83C[\uDFFB-\uDFFF])?|[\uDC92-\uDCA9]|\uDCAA(?:\uD83C[\uDFFB-\uDFFF])?|[\uDCAB-\uDCFC]|\uDCFD\uFE0F?|[\uDCFF-\uDD3D]|[\uDD49\uDD4A]\uFE0F?|[\uDD4B-\uDD4E\uDD50-\uDD67]|[\uDD6F\uDD70\uDD73]\uFE0F?|\uDD74(?:\uD83C[\uDFFB-\uDFFF]|\uFE0F)?|\uDD75(?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?|\uFE0F(?:\u200D[\u2640\u2642]\uFE0F?)?)?|[\uDD76-\uDD79]\uFE0F?|\uDD7A(?:\uD83C[\uDFFB-\uDFFF])?|[\uDD87\uDD8A-\uDD8D]\uFE0F?|\uDD90(?:\uD83C[\uDFFB-\uDFFF]|\uFE0F)?|[\uDD95\uDD96](?:\uD83C[\uDFFB-\uDFFF])?|\uDDA4|[\uDDA5\uDDA8\uDDB1\uDDB2\uDDBC\uDDC2-\uDDC4\uDDD1-\uDDD3\uDDDC-\uDDDE\uDDE1\uDDE3\uDDE8\uDDEF\uDDF3\uDDFA]\uFE0F?|[\uDDFB-\uDE2D]|\uDE2E(?:\u200D\uD83D\uDCA8)?|[\uDE2F-\uDE34]|\uDE35(?:\u200D\uD83D\uDCAB)?|\uDE36(?:\u200D\uD83C\uDF2B\uFE0F?)?|[\uDE37-\uDE41]|\uDE42(?:\u200D[\u2194\u2195]\uFE0F?)?|[\uDE43\uDE44]|[\uDE45-\uDE47](?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|[\uDE48-\uDE4A]|\uDE4B(?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|\uDE4C(?:\uD83C[\uDFFB-\uDFFF])?|[\uDE4D\uDE4E](?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|\uDE4F(?:\uD83C[\uDFFB-\uDFFF])?|[\uDE80-\uDEA2]|\uDEA3(?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|[\uDEA4-\uDEB3]|[\uDEB4\uDEB5](?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|\uDEB6(?:\u200D(?:[\u2640\u2642](?:\u200D\u27A1\uFE0F?|\uFE0F(?:\u200D\u27A1\uFE0F?)?)?|\u27A1\uFE0F?)|\uD83C[\uDFFB-\uDFFF](?:\u200D(?:[\u2640\u2642](?:\u200D\u27A1\uFE0F?|\uFE0F(?:\u200D\u27A1\uFE0F?)?)?|\u27A1\uFE0F?))?)?|[\uDEB7-\uDEBF]|\uDEC0(?:\uD83C[\uDFFB-\uDFFF])?|[\uDEC1-\uDEC5]|\uDECB\uFE0F?|\uDECC(?:\uD83C[\uDFFB-\uDFFF])?|[\uDECD-\uDECF]\uFE0F?|[\uDED0-\uDED2\uDED5-\uDED7\uDEDC-\uDEDF]|[\uDEE0-\uDEE5\uDEE9]\uFE0F?|[\uDEEB\uDEEC]|[\uDEF0\uDEF3]\uFE0F?|[\uDEF4-\uDEFC\uDFE0-\uDFEB\uDFF0])|\uD83E(?:\uDD0C(?:\uD83C[\uDFFB-\uDFFF])?|[\uDD0D\uDD0E]|\uDD0F(?:\uD83C[\uDFFB-\uDFFF])?|[\uDD10-\uDD17]|[\uDD18-\uDD1F](?:\uD83C[\uDFFB-\uDFFF])?|[\uDD20-\uDD25]|\uDD26(?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|[\uDD27-\uDD2F]|[\uDD30-\uDD34](?:\uD83C[\uDFFB-\uDFFF])?|\uDD35(?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|\uDD36(?:\uD83C[\uDFFB-\uDFFF])?|[\uDD37-\uDD39](?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|\uDD3A|\uDD3C(?:\u200D[\u2640\u2642]\uFE0F?)?|[\uDD3D\uDD3E](?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|[\uDD3F-\uDD45\uDD47-\uDD76]|\uDD77(?:\uD83C[\uDFFB-\uDFFF])?|[\uDD78-\uDDB4]|[\uDDB5\uDDB6](?:\uD83C[\uDFFB-\uDFFF])?|\uDDB7|[\uDDB8\uDDB9](?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|\uDDBA|\uDDBB(?:\uD83C[\uDFFB-\uDFFF])?|[\uDDBC-\uDDCC]|\uDDCD(?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|\uDDCE(?:\u200D(?:[\u2640\u2642](?:\u200D\u27A1\uFE0F?|\uFE0F(?:\u200D\u27A1\uFE0F?)?)?|\u27A1\uFE0F?)|\uD83C[\uDFFB-\uDFFF](?:\u200D(?:[\u2640\u2642](?:\u200D\u27A1\uFE0F?|\uFE0F(?:\u200D\u27A1\uFE0F?)?)?|\u27A1\uFE0F?))?)?|\uDDCF(?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|\uDDD0|\uDDD1(?:\u200D(?:[\u2695\u2696\u2708]\uFE0F?|\uD83C[\uDF3E\uDF73\uDF7C\uDF84\uDF93\uDFA4\uDFA8\uDFEB\uDFED]|\uD83D[\uDCBB\uDCBC\uDD27\uDD2C\uDE80\uDE92]|\uD83E(?:\uDD1D\u200D\uD83E\uDDD1|\uDDAF(?:\u200D\u27A1\uFE0F?)?|[\uDDB0-\uDDB3]|[\uDDBC\uDDBD](?:\u200D\u27A1\uFE0F?)?|(?:\uDDD1\u200D\uD83E)?\uDDD2(?:\u200D\uD83E\uDDD2)?))|\uD83C(?:\uDFFB(?:\u200D(?:[\u2695\u2696\u2708]\uFE0F?|\u2764\uFE0F?\u200D(?:\uD83D\uDC8B\u200D)?\uD83E\uDDD1\uD83C[\uDFFC-\uDFFF]|\uD83C[\uDF3E\uDF73\uDF7C\uDF84\uDF93\uDFA4\uDFA8\uDFEB\uDFED]|\uD83D[\uDCBB\uDCBC\uDD27\uDD2C\uDE80\uDE92]|\uD83E(?:\uDD1D\u200D\uD83E\uDDD1\uD83C[\uDFFB-\uDFFF]|\uDDAF(?:\u200D\u27A1\uFE0F?)?|[\uDDB0-\uDDB3]|[\uDDBC\uDDBD](?:\u200D\u27A1\uFE0F?)?)))?|\uDFFC(?:\u200D(?:[\u2695\u2696\u2708]\uFE0F?|\u2764\uFE0F?\u200D(?:\uD83D\uDC8B\u200D)?\uD83E\uDDD1\uD83C[\uDFFB\uDFFD-\uDFFF]|\uD83C[\uDF3E\uDF73\uDF7C\uDF84\uDF93\uDFA4\uDFA8\uDFEB\uDFED]|\uD83D[\uDCBB\uDCBC\uDD27\uDD2C\uDE80\uDE92]|\uD83E(?:\uDD1D\u200D\uD83E\uDDD1\uD83C[\uDFFB-\uDFFF]|\uDDAF(?:\u200D\u27A1\uFE0F?)?|[\uDDB0-\uDDB3]|[\uDDBC\uDDBD](?:\u200D\u27A1\uFE0F?)?)))?|\uDFFD(?:\u200D(?:[\u2695\u2696\u2708]\uFE0F?|\u2764\uFE0F?\u200D(?:\uD83D\uDC8B\u200D)?\uD83E\uDDD1\uD83C[\uDFFB\uDFFC\uDFFE\uDFFF]|\uD83C[\uDF3E\uDF73\uDF7C\uDF84\uDF93\uDFA4\uDFA8\uDFEB\uDFED]|\uD83D[\uDCBB\uDCBC\uDD27\uDD2C\uDE80\uDE92]|\uD83E(?:\uDD1D\u200D\uD83E\uDDD1\uD83C[\uDFFB-\uDFFF]|\uDDAF(?:\u200D\u27A1\uFE0F?)?|[\uDDB0-\uDDB3]|[\uDDBC\uDDBD](?:\u200D\u27A1\uFE0F?)?)))?|\uDFFE(?:\u200D(?:[\u2695\u2696\u2708]\uFE0F?|\u2764\uFE0F?\u200D(?:\uD83D\uDC8B\u200D)?\uD83E\uDDD1\uD83C[\uDFFB-\uDFFD\uDFFF]|\uD83C[\uDF3E\uDF73\uDF7C\uDF84\uDF93\uDFA4\uDFA8\uDFEB\uDFED]|\uD83D[\uDCBB\uDCBC\uDD27\uDD2C\uDE80\uDE92]|\uD83E(?:\uDD1D\u200D\uD83E\uDDD1\uD83C[\uDFFB-\uDFFF]|\uDDAF(?:\u200D\u27A1\uFE0F?)?|[\uDDB0-\uDDB3]|[\uDDBC\uDDBD](?:\u200D\u27A1\uFE0F?)?)))?|\uDFFF(?:\u200D(?:[\u2695\u2696\u2708]\uFE0F?|\u2764\uFE0F?\u200D(?:\uD83D\uDC8B\u200D)?\uD83E\uDDD1\uD83C[\uDFFB-\uDFFE]|\uD83C[\uDF3E\uDF73\uDF7C\uDF84\uDF93\uDFA4\uDFA8\uDFEB\uDFED]|\uD83D[\uDCBB\uDCBC\uDD27\uDD2C\uDE80\uDE92]|\uD83E(?:\uDD1D\u200D\uD83E\uDDD1\uD83C[\uDFFB-\uDFFF]|\uDDAF(?:\u200D\u27A1\uFE0F?)?|[\uDDB0-\uDDB3]|[\uDDBC\uDDBD](?:\u200D\u27A1\uFE0F?)?)))?))?|[\uDDD2\uDDD3](?:\uD83C[\uDFFB-\uDFFF])?|\uDDD4(?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|\uDDD5(?:\uD83C[\uDFFB-\uDFFF])?|[\uDDD6-\uDDDD](?:\u200D[\u2640\u2642]\uFE0F?|\uD83C[\uDFFB-\uDFFF](?:\u200D[\u2640\u2642]\uFE0F?)?)?|[\uDDDE\uDDDF](?:\u200D[\u2640\u2642]\uFE0F?)?|[\uDDE0-\uDDFF\uDE70-\uDE7C\uDE80-\uDE89\uDE8F-\uDEC2]|[\uDEC3-\uDEC5](?:\uD83C[\uDFFB-\uDFFF])?|[\uDEC6\uDECE-\uDEDC\uDEDF-\uDEE9]|\uDEF0(?:\uD83C[\uDFFB-\uDFFF])?|\uDEF1(?:\uD83C(?:\uDFFB(?:\u200D\uD83E\uDEF2\uD83C[\uDFFC-\uDFFF])?|\uDFFC(?:\u200D\uD83E\uDEF2\uD83C[\uDFFB\uDFFD-\uDFFF])?|\uDFFD(?:\u200D\uD83E\uDEF2\uD83C[\uDFFB\uDFFC\uDFFE\uDFFF])?|\uDFFE(?:\u200D\uD83E\uDEF2\uD83C[\uDFFB-\uDFFD\uDFFF])?|\uDFFF(?:\u200D\uD83E\uDEF2\uD83C[\uDFFB-\uDFFE])?))?|[\uDEF2-\uDEF8](?:\uD83C[\uDFFB-\uDFFF])?)";


        if (string.IsNullOrEmpty(input)) return false;
        var singleGrapheme = new StringInfo(input).LengthInTextElements == 1;
        if (!singleGrapheme) return false;

        var pattern = $@"^(?:{EmojiPattern})+\z";
        Regex regex = new Regex(pattern);
        return regex.IsMatch(input);
    }
}

public class LlmResponse
{
    public int pullRequest { get; set; }
    public string? sentence { get; set; }
    public string? emoji { get; set; }
}