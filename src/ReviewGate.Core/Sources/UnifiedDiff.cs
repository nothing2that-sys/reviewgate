using System.Text.RegularExpressions;
using ReviewGate.Contracts;

namespace ReviewGate.Core.Sources;

public record DiffLine(int DisplayLine,string? OldPath,string? NewPath,int? BeforeLine,int? AfterLine,string Text);
public static partial class UnifiedDiff
{
    public static List<DiffLine> Parse(string text)
    {
        var result=new List<DiffLine>();string? oldPath=null,newPath=null;int old=0,next=0,leftOld=0,leftNew=0;bool hunk=false;
        int display=0;
        var lines=text.Replace("\r\n","\n").Split('\n');
        // The empty element after a final LF is the end of the text, not a blank context line.
        foreach(var line in lines.Length>0&&lines[^1].Length==0?lines[..^1]:lines)
        {
            display++;
            // While a hunk still expects lines, every line is hunk content: removed "-- x" lines appear as
            // "--- x" and added "++ x" lines as "+++ x". An empty line is a whitespace-stripped blank context line.
            if(hunk&&(leftOld>0||leftNew>0))
            {
                if(line.StartsWith('\\'))continue;
                char kind=line.Length==0?' ':line[0];string content=line.Length==0?"":line[1..];
                if(kind==' '){result.Add(new(display,oldPath,newPath,old++,next++,content));leftOld--;leftNew--;}
                else if(kind=='-'){result.Add(new(display,oldPath,newPath,old++,null,content));leftOld--;}
                else if(kind=='+'){result.Add(new(display,oldPath,newPath,null,next++,content));leftNew--;}
                else throw new ReviewError(2,"invalid_diff");
                Data.Require(leftOld>=0&&leftNew>=0,"invalid_diff");
                continue;
            }
            if(line.StartsWith("diff --git ",StringComparison.Ordinal)){CheckEnd();hunk=false;continue;}
            if(line.StartsWith("GIT binary patch",StringComparison.Ordinal)||line.StartsWith("Binary files",StringComparison.Ordinal)||line.StartsWith("@@@",StringComparison.Ordinal))throw new ReviewError(2,"unsupported_diff");
            if(line.StartsWith("--- ",StringComparison.Ordinal)){CheckEnd();oldPath=PathPart(line[4..]);hunk=false;continue;}
            if(line.StartsWith("+++ ",StringComparison.Ordinal)){newPath=PathPart(line[4..]);continue;}
            var m=Header().Match(line);
            if(m.Success){CheckEnd();old=int.Parse(m.Groups[1].Value);next=int.Parse(m.Groups[3].Value);leftOld=m.Groups[2].Success?int.Parse(m.Groups[2].Value):1;leftNew=m.Groups[4].Success?int.Parse(m.Groups[4].Value):1;hunk=true;continue;}
            if(!hunk||line.Length==0||line.StartsWith("\\ No newline",StringComparison.Ordinal))continue;
            if(line=="-- "){hunk=false;continue;} // git format-patch signature separator
            // A completed hunk followed by more content lines means the header counts were wrong.
            Data.Require(line[0] is not (' ' or '-' or '+'),"invalid_diff");
        }
        CheckEnd();Data.Require(result.Count>0,"invalid_diff");return result;
        void CheckEnd(){if(hunk)Data.Require(leftOld==0&&leftNew==0,"invalid_diff");}
    }
    static string? PathPart(string s)
    {s=s.Split('\t')[0];if(s=="/dev/null")return null;Data.Require(!s.StartsWith('"')&&!s.Contains("..",StringComparison.Ordinal)&&!Path.IsPathRooted(s),"unsupported_diff_path");return s.StartsWith("a/",StringComparison.Ordinal)||s.StartsWith("b/",StringComparison.Ordinal)?s[2..]:s;}
    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex Header();
}
