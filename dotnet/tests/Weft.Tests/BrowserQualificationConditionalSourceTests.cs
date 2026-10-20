using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Weft.BrowserQualification;
using Weft.HostPrivacyProbe;

namespace Weft.Tests;

public sealed partial class AuthBrowserHttpsQualificationTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("mode")]
    [InlineData("site")]
    [InlineData("audio")]
    [InlineData("paint")]
    [InlineData("json")]
    [InlineData("text")]
    [InlineData("target")]
    public async Task ConditionalRegistrationRequiresFinalWireAndConsumesMismatch(string category)
    {
        var input = InertAdmissionInput() with { Assets = new() { ["inert.js"] = "inert" } };
        var owner = new BrowserQualificationAdmission(input);
        var grant = InertAdmission() with { RawTarget = "/inert.js", Destination = "script", Mode = "cors", Site = "same-origin", Origin = input.Origin };
        // No early observed browser metadata exists in this private registration.
        owner.Register(grant); var actual = InertContext(grant); int product = 0;
        if (category == "missing") actual.Request.Headers.Remove("Sec-Fetch-Dest");
        if (category == "duplicate") actual.Request.Headers["Sec-Fetch-Dest"] = new StringValues(["script", "script"]);
        if (category == "mode") actual.Request.Headers["Sec-Fetch-Mode"] = "no-cors";
        if (category == "site") actual.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        if (category is "audio" or "paint" or "json" or "text") actual.Request.Headers["Sec-Fetch-Dest"] = category switch { "audio" => "audioworklet", "paint" => "paintworklet", _ => category };
        if (category == "target") actual.Features.Get<IHttpRequestFeature>()!.RawTarget = "/other.js";
        await owner.InvokeAsync(actual, _ => { product++; return Task.CompletedTask; });
        var replay = InertContext(grant);
        await owner.InvokeAsync(replay, _ => { product++; return Task.CompletedTask; });
        var counts = owner.Counts(QualificationReply.Ok());
        Assert.True(product == 0 && actual.Response.StatusCode == 403 && replay.Response.StatusCode == 403
            && !actual.Request.Headers.ContainsKey(BrowserQualificationAdmission.Header) && !replay.Request.Headers.ContainsKey(BrowserQualificationAdmission.Header)
            && counts.ProofUsed == 1 && counts.ProofPending == 0 && counts.AdmissionRefusals == 2);
    }
    [Theory]
    [InlineData("script", "cors", true, true)]
    [InlineData("font", "cors", true, true)]
    [InlineData("style", "no-cors", false, true)]
    [InlineData("image", "no-cors", false, true)]
    [InlineData("script", "cors", false, false)]
    [InlineData("script", "no-cors", false, false)]
    [InlineData("style", "no-cors", true, false)]
    [InlineData("style", "cors", true, false)]
    public void ConditionalSubresourceGrantCouplesModeAndOrigin(string destination, string mode, bool origin, bool allowed)
    {
        var input = InertAdmissionInput() with { Assets = new() { ["inert.js"] = "inert" } };
        var owner = new BrowserQualificationAdmission(input);
        var grant = InertAdmission() with { RawTarget = "/inert.js", Destination = destination, Mode = mode, Site = "same-origin", Origin = origin ? input.Origin : null };
        bool registered = true; try { owner.Register(grant); } catch (QualificationFailure) { registered = false; }
        Assert.Equal(allowed, registered);
    }
    [Fact]
    public async Task ConditionalRegistrationAbsentEarlyMetadataCanOnlyDeliverOnExactFinalTuple()
    {
        var owner = new BrowserQualificationAdmission(InertAdmissionInput()); var grant = InertAdmission(); owner.Register(grant);
        int product = 0; bool stripped = false;
        await owner.InvokeAsync(InertContext(grant), context => { product++; stripped = !context.Request.Headers.ContainsKey(BrowserQualificationAdmission.Header); return Task.CompletedTask; });
        Assert.True(product == 1 && stripped && owner.Counts(QualificationReply.Ok()).ProofPending == 0);
    }
}
