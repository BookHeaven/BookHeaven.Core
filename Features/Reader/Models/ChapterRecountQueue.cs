namespace BookHeaven.Core.Features.Reader.Models;

public class ChapterRecountQueue
{
    private readonly List<int> _chapters = [];

    public int Count => _chapters.Count;

    public bool IsEmpty => _chapters.Count == 0;

    public void Clear() => _chapters.Clear();

    public bool Contains(int chapter) => _chapters.Contains(chapter);
    
    public void Enqueue(IEnumerable<int> chapters, bool front = false)
    {
        var newChapters = chapters.Where(chapter => !_chapters.Contains(chapter)).ToArray();
        if (newChapters.Length == 0) return;
        if (front) _chapters.InsertRange(0, newChapters);
        else _chapters.AddRange(newChapters);
    }
    
    public void Prioritize(int chapter)
    {
        var index = _chapters.IndexOf(chapter);
        if (index <= 0) return;
        _chapters.RemoveAt(index);
        _chapters.Insert(0, chapter);
    }
    
    public int[] ToArray() => [.. _chapters];
    
    public bool Dequeue(int chapter)
    {
        var index = _chapters.IndexOf(chapter);
        if (index < 0) return false;
        _chapters.RemoveAt(index);
        return true;
    }

    public bool TryDequeueNext(out int chapter)
    {
        if (_chapters.Count == 0)
        {
            chapter = -1;
            return false;
        }
        chapter = _chapters[0];
        _chapters.RemoveAt(0);
        return true;
    }

    public int[] DequeueAll()
    {
        var chapters = _chapters.ToArray();
        _chapters.Clear();
        return chapters;
    }
}
