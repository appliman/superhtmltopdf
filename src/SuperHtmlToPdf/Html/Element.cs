namespace SuperHtmlToPdf;

internal sealed class Element(string tag, string text = "")
{
    public string Tag { get; } = tag;
    public string Text { get; } = text;
    public List<Element> Children { get; } = [];
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Style { get; } = new(StringComparer.Ordinal);
    internal Element? Parent
    {
        get; private set;
    }
    internal Element? PreviousSibling
    {
        get; private set;
    }
    internal Element? LastElementChild
    {
        get; private set;
    }
    internal int ElementIndex
    {
        get; private set;
    }
    internal int ElementCount
    {
        get; private set;
    }

    internal void Append(Element child)
    {
        child.Parent = this;
        if (child.Tag != "#text")
        {
            child.PreviousSibling = LastElementChild;
            LastElementChild = child;
            child.ElementIndex = ++ElementCount;
        }
        Children.Add(child);
    }

    public IEnumerable<Element> DescendantsAndSelf()
    {
        var remaining = new Stack<Element>();
        remaining.Push(this);
        while (remaining.TryPop(out var node))
        {
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                remaining.Push(node.Children[i]);
            }
        }
    }
}
