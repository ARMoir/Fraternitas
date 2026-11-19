namespace Fraternitas.Services;

public sealed class DataStorage(string directory)
{
    public string Directory => directory;
    public string DocumentsDirectory => Path.Combine(directory, "documents");
}
