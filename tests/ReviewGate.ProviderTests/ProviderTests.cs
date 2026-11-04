using System.Text.Json.Nodes;
using ReviewGate.Contracts;
using ReviewGate.Providers;
using ReviewGate.Testing;

namespace ReviewGate.ProviderTests;
public sealed class ProviderTests
{
    [Fact] public void LocalSpecificationFixtureHashesArePinned()
    {
        string dir=Path.Combine(TestEnvironment.Repository,"tests","fixtures","provider-specs");var manifest=JsonNode.Parse(File.ReadAllText(Path.Combine(dir,"manifest.json")))!;
        Assert.Equal("2026-11-04",manifest["retrieved_at"]!.GetValue<string>());
        foreach(var item in manifest["documents"]!.AsArray()){string path=Path.Combine(dir,item!["path"]!.GetValue<string>());Assert.Equal(item["sha256"]!.GetValue<string>(),Data.FileHash(path));}
        Assert.Equal(9,manifest["documents"]!.AsArray().Count);
    }
    static ReviewerProfile Reviewer(string provider)=>new(Data.Version,"wire",provider,"synthetic-model",provider=="openai"?"responses":provider=="together"?"chat_completions":"interactions","synthetic/wire");
    [Theory][InlineData("openai")][InlineData("together")][InlineData("google")]
    public void PureMockTransportRoundTripAndProviderSpecificOptions(string provider)
    {
        var r=Reviewer(provider);var body=JsonNode.Parse(WireAdapters.Build(r,"source"))!;
        Assert.Equal(provider=="together"?null:false,body["store"]?.GetValue<bool>());
        if(provider=="openai")Assert.Equal("json_schema",body["text"]?["format"]?["type"]?.GetValue<string>());
        if(provider=="google")Assert.Equal("application/json",body["response_format"]?["mime_type"]?.GetValue<string>());
        string specs=Path.Combine(TestEnvironment.Repository,"tests","fixtures","provider-specs");
        var expected=JsonNode.Parse(File.ReadAllText(Path.Combine(specs,provider+"-request.json")))!.AsObject();
        foreach(var (path,value) in expected){JsonNode? node=body;foreach(string segment in path.Split('/'))node=node is JsonArray a?a[int.Parse(segment,System.Globalization.CultureInfo.InvariantCulture)]:node?[segment];Assert.True(JsonNode.DeepEquals(value,node),path);}
        int sends=0;string MockSend(string request){sends++;Assert.Equal("synthetic-model",JsonNode.Parse(request)?["model"]?.GetValue<string>());return File.ReadAllText(Path.Combine(specs,provider+"-response.json"));}
        var response=WireAdapters.Parse(r,MockSend(body.ToJsonString()));Assert.Equal(1,sends);Assert.Equal("success",response.Status);Assert.Equal("no_finding",response.Disposition);Assert.Equal(100,response.Usage.InputTokens);
    }
    [Theory][InlineData("openai","refused")][InlineData("together","truncated")][InlineData("google","truncated")]
    public void RefusalAndTruncationNeverBecomeNoFinding(string provider,string status)
    {var r=Reviewer(provider);var parsed=WireAdapters.Parse(r,WireAdapters.WrapFixture(r,new(Status:status,Usage:new(100,20))));Assert.Equal(status,parsed.Status);Assert.Equal("not_reported",parsed.Disposition);}
    [Theory][InlineData(401,"unauthorized")][InlineData(429,"rate_limited")][InlineData(503,"error")]
    public void HttpErrorsPreserveClassificationWithoutLeakingBody(int http,string expected)
    {var r=WireAdapters.Parse(Reviewer("openai"),"{\"error\":{\"message\":\"TEST_SECRET_SENTINEL\"}}",http);Assert.Equal(expected,r.Status);Assert.False(r.Usage.Known);Assert.DoesNotContain("TEST_SECRET_SENTINEL",Data.Json(r));}
    // Hand-written wire bodies, not produced by WrapFixture, so serializer and parser cannot share one mistake here.
    [Theory]
    [InlineData("openai","{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"no\"}]}],\"usage\":{\"input_tokens\":3,\"output_tokens\":1}}","refused")]
    [InlineData("openai","{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"},\"output\":[],\"usage\":{\"input_tokens\":3,\"output_tokens\":1}}","truncated")]
    [InlineData("together","{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"status\\\":\"}}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":1}}","truncated")]
    [InlineData("google","{\"status\":\"incomplete\",\"steps\":[],\"usage\":{\"total_input_tokens\":3,\"total_output_tokens\":1,\"total_tokens\":4}}","truncated")]
    [InlineData("together","{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"not json\"}}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":1}}","invalid_payload")]
    public void IndependentWireBodiesKeepNonSuccessStatusAndUsage(string provider,string wire,string expected)
    {var r=WireAdapters.Parse(Reviewer(provider),wire);Assert.Equal(expected,r.Status);Assert.Equal("not_reported",r.Disposition);Assert.True(r.Usage.Known);Assert.Equal(3,r.Usage.InputTokens);}
    [Fact] public void ErrorBodyRedactionKeepsOnlyUsage()
    {var redacted=WireAdapters.RedactErrorBody("{\"error\":{\"message\":\"TEST_SECRET_SENTINEL\"},\"usage\":{\"input_tokens\":1}}");Assert.DoesNotContain("TEST_SECRET_SENTINEL",redacted);Assert.Contains("input_tokens",redacted);}
    [Fact] public void GoogleUnexplainedUsageRemainsUnknown()
    {var r=Reviewer("google");var wire=JsonNode.Parse(WireAdapters.WrapFixture(r,new(Usage:new(100,20))))!;wire["usage"]!["total_tokens"]=130;Assert.False(WireAdapters.Parse(r,wire.ToJsonString()).Usage.Known);}
    [Fact] public void ReasoningIncludedOutputIsNotChargedTwice()
    {Assert.Equal(.00014m,WireAdapters.Cost(new(100,20,ReasoningTokens:10),new("model",1,2,ReasoningPerMillion:999)));Assert.Equal(.00017m,WireAdapters.Cost(new(100,20,ReasoningTokens:10),new("model",1,2,OutputIncludesReasoning:false,ReasoningPerMillion:3)));}
    [Fact] public void MissingUsageCannotBeZeroCostAndBadJsonFails()
    {var r=Reviewer("openai");Assert.False(WireAdapters.Parse(r,WireAdapters.WrapFixture(r,new())).Usage.Known);Assert.Throws<ReviewError>(()=>WireAdapters.Cost(new(0,0,Known:false),new("model",1,2)));Assert.Throws<ReviewError>(()=>WireAdapters.Parse(r,"not-json"));}
    [Fact] public void OversizeRequestIsRejectedWithoutTruncation()
    {Assert.Equal(12,Assert.Throws<ReviewError>(()=>WireAdapters.Build(Reviewer("openai")with{MaxRequestBytes=1024},new string('x',5000))).ExitCode);}
}
