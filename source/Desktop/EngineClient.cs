using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using static ReadAloudStudio.UiText;

namespace ReadAloudStudio;

internal sealed class EngineClient : IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(30) };
    private readonly SemaphoreSlim launch = new(1);
    private Process? worker;
    private Uri? endpoint;
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private bool closed;
    public EngineClient() { http.DefaultRequestHeaders.Add("X-Session", token); }

    public async Task StartAsync()
    {
        await launch.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (worker is { HasExited: false } && endpoint != null) return;
            worker?.Dispose();
            endpoint = null;
            string python = Path.Combine(StorePaths.Base, "runtime", "python", "python.exe");
            string service = Path.Combine(StorePaths.Base, "engine", "service.py");
            if (!File.Exists(python) || !File.Exists(service))
                throw new FileNotFoundException(Tr("DiagEngineFiles"));
            var info = new ProcessStartInfo(python)
            {
                WorkingDirectory = StorePaths.Base, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string arg in new[] { "-I", "-B", "-X", "utf8", "-u", service, "--data", StorePaths.Root })
                info.ArgumentList.Add(arg);
            info.Environment["READALOUD_SESSION_TOKEN"] = token;
            worker = Process.Start(info) ?? throw new IOException(Tr("DiagEngineStart"));
            worker.ErrorDataReceived += (_, a) => {
                if (!string.IsNullOrWhiteSpace(a.Data))
                    try { File.AppendAllText(Path.Combine(StorePaths.Root, "runtime.log"), a.Data + Environment.NewLine); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
            };
            worker.BeginErrorReadLine();
            string? line = await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (line == null) throw new IOException(Tr("DiagEngineLog"));
            int port = JsonDocument.Parse(line).RootElement.GetProperty("port").GetInt32();
            endpoint = new Uri($"http://127.0.0.1:{port}/");
            _ = worker.StandardOutput.ReadToEndAsync();
        }
        catch
        {
            if (worker is { HasExited: false }) worker.Kill(true);
            throw;
        }
        finally { launch.Release(); }
    }

    private async Task<T> Request<T>(string path, HttpContent? body = null)
    {
        await StartAsync();
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, new Uri(endpoint!, path)) { Content = body };
        using var response = await http.SendAsync(request);
        string json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            using var error = JsonDocument.Parse(json);
            throw new InvalidOperationException(error.RootElement.GetProperty("error").GetString());
        }
        return JsonSerializer.Deserialize<T>(json, StorePaths.Json) ?? throw new IOException(Tr("DiagEmptyResult"));
    }
    public Task<EngineHealth> Health() => Request<EngineHealth>("health");
    public Task<List<AttemptSummary>> History() => Request<List<AttemptSummary>>("history");
    public Task<Attempt> Get(string id) => Request<Attempt>("attempt/" + id);
    public Task<PreparedReference> Prepare(string reference,string exampleReference) =>
        Post<PreparedReference>("prepare",new {reference,exampleReference});
    public Task<DemoAudio> Demo(string voice, string reference) =>
        Request<DemoAudio>("demo", new StringContent(JsonSerializer.Serialize(new { voice, reference }), Encoding.UTF8, "application/json"));
    public Task<Attempt> Assess(string id, string reference, SentenceOrigin? sentencePractice=null) =>
        Post<Attempt>("assess",new {id,reference,sentencePractice});
    public Task<List<ExampleTrack>> Examples(string reference) => Post<List<ExampleTrack>>("examples",new {reference});
    public Task<ExampleTrack> ImportExample(string id,string reference,string name) =>
        Post<ExampleTrack>("examples/import",new {id,reference,name});
    public Task<ExampleTrack> ExampleTiming(string reference,DemoAudio audio) =>
        Post<ExampleTrack>("examples/timing",new {reference,audio.SampleRate,audio.Pcm});
    private Task<T> Post<T>(string path,object data) =>
        Request<T>(path,new StringContent(JsonSerializer.Serialize(data,StorePaths.Json),Encoding.UTF8,"application/json"));
    public Task<Attempt> Rescore(string id) =>
        Request<Attempt>("rescore", new StringContent(JsonSerializer.Serialize(new { id }), Encoding.UTF8, "application/json"));
    public void Dispose()
    {
        closed = true;
        http.Dispose();
        if (worker != null)
        {
            try { if (!worker.HasExited) { worker.StandardInput.Close(); if (!worker.WaitForExit(800)) worker.Kill(true); } }
            catch (InvalidOperationException) { }
            worker.Dispose();
        }
    }
}
