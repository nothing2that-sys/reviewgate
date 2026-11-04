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
        foreach(var line in text.Replace("\r\n","\n").Split('\n'))
        {
            display++;
            if(line.StartsWith("diff --git ",StringComparison.Ordinal)){CheckEnd();hunk=false;continue;}
            if(line.StartsWith("GIT binary patch",StringComparison.Ordinal)||line.StartsWith("Binary files",StringComparison.Ordinal)||line.StartsWith("@@@",StringComparison.Ordinal))throw new ReviewError(2,"unsupported_diff");
            if(line.StartsWith("--- ",StringComparison.Ordinal)){CheckEnd();oldPath=PathPart(line[4..]);hunk=false;continue;}
            if(line.StartsWith("+++ ",StringComparison.Ordinal)){newPath=PathPart(line[4..]);continue;}
            var m=Header().Match(line);
            if(m.Success){CheckEnd();old=int.Parse(m.Groups[1].Value);next=int.Parse(m.Groups[3].Value);leftOld=m.Groups[2].Success?int.Parse(m.Groups[2].Value):1;leftNew=m.Groups[4].Success?int.Parse(m.Groups[4].Value):1;hunk=true;continue;}
            if(!hunk||line.Length==0||line.StartsWith("\\ No newline",StringComparison.Ordinal))continue;
            if(line[0]==' '){result.Add(new(display,oldPath,newPath,old++,next++,line[1..]));leftOld--;leftNew--;}
            else if(line[0]=='-'){result.Add(new(display,oldPath,newPath,old++,null,line[1..]));leftOld--;}
            else if(line[0]=='+'){result.Add(new(display,oldPath,newPath,null,next++,line[1..]));leftNew--;}
            else throw new ReviewError(2,"invalid_diff");
            Data.Require(leftOld>=0&&leftNew>=0,"invalid_diff");
        }
        CheckEnd();Data.Require(result.Count>0,"invalid_diff");return result;
        void CheckEnd(){if(hunk)Data.Require(leftOld==0&&leftNew==0,"invalid_diff");}
    }
    static string? PathPart(string s)
    {s=s.Split('\t')[0];if(s=="/dev/null")return null;Data.Require(!s.StartsWith('"')&&!s.Contains("..",StringComparison.Ordinal)&&!Path.IsPathRooted(s),"unsupported_diff_path");return s.StartsWith("a/",StringComparison.Ordinal)||s.StartsWith("b/",StringComparison.Ordinal)?s[2..]:s;}
    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex Header();
}
