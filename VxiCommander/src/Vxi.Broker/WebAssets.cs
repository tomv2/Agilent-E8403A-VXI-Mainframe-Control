using System.Net;
using System.Text;

static class WebAssets
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "web");

    public static async Task WriteAsync(HttpListenerContext context, string relativePath)
    {
        string safePath = relativePath.Replace('\\', '/').TrimStart('/');
        if (safePath.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid asset path.");

        string fullPath = Path.Combine(Root, safePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            context.Response.StatusCode = 404;
            await WriteTextAsync(context, "text/plain; charset=utf-8", "Not found");
            return;
        }

        byte[] bytes = await File.ReadAllBytesAsync(fullPath);
        context.Response.ContentType = ContentType(fullPath);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public static async Task WriteTextAsync(HttpListenerContext context, string contentType, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        _ => "application/octet-stream"
    };
}
