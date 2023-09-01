using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace AzureInsights
{
    internal static class GitHttpClientExtensions
    {
        public static async IAsyncEnumerable<GitPullRequest[]> StreamPullRequests(
            this GitHttpClient client, 
            string projectName, 
            string repositoryName,
            GitPullRequestSearchCriteria searchCriteria,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var batchSize = 500;
            var skip = 0;

            while (true)
            {
                var pullRequests = await client.GetPullRequestsAsync(
                    projectName, 
                    repositoryName, 
                    searchCriteria, 
                    skip: skip,
                    top: batchSize,
                    cancellationToken: cancellationToken);

                if (pullRequests.Count < batchSize)
                {
                    yield return pullRequests.ToArray();
                    yield break;
                }

                yield return pullRequests.ToArray();
                skip += batchSize;
            }
        }
    }
}
