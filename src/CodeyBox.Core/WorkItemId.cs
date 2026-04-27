namespace CodeyBox.Core;

public readonly record struct WorkItemId(Guid Value)
{
    public static WorkItemId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
    public static WorkItemId Parse(string s) => new(Guid.Parse(s));
}
