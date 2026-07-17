namespace SevenRecord.Domain.Projects;

public sealed record ProjectDocument
{
    private ProjectDocument(Guid id, string name, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public string Name { get; }

    public DateTimeOffset CreatedAt { get; }

    public static ProjectDocument Create(string name, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new ProjectDocument(Guid.NewGuid(), name.Trim(), createdAt);
    }
}
