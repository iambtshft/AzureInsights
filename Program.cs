using AzureInsights;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

var connectionUrl = "https://dev.azure.com/<DOMAIN>";
var projectName = "<PROJECT_NAME>";
var repositoryName = "<REPO_NAME>";
var pat = "<PAT_TOKEN>";

var daysToConsider = 32;

var connection = new VssConnection(new Uri(connectionUrl), new VssBasicCredential("pat", pat));
await connection.ConnectAsync();

var projectClient = await connection.GetClientAsync<ProjectHttpClient>();
var project = await projectClient.GetProject(projectName);
if (project == null || project.Name == null)
{
    throw new InvalidOperationException($"Project {projectName} not found");
}

var gitClient = await connection.GetClientAsync<GitHttpClient>();
var repository = await gitClient.GetRepositoryAsync(projectName, repositoryName);
if (repository == null || repository.Name == null)
{
    throw new InvalidOperationException($"Repository {repositoryName} not found");
}

var dateNotOlderThan = DateTime.Now.Date.Subtract(TimeSpan.FromDays(daysToConsider));
var pullRequestsStream = gitClient.StreamPullRequests(
    projectName: projectName, 
    repositoryName: repositoryName, 
    new GitPullRequestSearchCriteria
    {
        Status = PullRequestStatus.All,
        ReviewerId = connection.AuthenticatedIdentity.Id,
        MinTime = dateNotOlderThan,
        MaxTime = DateTime.Now.Date
    });

var reviewerId = connection.AuthorizedIdentity.Id.ToString();
var notReviewedState = 0;
var participatedPRs = new List<GitPullRequest>();

await foreach (var pullRequestBatch in pullRequestsStream)
{
    foreach (var pullRequest in pullRequestBatch)
    {
        var voteId = pullRequest.Reviewers.FirstOrDefault(r => r.Id == reviewerId && r.Vote != notReviewedState);
        if (voteId == null)
        {
            continue;
        }

        participatedPRs.Add(pullRequest);
    }
}

if (participatedPRs.Count == 0)
{
    Console.WriteLine("No reviews found");
    return;
}


var filePath = Path.GetFullPath($"./reviews_{Guid.NewGuid().ToString("N")[..7]}.md");
await using var file = File.CreateText(filePath);
await file.WriteLineAsync($"## Pull requests ({dateNotOlderThan:d}- {DateTime.Now.Date:d})");

foreach (var pr in participatedPRs.OrderByDescending(s => s.CreationDate))
{
    var actions = new List<(string, DateTime)>
    {
        ("Created", pr.CreationDate)
    };

    if (pr.ClosedDate != DateTime.MinValue)
    {
        actions.Add(("Closed", pr.ClosedDate));
    } 

    var threads = await gitClient.GetThreadsAsync(projectName, repositoryName, pr.PullRequestId);

    foreach (var thread in threads.OrderByDescending(s => s.LastUpdatedDate))
    {
        if (thread.Properties == null)
        {
            continue;
        }

        if (thread.Properties.TryGetValue("CodeReviewThreadType", out var threadType))
        {
            if (((string)threadType) != "VoteUpdate")
            {
                continue;
            }

            var votedIdentity = (string)thread.Properties["CodeReviewVotedByIdentity"];
            var identity = thread.Identities[votedIdentity];

            if (identity.Id != connection.AuthorizedIdentity.Id.ToString())
            {
                continue;
            }

            var voteId = (string)thread.Properties["CodeReviewVoteResult"];
            var voteComment = thread.Comments.First(t => t.Content.Contains($"voted {voteId}"));

            actions.Add(($"Marked PR as {ToStatusText(voteId)}", voteComment.PublishedDate));
        }
    }

    await file.WriteLineAsync(
        $"1. [{pr.Title} by {pr.CreatedBy.DisplayName}]({connectionUrl}/{projectName}/_git/{repositoryName}/pullrequest/{pr.PullRequestId})");

    foreach (var (action, date) in actions.OrderByDescending(d => d.Item2))
    {
        await file.WriteLineAsync($"   * {action} on {date:d}");
    }
}

await file.FlushAsync();

Console.WriteLine($"Report saved to {filePath}");

static string ToStatusText(string statusCode)
{
    return statusCode switch
    {
        "10" => "Approved",
        "5" => "Approved with suggestions",
        "0" => "No vote",
        "-5" => "Waiting for author",
        "-10" => "Rejected",
        _ => "Unknown"
    };
}