using System.Collections;
using System.Text;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace ImageGenerationServer.Services;

public class ReplicateAiServiceOptions
{
    public string BaseUrl { get; init; }
    public string? Token { get; init; }
}

public interface IReplicateAiService
{
    Task<List<string>> GenerateImage(string keyword);
}

public class ReplicateAiService : IReplicateAiService
{
    private const string BasePrompt = "clip art";
    private const string BaseNegativePrompt = "english characters, alphabet, realistic";
    private static readonly TimeSpan timeout = TimeSpan.FromMinutes(1);

    private readonly ReplicateAiServiceOptions _options;
    private readonly HttpClient _httpClient;

    public ReplicateAiService(IOptions<ReplicateAiServiceOptions> options, HttpClient httpClient)
    {
        _options = options.Value;
        _httpClient = httpClient;
    }

    public async Task<List<string>> GenerateImage(string keyword)
    {
        try
        {
            var response = await SubmitRequest(keyword);
            var urls = await PollResult(response);
            return urls.Select(url => ToBase64Image(url).Result).ToList();
        }
        catch (Exception e)
        {
            Log.Error(e, "Error when generate image");
            return new List<string>();
        }
    }

    private async ValueTask<string> SubmitRequest(string keyword)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _options.BaseUrl);
        request.Headers.Add("Authorization", $"Token {_options.Token}");
        var payload = JsonConvert.SerializeObject(new
        {
            version = "db21e45d3f7023abc2a46ee38a23973f6dce16bb082a930b0c49861f96d1e5bf",
            input = new
            {
                prompt = $"{keyword}, {BasePrompt}",
                image_dimensions = "512x512",
                negative_prompt = BaseNegativePrompt,
                num_outputs = 4
            }
        });
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Log.Information("response: {Message}", json);
        return JObject.Parse(json).Value<string>("id")!;
    }

    private async ValueTask<string> ToBase64Image(string url)
    {
        using var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        
        var ext = Path.GetExtension(url).Substring(1);
        var base64String = response.Content.ReadAsByteArrayAsync()
            .ContinueWith(t => Convert.ToBase64String(t.Result))
            .Result;

        return $"data:image/{ext};base64,{base64String}";
    }

    private async ValueTask<List<string>> PollResult(string id)
    {
        var startTime = DateTime.Now;
        while (DateTime.Now - startTime < timeout)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/{id}");
            request.Headers.Add("Authorization", $"Token {_options.Token}");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            
            var json = await response.Content.ReadAsStringAsync();
            Log.Information("response: {Json}", json);
            var jsonObject = JObject.Parse(json);
            if ("succeeded".Equals(jsonObject.Value<string>("status")))
            {
                Log.Information("Complete generate images for '{value}'", jsonObject.Value<JObject>("input")!.Value<string>("prompt"));
                return jsonObject.Value<JArray>("output")!.ToObject<List<string>>()!;
            }
            Thread.Sleep(TimeSpan.FromSeconds(1));
        }
        throw new TimeoutException("Timeout when generating images");
    }
}