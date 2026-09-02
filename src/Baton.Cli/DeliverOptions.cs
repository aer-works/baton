namespace Baton.Cli;

/// <summary>
/// Parsed options for <see cref="DeliverCommand"/>.
/// </summary>
public sealed record DeliverOptions(string SourceFilePath, string? Title, string RoomDirectoryPath);
