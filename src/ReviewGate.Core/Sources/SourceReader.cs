using System.Text;
using System.Text.RegularExpressions;
using ReviewGate.Contracts;

namespace ReviewGate.Core.Sources;

public static partial class SafePath
{
    public static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Within(string path, string root) => Full(path).Equals(Full(root), StringComparison.OrdinalIgnoreCase)
        || Full(path).StartsWith(Full(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static void NoLinks(string path)
    {
        for (var p = Full(path); p is not null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new ReviewError(2, "reparse_point_not_allowed");
    }
    public static string Input(string path, string root)
    {
        var full = Full(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        Data.Require(Within(full, root), "path_outside_root");
        var relative = Path.GetRelativePath(Path.GetPathRoot(full)!, full);
        foreach (var component in relative.Split(Path.DirectorySeparatorChar))
            Data.Require(!component.Contains(':') && !component.Contains('*') && !component.Contains('?') && !component.EndsWith(' ') && !component.EndsWith('.')
                && !Reserved().IsMatch(component), "invalid_path");
        NoLinks(full);
        return ActualCase(full);
    }
    // Returns the path with each existing component spelled as the directory entry is stored,
    // so logical and repository paths keep the on-disk case rather than the typed case.
    public static string ActualCase(string full)
    {
        string root = Path.GetPathRoot(full)!, current = root;
        foreach (var component in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".") continue;
            string? entry = Directory.Exists(current) ? Directory.EnumerateFileSystemEntries(current, component).FirstOrDefault() : null;
            current = entry ?? Path.Combine(current, component);
        }
        return current;
    }
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Reserved();
}

public static class SourceReader
{
    public static string InventoryHash(string directory)
    {
        SafePath.NoLinks(directory);Data.Require(Directory.Exists(directory),"source_inventory_changed",13);
        var entries=new List<string>();
        void Walk(string path)
        {
            foreach(string item in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
            {
                var attributes=File.GetAttributes(item);string relative=Path.GetRelativePath(directory,item).Replace('\\','/');
                if(ExcludedDirs.Contains(Path.GetFileName(item)))continue;
                entries.Add(relative+":"+(attributes.HasFlag(FileAttributes.ReparsePoint)?"link":Directory.Exists(item)?"dir":"file"));
                if(Directory.Exists(item)&&!attributes.HasFlag(FileAttributes.ReparsePoint))Walk(item);
            }
        }
        Walk(directory);return Data.Hash(Data.Json(entries));
    }
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "bin", "obj", "node_modules", ".packages", ".work", ".vs", ".idea", "__pycache__" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".cs", ".cpp", ".c", ".h", ".hpp", ".py", ".js", ".ts", ".tsx", ".jsx", ".json", ".yaml", ".yml", ".xml", ".xaml", ".ps1", ".diff", ".patch", ".sql", ".ini", ".toml", ".csv", ".html", ".css" };
    public static (List<SourceFile> Sources, List<Exclusion> Exclusions, List<Chunk> Chunks) Capture(
        string root, string? repositoryRoot, IReadOnlyList<string> inputs, IReadOnlyList<string> references,
        string snapshotDir, int chunkBytes, int contextBytes)
    {
        SafePath.NoLinks(root);
        Data.Require(Directory.Exists(root), "source_root_unavailable");
        Data.Require(chunkBytes >= 4 && chunkBytes <= 1_048_576 && contextBytes >= 0 && contextBytes <= chunkBytes, "invalid_chunk_settings");
        var candidates = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var exclusions = new List<Exclusion>();
        void Add(string path, string role, bool fromDirectory = false)
        {
            string full = SafePath.Input(path, root);
            if (Directory.Exists(full))
            {
                foreach (var item in Directory.EnumerateFileSystemEntries(full).Order(StringComparer.Ordinal))
                {
                    string rel = Path.GetRelativePath(root, item).Replace('\\','/');
                    var attrs = File.GetAttributes(item);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) { exclusions.Add(new(rel,"reparse_point")); continue; }
                    if (ExcludedDirs.Contains(Path.GetFileName(item))) { exclusions.Add(new(rel,"generated_or_metadata")); continue; }
                    Add(item, role, true);
                }
                return;
            }
            Data.Require(File.Exists(full), "input_unavailable");
            string name = Path.GetFileName(full).ToLowerInvariant();
            if (name.StartsWith(".env",StringComparison.Ordinal) || name is "secrets.json" or "credentials.json" or "id_rsa" or "id_ed25519"
                || new[]{".pem",".key",".pfx"}.Contains(Path.GetExtension(name)))
            { exclusions.Add(new(Path.GetRelativePath(root,full).Replace('\\','/'),"credential_candidate")); return; }
            // Directory traversal records unsupported files as exclusions; an explicitly named file must be supported.
            if (!Extensions.Contains(Path.GetExtension(full)))
            {
                Data.Require(fromDirectory, "unsupported_input_format");
                exclusions.Add(new(Path.GetRelativePath(root,full).Replace('\\','/'),"unsupported_format")); return;
            }
            Data.Require(candidates.TryAdd(full, role), "duplicate_input");
            Data.Require(candidates.Count <= 2000, "input_limit_exceeded");
        }
        foreach (var p in inputs) Add(p,"target");
        foreach (var p in references) Add(p,"reference");
        var sources = new List<SourceFile>(); var chunks = new List<Chunk>(); long total = 0;
        foreach (var (path,role) in candidates)
        {
            SafePath.NoLinks(path);
            var info = new FileInfo(path); long length = info.Length; var modified = info.LastWriteTimeUtc;
            Data.Require(length <= 10*1024*1024 && total+length <= 100*1024*1024, "input_limit_exceeded");
            var raw = File.ReadAllBytes(path); var verify = File.ReadAllBytes(path); info.Refresh();
            Data.Require(raw.LongLength == length && info.Length == length && info.LastWriteTimeUtc == modified
                && Data.Hash(raw) == Data.Hash(verify), "source_changed", 13);
            total += raw.Length;
            var (text, cr, bom) = Normalize(raw); var normalized = Data.Utf8.GetBytes(text);
            string id = "src_"+Data.Hash(Path.GetRelativePath(root,path).Replace('\\','/')+"\n"+Data.Hash(raw))[..24];
            string rawPath = Path.Combine(snapshotDir,id+".raw"), textPath = Path.Combine(snapshotDir,id+".txt");
            Data.CreateFile(rawPath,raw); Data.CreateFile(textPath,normalized);
            string? export = repositoryRoot is not null && SafePath.Within(path,repositoryRoot)
                ? Path.GetRelativePath(repositoryRoot,path).Replace('\\','/') : null;
            var source = new SourceFile(id,Path.GetRelativePath(root,path).Replace('\\','/'),path,Data.Hash(raw),rawPath,textPath,
                Data.Hash(normalized),normalized.Length,bom,cr,role,export);
            sources.Add(source);
            if (role == "target") chunks.AddRange(Split(source,normalized,chunkBytes,contextBytes));
        }
        Data.Require(chunks.Count > 0,"no_reviewable_content");
        VerifyPartition(sources.Where(s=>s.Role=="target"),chunks);
        return (sources,exclusions,chunks);
    }
    public static (string Text,List<int> RemovedCr,int Bom) Normalize(byte[] raw)
    {
        int bom=raw.AsSpan().StartsWith(new byte[]{239,187,191})?3:0;
        try { _ = Data.Utf8.GetString(raw,bom,raw.Length-bom); }
        catch (DecoderFallbackException) { throw new ReviewError(2,"invalid_utf8"); }
        Data.Require(!raw.Contains((byte)0),"nul_in_text");
        var result=new List<byte>(raw.Length); var cr=new List<int>();
        for(int i=bom;i<raw.Length;i++)
        { if(raw[i]==13 && i+1<raw.Length && raw[i+1]==10){ cr.Add(result.Count);continue; } result.Add(raw[i]); }
        return (Data.Utf8.GetString(result.ToArray()),cr,bom);
    }
    public static long RawOffset(SourceFile source,int normalizedOffset) => source.BomBytes+normalizedOffset+source.RemovedCrOffsets.Count(x=>x<normalizedOffset);
    public static bool Boundary(byte[] data,int offset) => offset>=0 && offset<=data.Length && (offset==data.Length || (data[offset]&0xC0)!=0x80);
    public static List<Chunk> Split(SourceFile source,byte[] bytes,int max,int context)
    {
        var chunks=new List<Chunk>(); int start=0,index=0;
        while(start<bytes.Length)
        {
            int end=Math.Min(bytes.Length,start+max);
            while(!Boundary(bytes,end))end--;
            if(end<bytes.Length){int newline=Array.LastIndexOf(bytes,(byte)10,end-1,end-start);if(newline>=start+max/2)end=newline+1;}
            Data.Require(end>start,"invalid_chunk_boundary");
            int cs=Math.Max(0,start-context),ce=Math.Min(bytes.Length,end+context);
            while(!Boundary(bytes,cs))cs++;while(!Boundary(bytes,ce))ce--;
            chunks.Add(new(source.Id+"_"+index,source.Id,index++,start,end,cs,ce));start=end;
        }
        return chunks;
    }
    public static void VerifyPartition(IEnumerable<SourceFile> sources,IReadOnlyList<Chunk> chunks)
    {
        foreach(var source in sources)
        {
            var bytes=File.ReadAllBytes(source.TextPath); int cursor=0;
            Data.Require(bytes.Length==source.ByteLength && Data.Hash(bytes)==source.TextSha256,"snapshot_corrupt",13);
            foreach(var c in chunks.Where(c=>c.SourceId==source.Id).OrderBy(c=>c.Start))
            {Data.Require(c.Start==cursor && c.End>c.Start && c.End<=bytes.Length && Boundary(bytes,c.Start)&&Boundary(bytes,c.End),"partition_invalid",13);cursor=c.End;}
            Data.Require(cursor==bytes.Length,"partition_invalid",13);
        }
        Data.Require(chunks.All(c=>sources.Any(s=>s.Id==c.SourceId)),"partition_invalid",13);
    }
}
