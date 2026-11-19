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

    [Fact]
    public async Task Too_many_large_uploads_at_once_is_a_503_with_a_minute_to_wait()
    {
        Assert.Equal((503, "60", "Too many large uploads at once; try again in a minute."), await AnswerAsync(new Kumbu.Server.Vaults.TooManyUploadsException()));
    }

    /// <summary>The rate a large body must keep: 8 KiB a second after 10 s, so each 4 MiB chunk arrives well within the
    /// sweep's grace of 10 minutes, where Kestrel's default of 240 bytes a second would let a stalled client hold a slot
    /// for days.</summary>
    [Fact]
    public void A_large_body_must_arrive_at_8_KiB_a_second()
    {
        var http = new DefaultHttpContext();
        var rate = new RateFeature();
        http.Features.Set<Microsoft.AspNetCore.Server.Kestrel.Core.Features.IHttpMinRequestBodyDataRateFeature>(rate);

        FileApi.RequireLargeBodyRate(http);

        Assert.Equal(8 * 1024, rate.MinDataRate!.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(10), rate.MinDataRate.GracePeriod);
        // Without the feature (HTTP/2's refuses a rate, the test host has none) nothing changes.
        FileApi.RequireLargeBodyRate(new DefaultHttpContext());
        var http2 = new DefaultHttpContext();
        http2.Features.Set<Microsoft.AspNetCore.Server.Kestrel.Core.Features.IHttpMinRequestBodyDataRateFeature>(new Http2RateFeature());
        FileApi.RequireLargeBodyRate(http2);
    }

    private sealed class RateFeature : Microsoft.AspNetCore.Server.Kestrel.Core.Features.IHttpMinRequestBodyDataRateFeature
    {
        public Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate? MinDataRate { get; set; }
    }

    /// <summary>As Kestrel's HTTP/2 stream has it: a rate can only be switched off.</summary>
    private sealed class Http2RateFeature : Microsoft.AspNetCore.Server.Kestrel.Core.Features.IHttpMinRequestBodyDataRateFeature
    {
        public Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate? MinDataRate
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }
}
