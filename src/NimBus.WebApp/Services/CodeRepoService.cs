using System;

namespace NimBus.WebApp.Services;

public interface ICodeRepoService
{
    string? GetSearchUrl(string className, string? namespaceName);
    string? CodeRepoUrl { get; }
}

internal sealed class CodeRepoService : ICodeRepoService
{
    public CodeRepoService(string? codeRepoUrl)
    {
        CodeRepoUrl = codeRepoUrl;
    }

    public string? CodeRepoUrl { get; }

    public string? GetSearchUrl(string className, string? namespaceName) =>
        string.IsNullOrWhiteSpace(CodeRepoUrl)
            ? null
            : $"{CodeRepoUrl.TrimEnd('/')}/_search?type=code&text= class:{className} AND namespace:{namespaceName}";
}
