using Microsoft.AspNetCore.Http.Features;

#region builder
var builder = WebApplication.CreateBuilder(args);
// Load additional config file if specified - this is specific to the chat client and mcp configuration
var mcpChatConfig = builder.Configuration["ChatClient:ConfigFile"];
if (!string.IsNullOrEmpty(mcpChatConfig))
    builder.Configuration.AddJsonFile(mcpChatConfig, optional: true, reloadOnChange: true);
Console.WriteLine("🔧 ======= Version 1.0.1 Configuration settings: =======");
foreach (var c in builder.Configuration.AsEnumerable()) Console.WriteLine(c.Key + " = " + c.Value);

builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 26214400); // 25MB
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddHttpClient();
builder.Services.AddSingleton<WhisperService>();
builder.Services.AddSingleton<MCPChat>();
#endregion

#region app creation
var app = builder.Build();
// Configure middleware
app.UseCors();
// Configure static file serving
app.UseDefaultFiles(); // This serves index.html by default
app.UseStaticFiles(); // Serve static files from wwwroot
// Create directories if they don't exist
var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "uploads");

if (!Directory.Exists(uploadsDir))
{
    Directory.CreateDirectory(uploadsDir);
    Console.WriteLine("📁 Created uploads directory");
}
#endregion

#region app endpoints
// Health check endpoint
app.MapGet("/health", () => 
{
    return Results.Json(new 
    { 
        status = "ok", 
        timestamp = DateTime.UtcNow.ToString("O") 
    });
});

// reinitialize chat client
app.MapPost("/newsession", (MCPChat mcpChat) =>
{
    mcpChat.InitializeSession();
    return Results.Json(new { success = true, message = "Chat client reinitialized" });
});

// reinitialize mcp clients
app.MapPost("/reinit", async (MCPChat mcpChat) =>
{
    await mcpChat.ReconnectMCPClients();
    return Results.Json(new { success = true, message = "MCP clients reinitialized" });
});

// process text input
app.MapPost("/process", async (HttpRequest request, MCPChat mcpChat) =>
{
    using var reader = new StreamReader(request.Body);
    var message = await reader.ReadToEndAsync();
    var responseFromMCP = await mcpChat.Send(message);
    return Results.Json(new
    {
        success = true,
        requestText = message,
        text = responseFromMCP,
        transcriptId = "0",
        processingTime = Math.Round(0.0),
        timestamp = DateTime.UtcNow.ToString("O")
    });
});

// Main transcription endpoint
app.MapPost("/transcribe", async (HttpRequest request, WhisperService whisperService, MCPChat mcpChat) =>
{
    var transcriptId = "unknown";
    
    try
    {
        var transcribedText = await whisperService.TranscribeAsync(request);
        
        // Extract transcriptId from form if available for logging
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            transcriptId = form["transcriptId"].FirstOrDefault() ?? "unknown";
        }
        
        var responseFromMCP = "";
        if (!string.IsNullOrEmpty(transcribedText))
        {
            responseFromMCP = await mcpChat.Send(transcribedText);
        }

        return Results.Json(new
        {
            success = true,
            requestText = transcribedText,
            text = responseFromMCP,
            transcriptId = transcriptId,
            processingTime = 0,
            timestamp = DateTime.UtcNow.ToString("O")
        });
    }
    catch (ArgumentException ex)
    {
        Console.WriteLine($"❌ [{transcriptId}] Validation error: {ex.Message}");
        return Results.BadRequest(new { success = false, error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        Console.WriteLine($"❌ [{transcriptId}] Configuration error: {ex.Message}");
        return Results.Problem("Server configuration error", statusCode: 500);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ [{transcriptId}] Unexpected error: {ex.Message}");
        
        // Handle specific OpenAI API errors
        var errorMessage = ex.Message.ToLower();
        
        if (errorMessage.Contains("413") || errorMessage.Contains("too large"))
        {
            return Results.Problem("Audio file too large (max 25MB)", statusCode: 413);
        }
        else if (errorMessage.Contains("401") || errorMessage.Contains("unauthorized"))
        {
            return Results.Problem("Invalid OpenAI API key", statusCode: 500);
        }
        else if (errorMessage.Contains("429") || errorMessage.Contains("rate limit"))
        {
            return Results.Problem("Rate limit exceeded - speaking too fast?", statusCode: 429);
        }
        else
        {
            return Results.Problem($"Transcription failed: {ex.Message}", statusCode: 500);
        }
    }
});
#endregion

#region run app
// Start server
var port = builder.Configuration["Port"] ?? "3000";
var urls = $"http://0.0.0.0:{port}";

Console.WriteLine("🚀 mcp-agent started");
Console.WriteLine($"📡 Server running on {urls}");

app.Run(urls);
#endregion
