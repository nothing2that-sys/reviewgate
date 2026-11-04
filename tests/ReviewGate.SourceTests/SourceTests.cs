using ReviewGate.Contracts;
using ReviewGate.Core.Sources;

namespace ReviewGate.SourceTests;
public sealed class SourceTests : IDisposable
{
    readonly string root=Path.Combine(AppContext.BaseDirectory,"source-"+Guid.NewGuid().ToString("N"));
    public SourceTests()=>Directory.CreateDirectory(root);
    public void Dispose(){if(Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppContext.BaseDirectory),StringComparison.OrdinalIgnoreCase))Directory.Delete(root,true);}
    (List<SourceFile> Sources,List<Exclusion> Exclusions,List<Chunk> Chunks) Capture(int size=8)
        =>SourceReader.Capture(root,null,["a.txt"],[],Path.Combine(root,"snap"),size,4);
    [Fact] public void Utf8CrLfAndRawOffsetsSurviveChunking()
    {
        File.WriteAllBytes(Path.Combine(root,"a.txt"),[239,187,191,..Data.Utf8.GetBytes("가🙂\r\n끝\rX\u2028")]);
        var c=Capture();var s=Assert.Single(c.Sources);
        Assert.Equal("가🙂\n끝\rX\u2028",File.ReadAllText(s.TextPath));
        Assert.Equal(10,SourceReader.RawOffset(s,7));
        Assert.Equal(12,SourceReader.RawOffset(s,8));
        SourceReader.VerifyPartition(c.Sources,c.Chunks);
        Assert.All(c.Chunks,x=>Assert.True(x.End-x.Start<=8));
    }
    [Fact] public void PartitionChecksSnapshotLengthRatherThanPlannerList()
    {
        File.WriteAllText(Path.Combine(root,"a.txt"),"0123456789ABCDEFGHIJK");var c=Capture();
        Assert.Throws<ReviewError>(()=>SourceReader.VerifyPartition(c.Sources,c.Chunks.Skip(1).ToList()));
    }
    [Theory] [InlineData(new byte[]{0xFF})] [InlineData(new byte[]{0x61,0})]
    public void InvalidBytesAreNotReplaced(byte[] bytes)=>Assert.Throws<ReviewError>(()=>SourceReader.Normalize(bytes));
    [Fact] public void PathEscapeIsRejected()=>Assert.Throws<ReviewError>(()=>SafePath.Input("../outside.txt",root));
    [Fact] public void SecretFilesAreExcludedAndEmptyInputsAreNotComplete()
    {
        File.WriteAllText(Path.Combine(root,"a.txt"),"valid");File.WriteAllText(Path.Combine(root,".env"),"fake");
        var capture=SourceReader.Capture(root,null,["a.txt",".env"],[],Path.Combine(root,"snap"),8,0);
        Assert.Equal("credential_candidate",Assert.Single(capture.Exclusions).Reason);
        File.WriteAllText(Path.Combine(root,"a.txt"),"");Assert.Throws<ReviewError>(()=>Capture());
    }
    [Fact] public void DiffCoordinatesDoNotInventAfterLineForDeletion()
    {
        var lines=UnifiedDiff.Parse("--- a/a.cs\n+++ b/a.cs\n@@ -5,2 +5,2 @@\n-old\n+new\n same\n");
        Assert.Null(lines[0].AfterLine);Assert.Equal(5,lines[1].AfterLine);Assert.Equal(6,lines[2].BeforeLine);
    }
    [Fact] public void DiffHunkContentThatLooksLikeHeadersOrBlankContextIsParsed()
    {
        var lines=UnifiedDiff.Parse("--- a/q.sql\n+++ b/q.sql\n@@ -1,2 +1,2 @@\n--- old comment\n+++ new comment\n same\n");
        Assert.Equal(3,lines.Count);Assert.Equal("-- old comment",lines[0].Text);Assert.Null(lines[0].AfterLine);Assert.Equal("++ new comment",lines[1].Text);Assert.Equal("q.sql",lines[1].NewPath);
        var blank=UnifiedDiff.Parse("--- a/a.cs\n+++ b/a.cs\n@@ -1,3 +1,3 @@\n a\n\n-b\n+c\n-- \n2.45.0\n");
        Assert.Equal(2,blank[1].BeforeLine);Assert.Equal("",blank[1].Text);Assert.Equal(3,blank[3].AfterLine);
    }
    [Fact] public void DirectoryTraversalExcludesUnsupportedFilesButExplicitFileIsRejected()
    {
        Directory.CreateDirectory(Path.Combine(root,"src"));File.WriteAllText(Path.Combine(root,"src","A.cs"),"int a;");File.WriteAllText(Path.Combine(root,"src","App.csproj"),"<Project/>");File.WriteAllText(Path.Combine(root,"src","LICENSE"),"text");
        var c=SourceReader.Capture(root,null,["src"],[],Path.Combine(root,"snap"),8,0);
        Assert.Equal("src/A.cs",Assert.Single(c.Sources).LogicalPath);Assert.Equal(2,c.Exclusions.Count(x=>x.Reason=="unsupported_format"));
        Assert.Equal("unsupported_input_format",Assert.Throws<ReviewError>(()=>SourceReader.Capture(root,null,["src/App.csproj"],[],Path.Combine(root,"snap2"),8,0)).Code);
    }
    [Fact] public void ExplicitInputKeepsOnDiskCase()
    {
        File.WriteAllText(Path.Combine(root,"Mixed.Case.txt"),"x");
        Assert.Equal("Mixed.Case.txt",Assert.Single(SourceReader.Capture(root,null,["mixed.case.TXT"],[],Path.Combine(root,"snap"),8,0).Sources).LogicalPath);
    }
    [Fact] public void DiffCountsAndBinaryAreValidated()
    {
        Assert.Throws<ReviewError>(()=>UnifiedDiff.Parse("--- a/a.cs\n+++ b/a.cs\n@@ -1,2 +1,2 @@\n one\n"));
        Assert.Throws<ReviewError>(()=>UnifiedDiff.Parse("GIT binary patch\n"));
    }
}
