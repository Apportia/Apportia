namespace Apportia.Models;

public readonly record struct CopyProgress(int Total, int Copied, string File);
