using ReviewGate.Contracts;

namespace ReviewGate.ContractTests;
public class ContractTests
{
    [Fact] public void UnknownFieldsAreRejected()
    {
        var error = Assert.Throws<ReviewError>(() => Data.Parse<Usage>("{\"input_tokens\":1,\"output_tokens\":2,\"secret\":\"TEST_ONLY_SENTINEL\"}"));
        Assert.Equal(2, error.ExitCode);
        Assert.DoesNotContain("SENTINEL", error.Message);
    }
    [Fact] public void UnsupportedVersionIsRejected() => Assert.Throws<ReviewError>(() => Data.VersionIs("2.0"));
    [Fact] public void EnvelopeIsVersionedAndIsNotHglosRaw()
    {
        var json = Data.Json(new Envelope { Command = "status", Status = "completed" });
        Assert.Contains("\"schema_version\": \"1.0\"", json);
        Assert.DoesNotContain("\"findings\"", json);
        Assert.Contains("synthetic_only", json);
    }
    [Fact] public void ArtifactCreationDoesNotOverwrite()
    {
        var path = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid()+".json");
        try { Data.CreateFile(path,"first"); Assert.Throws<IOException>(()=>Data.CreateFile(path,"second")); Assert.Equal("first",File.ReadAllText(path)); }
        finally { File.Delete(path); }
    }
    [Fact] public void ContractRoundTripsUnicode() => Assert.Equal("가🙂", Data.Parse<Criterion>(Data.Json(new Criterion("x","가🙂"))).Description);
}
