// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Server.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kumbu.Server.Tests.Api;

/// <summary>How <c>/api</c> answers the refusals of files larger than 15 MB (files spec, decisions 11–12).</summary>
public sealed class LargeFileRefusalTests
{
    private static async Task<(int? Status, string RetryAfter, string Message)> AnswerAsync(Exception e)
    {
        var http = new DefaultHttpContext();
        var result = await ApiErrors.Filter(new DefaultEndpointFilterInvocationContext(http), _ => throw e);
        var json = Assert.IsType<JsonHttpResult<ApiError>>(result);
        return (json.StatusCode, http.Response.Headers.RetryAfter.ToString(), json.Value!.Error);
    }

    [Fact]
    public async Task A_file_too_large_to_read_is_a_bad_request_with_its_sentence()
    {
        Assert.Equal((400, "", $"The file is {AttachmentFormat.NotRead}."), await AnswerAsync(new TooLargeToReadException()));
    }
}
