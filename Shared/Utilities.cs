namespace BookHeaven.Core.Shared;

public static class Utilities
{
    public static async Task StoreFile(string? source, string destination, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }
            
        byte[] fileBytes;
        if (source.StartsWith("http"))
        {
            using var httpClient = new HttpClient();
            fileBytes = await httpClient.GetByteArrayAsync(source, cancellationToken);
        }
        else
        {
            fileBytes = await File.ReadAllBytesAsync(source, cancellationToken);
        }
        
        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (fileBytes.Length > 0)
        {
            await File.WriteAllBytesAsync(destination, fileBytes, cancellationToken);
        }
    }
}