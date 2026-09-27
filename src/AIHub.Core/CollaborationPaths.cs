namespace AIHub.Core;

internal static class CollaborationPaths
{
    public static void Validate(string workspace, string path)
    {
        // Syntactic relative-path validation runs first in the submission schema.
        // Windows aliases and links must not turn a cited path into another location.
        var parts = path.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.') ||
            System.Text.RegularExpressions.Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new CollaborationValidationException("Use a canonical workspace-relative file path.");
        var cursor = Path.GetFullPath(workspace);
        foreach (var part in parts)
        {
            cursor = Path.Combine(cursor, part);
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new CollaborationValidationException("Linked paths are not accepted as evidence locations.");
        }
    }
}
