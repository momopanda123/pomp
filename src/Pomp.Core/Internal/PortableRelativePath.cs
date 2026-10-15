using System;
using System.Linq;
namespace Pomp.Internal
{
    /// <summary>Conservative portable relative file names, excluding Windows devices, streams and traversal.</summary>
    internal static class PortableRelativePath
    {
        public static string Validate(string path, int maximumLength = 1024)
        {
            if(string.IsNullOrEmpty(path)||path.Length>maximumLength)throw new ArgumentException("Invalid relative file path.");
            path=path.Replace('\\','/');
            foreach(var part in path.Split('/'))
            {
                var stem=part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
                if(part.Length==0||part.Length>240||part=="."||part==".."||part.EndsWith(".",StringComparison.Ordinal)||part.EndsWith(" ",StringComparison.Ordinal)||part.Any(c=>char.IsControl(c)||":*?\"<>|".IndexOf(c)>=0)||new[]{"CON","PRN","AUX","NUL","CONIN$","CONOUT$"}.Contains(stem)||(stem.Length==4&&(stem.StartsWith("COM",StringComparison.Ordinal)||stem.StartsWith("LPT",StringComparison.Ordinal))&&"123456789¹²³".IndexOf(stem[3])>=0))
                    throw new ArgumentException("Use ordinary relative file names, without devices, streams or traversal.");
            }
            return path;
        }
    }
}
