using System.Text;

namespace RoadChron;

/// <summary>Appends bounded diagnostic lines and keeps a fixed number of rotated files.</summary>
public static class RoadChronBoundedFileAppender
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static bool AppendLine(string path, string line, long maxFileBytes = 1024 * 1024, int retainedFiles = 3)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(line);
        if (maxFileBytes < 64) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        if (retainedFiles < 1 || retainedFiles > 10) throw new ArgumentOutOfRangeException(nameof(retainedFiles));

        var text = line.EndsWith('\n') ? line : line + Environment.NewLine;
        var bytes = Utf8NoBom.GetBytes(text);
        if (bytes.LongLength > maxFileBytes) return false;

        lock (Gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

                var currentLength = File.Exists(path) ? new FileInfo(path).Length : 0L;
                if (currentLength + bytes.LongLength > maxFileBytes)
                {
                    if (!Rotate(path, retainedFiles)) return false;
                }

                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length + bytes.LongLength > maxFileBytes) return false;
                stream.Write(bytes, 0, bytes.Length);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static bool Rotate(string path, int retainedFiles)
    {
        try
        {
            var oldest = path + "." + retainedFiles;
            if (File.Exists(oldest)) File.Delete(oldest);

            for (var index = retainedFiles - 1; index >= 1; index--)
            {
                var source = path + "." + index;
                var destination = path + "." + (index + 1);
                if (File.Exists(source)) File.Move(source, destination, true);
            }

            if (File.Exists(path)) File.Move(path, path + ".1", true);
            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
