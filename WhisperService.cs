using System.Text.Json;

public class WhisperService
{

    #region Private members
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    #endregion

    #region Constructor
    public WhisperService(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }
    #endregion

    #region Transcribe methods
    public async Task<string> TranscribeAsync(HttpRequest request)
    {
        var transcriptId = "unknown";
        var apiKey = _config["Whisper:ApiKey"] ?? "";
        var endpoint = _config["Whisper:Endpoint"] ?? "https://api.openai.com/v1/";
        var model = _config["Whisper:Model"] ?? "whisper-1";

        try
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                Console.WriteLine("❌ OpenAI API key not found");
                throw new InvalidOperationException("OpenAI API key not configured");
            }

            if (!request.HasFormContentType)
            {
                Console.WriteLine("❌ Invalid content type");
                throw new ArgumentException("Invalid content type");
            }

            var form = await request.ReadFormAsync();
            var audioFile = form.Files["audio"];
            transcriptId = form["transcriptId"].FirstOrDefault() ?? "unknown";

            Console.WriteLine($"📥 [{transcriptId}] Received voice segment");

            if (audioFile == null || audioFile.Length == 0)
            {
                Console.WriteLine($"❌ [{transcriptId}] No audio file provided");
                throw new ArgumentException("No audio file provided");
            }

            var fileSizeKB = Math.Round(audioFile.Length / 1024.0);
            Console.WriteLine($"🎵 [{transcriptId}] Processing voice segment: {fileSizeKB}KB");

            // Skip very small files (likely just noise)
            if (audioFile.Length < 3000)
            {
                Console.WriteLine($"⏭️  [{transcriptId}] Skipping tiny segment ({fileSizeKB}KB)");
                return "";
            }

            var startTime = DateTime.UtcNow;

            try
            {
                Console.WriteLine($"🔄 [{transcriptId}] Sending to Whisper API...");

                //var httpClient = _httpClientFactory.CreateClient("OpenAI"); // Named client configured previously in Program.cs, now not needed
                var httpClient = _httpClientFactory.CreateClient();
                httpClient.BaseAddress = new Uri(endpoint);
                httpClient.DefaultRequestHeaders.Add("User-Agent", "WhisperServer/1.0");
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                // Create multipart form data
                using var content = new MultipartFormDataContent();

                // Add audio file
                using var fileStream = new MemoryStream();
                await audioFile.CopyToAsync(fileStream);
                fileStream.Position = 0;

                var fileContent = new StreamContent(fileStream);
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/webm");
                content.Add(fileContent, "file", audioFile.FileName ?? "voice-segment.webm");

                // Add other parameters
                content.Add(new StringContent(model), "model");
                content.Add(new StringContent("en"), "language");
                content.Add(new StringContent("json"), "response_format");
                content.Add(new StringContent("0.0"), "temperature");

                // Make the API call
                var response = await httpClient.PostAsync("audio/transcriptions", content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    throw new Exception($"OpenAI API error: {response.StatusCode} - {errorContent}");
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                var transcriptionResult = JsonSerializer.Deserialize<JsonElement>(responseContent);

                var transcribedText = transcriptionResult.TryGetProperty("text", out var textProperty)
                    ? textProperty.GetString()?.Trim() ?? ""
                    : "";

                // Filter out common artifacts/watermarks
                transcribedText = CleanTranscribedText(transcribedText);

                var processingTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

                // Enhanced logging for voice segments
                if (!string.IsNullOrEmpty(transcribedText))
                {
                    Console.WriteLine($"✅ [{transcriptId}] SUCCESS ({processingTime}ms): \"{transcribedText}\"");
                }
                else
                {
                    Console.WriteLine($"⚠️  [{transcriptId}] Empty result ({processingTime}ms) - likely silence or noise");
                }

                return transcribedText;
            }
            catch (Exception ex)
            {
                var processingTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                Console.WriteLine($"❌ [{transcriptId}] ERROR ({processingTime}ms): {ex.Message}");

                throw;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ [{transcriptId}] Unexpected error: {ex.Message}");
            throw;
        }
    }

    private static string CleanTranscribedText(string transcribedText)
    {
        // Filter out common artifacts/watermarks
        var artifactsToRemove = new[]
        {
            "Transcribed by https://otter.ai",
            "transcribed by https://otter.ai",
            "Transcribed by Otter.ai",
            "transcribed by otter.ai",
            "otter.ai",
            "Otter.ai"
        };

        foreach (var artifact in artifactsToRemove)
        {
            transcribedText = transcribedText.Replace(artifact, "", StringComparison.OrdinalIgnoreCase).Trim();
        }

        // Clean up extra whitespace
        transcribedText = System.Text.RegularExpressions.Regex.Replace(transcribedText, @"\s+", " ").Trim();

        return transcribedText;
    }
    #endregion
}