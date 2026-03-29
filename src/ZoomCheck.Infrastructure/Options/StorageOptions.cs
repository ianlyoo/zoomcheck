namespace ZoomCheck.Infrastructure.Options;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string DatabasePath { get; set; } = "data/zoomcheck.db";
}
