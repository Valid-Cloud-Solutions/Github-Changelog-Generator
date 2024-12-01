# GitHub Changelog Generator

This command line utility will use your commit history to generate a change log using Open AI's chat GPT. It expects you to use git tags for versioning your application and it will get all commits between the provided tag (or the latest one if not provided), and the previous tag. This tool will determine which GitHub Pull Requests had been merged between those tags, and it will pull in any comments, issues, and other details linked to those PRs into a context. It will send this context to an LLM to ask for an 80 character summary and an emoji. Finally it will format that into Markdown for publication onto your Release tab.

This project expects that you do not commit directly to the main branch and all changes are merged via pull requests.

## Usage

```
  -t, --tag           Target tag version. Defaults to the latest tag.

  -r, --repo          Path to the repository directory. Defaults to the current working directory.

  -o, --openai-key    OpenAI API key. Defaults to the value of the OPENAI_API_KEY environment variable.

  -g, --github-key    GitHub API personal access token. Defaults to the value of the GITHUB_API_KEY environment variable.

  --help              Display this help screen.

  --version           Display version information.
```

## License

We use the [MIT LICENSE](./LICENSE)
