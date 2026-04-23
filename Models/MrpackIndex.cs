using System.Text.Json.Serialization;

namespace McSH.Models;

public class MrpackIndex
{
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; }
    [JsonPropertyName("game")]          public string Game        { get; set; } = string.Empty;
    [JsonPropertyName("versionId")]     public string VersionId   { get; set; } = string.Empty;
    [JsonPropertyName("name")]          public string Name        { get; set; } = string.Empty;
    [JsonPropertyName("summary")]       public string? Summary    { get; set; }
    [JsonPropertyName("files")]         public List<MrpackFile> Files { get; set; } = [];
    [JsonPropertyName("dependencies")]  public Dictionary<string, string> Dependencies { get; set; } = [];
}

public class MrpackFile
{
    [JsonPropertyName("path")]      public string Path      { get; set; } = string.Empty;
    [JsonPropertyName("hashes")]    public Dictionary<string, string> Hashes { get; set; } = [];
    [JsonPropertyName("env")]       public MrpackEnv? Env   { get; set; }
    [JsonPropertyName("downloads")] public List<string> Downloads { get; set; } = [];
    [JsonPropertyName("fileSize")]  public long FileSize    { get; set; }
}

public class MrpackEnv
{
    [JsonPropertyName("client")] public string Client { get; set; } = "required";
    [JsonPropertyName("server")] public string Server { get; set; } = "required";
}
