using Swtor.Formats.Dds;

namespace Swtor.App;

/// <summary>
/// Keeps decoded DDS images in memory, so a character rebuild does not decode the same texture again.
/// The images must be treated as read-only: the color functions return new images. When the byte budget is full,
/// the oldest images are dropped first. Safe to call from several threads.
/// </summary>
public sealed class DdsCache(long budgetBytes = 384L * 1024 * 1024)
{
    private readonly Dictionary<string, DdsImage> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();
    private long _bytes;

    /// <summary>Returns the decoded image of a file, reading it on the first call.</summary>
    public DdsImage Get(string path)
    {
        lock (_images)
        {
            if (_images.TryGetValue(path, out var cached)) return cached;
        }

        // The file is read outside the lock, so other threads are not blocked by the decoder.
        var image = DdsReader.Decode(File.ReadAllBytes(path));
        long size = image.Rgba.LongLength;
        lock (_images)
        {
            if (_images.ContainsKey(path)) return _images[path];
            while (_bytes + size > budgetBytes && _order.Count > 0)
            {
                string oldest = _order.Dequeue();
                if (_images.Remove(oldest, out var dropped)) _bytes -= dropped.Rgba.LongLength;
            }
            _images[path] = image;
            _order.Enqueue(path);
            _bytes += size;
        }
        return image;
    }
}
