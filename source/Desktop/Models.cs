using System.ComponentModel;
using System.IO;
using System.Text.Json;

using static ReadAloudStudio.UiText;

namespace ReadAloudStudio;

internal sealed class Phone
{
    public string Expected { get; set; } = "";
    public string Heard { get; set; } = "";
    public double Risk { get; set; }
    public double? Start { get; set; }
    public double? End { get; set; }
    public bool HasAlternative => Risk >= .5 && Heard.Length > 0 && Heard != Expected;
}

internal sealed class Word
{
    public int Index { get; set; }
    public string Text { get; set; } = "";
    public string Ipa { get; set; } = "";
    public string BritishIpa { get; set; } = "";
    public string AmericanIpa { get; set; } = "";
    public string Sapi { get; set; } = "";
    public double? Start { get; set; }
    public double? End { get; set; }
    public bool Flagged { get; set; }
    public bool Missing { get; set; }
    public bool Guessed { get; set; }
    public double Risk { get; set; }
    public List<Phone> Phones { get; set; } = [];
    public bool CanPlay => !Missing && End > Start && Start >= 0;
    // The engine only supplies an alternative heard phone when its own sound-error gate passes.
    public Phone? Candidate => Guessed || Missing ? null :
        Phones.Where(p => p.HasAlternative).MaxBy(p => p.Risk);
    public bool NeedsReview => !Guessed && (Flagged || Missing || Candidate != null);
    public double ReviewPriority => Missing ? 1 : Math.Max(Risk, Candidate?.Risk ?? 0);
}

internal sealed class PreparedReference
{
    public List<ExampleTrack> Examples { get; set; } = [];
    public List<ReadingSection> Sections { get; set; } = [];
    public List<Word> Words { get; set; } = [];
}

internal sealed class ExtraSpeech
{
    public double? Start { get; set; }
    public double? End { get; set; }
    public string Heard { get; set; } = "";
}

internal class AttemptSummary : INotifyPropertyChanged
{
    public SentenceOrigin? SentencePractice { get; set; }
    public string Id { get; set; } = "";
    public string Reference { get; set; } = "";
    public string SentenceKey { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public double Duration { get; set; }
    public double? Total { get; set; }
    public bool Reliable { get; set; }
    public int SentenceCount { get; set; }
    public int AssessedCount { get; set; }
    public string ReferenceAccent { get; set; } = "en-US";
    public bool BritishReference => ReferenceAccent == "en-GB";
    public string AccentLabel => BritishReference ? Tr("British") : Tr("LegacyUS");
    public string Label => Created.ToLocalTime().ToString("MM-dd  HH:mm:ss") + " · " + Tr("ShortSeconds",Duration)
        + " · " + AccentLabel + (SentencePractice is { } origin ? " · "+Tr("RetakeNumber",origin.SectionIndex+1) : "")
        + (Reliable ? "" : " · " + Tr("NeedsReview"));
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLabel() => PropertyChanged?.Invoke(this,new(nameof(Label)));
    public override string ToString() => Label;
}

internal sealed class InferenceDevice
{
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";
    public string Processor => Kind switch { "cuda" => "GPU", "mixed" => "GPU / CPU", _ => "CPU" };
}

internal sealed class ReadingSection
{
    public int Index { get; set; }
    public string Reference { get; set; } = "";
    public int FirstWord { get; set; }
    public int WordCount { get; set; }
    public string Status { get; set; } = "";
    public bool Reliable { get; set; }
    public double? Start { get; set; }
    public double? End { get; set; }
    public string Transcript { get; set; } = "";
    public List<string> Reasons { get; set; } = [];
    public List<string> TextDiff { get; set; } = [];
    public List<double> Peaks { get; set; } = [];
    public List<ExtraSpeech> Extra { get; set; } = [];
    public Dictionary<string,double> Scores { get; set; } = [];
    public InferenceDevice? Device { get; set; }
    public bool Contains(int word) => word >= FirstWord && word < FirstWord + WordCount;
    public bool CanPlay => End > Start && Start >= 0;
    private string Excerpt => Reference.Length > 50 ? Reference[..48] + "…" : Reference;
    public string Title => Tr("SentenceNumber",Index+1) + " · " + Excerpt;
    public string Label => Reliable ? Title : Tr("SentenceNumber",Index+1) + " · " +
        Tr(Status=="missing" ? "PossibleSkip" : "NeedsReview") + " · " + Excerpt;
    public override string ToString() => Label;
}

internal sealed class EngineHealth
{
    public InferenceDevice? Device { get; set; }
}

internal sealed record SentenceOrigin(string Passage, int SectionIndex, string? ParentId);

internal sealed class ExampleTrack
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double Duration { get; set; }
    public List<ReadingSection> Sections { get; set; } = [];
    public List<Word> Words { get; set; } = [];

    public (double Start,double End,string Text)? Excerpt(int index,int sectionIndex,bool neighbours)
    {
        var section = Sections.ElementAtOrDefault(sectionIndex);
        var target = Words.ElementAtOrDefault(index);
        if (section?.CanPlay != true || !section.Contains(index) || target?.CanPlay != true) return null;
        int first=index, last=index;
        if (neighbours && section.Contains(index-1) && Words[index-1].CanPlay) first--;
        if (neighbours && section.Contains(index+1) && Words.ElementAtOrDefault(index+1)?.CanPlay==true) last++;
        return (Words[first].Start!.Value, Words[last].End!.Value,
            string.Join(" ",Words.GetRange(first,last-first+1).Select(w=>w.Text)));
    }
}

internal sealed class DemoAudio
{
    public byte[] Pcm { get; set; } = [];
    public int SampleRate { get; set; }
}

internal sealed class Attempt : AttemptSummary
{
    public string Transcript { get; set; } = "";
    public string Engine { get; set; } = "";
    public InferenceDevice? Device { get; set; }
    public double Elapsed { get; set; }
    public List<string> Reasons { get; set; } = [];
    public List<string> TextDiff { get; set; } = [];
    public List<double> Peaks { get; set; } = [];
    public List<Word> Words { get; set; } = [];
    public List<ReadingSection> Sections { get; set; } = [];
    public ReadingSection? SectionFor(int index) => Sections.FirstOrDefault(s => s.Contains(index));
    public List<ExtraSpeech> Extra { get; set; } = [];
    public Dictionary<string,double> Scores { get; set; } = [];
    public string Score(string key) => Reliable && Scores.TryGetValue(key, out var score) ? score.ToString("0") : "—";
}

internal static class StorePaths
{
    public static readonly string Root = Environment.GetEnvironmentVariable("READALOUD_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReadAloudStudio");
    public static readonly string Base = AppContext.BaseDirectory;
    public static string Draft(string id) => Path.Combine(Root, "drafts", id + ".wav");
    public static string Audio(string id) => Path.Combine(Root, "recordings", id + ".wav");
    public static string ExampleDraft(string id) => Path.Combine(Root,"example-drafts",id+".wav");
    public static string ExampleAudio(string id) => Path.Combine(Root,"examples",id+".wav");
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

internal sealed class Preferences
{
    public string UiLanguage { get; set; } = UiText.English;
    public string MicrophoneId { get; set; } = "";
    public string DemoVoiceId { get; set; } = DemoVoice.Default.Id;
    public string Reference { get; set; } = "I think it’s worth it.";
    public string? PendingId { get; set; }
    public string? PendingReference { get; set; }
    public SentenceOrigin? PendingSentence { get; set; }
    public int? PendingWord { get; set; }
    public Dictionary<string,string> ExampleSources { get; set; } = [];
    public string? ImportDirectory { get; set; }
    private static string FilePath => Path.Combine(StorePaths.Root, "preferences.json");
    public static Preferences Read()
    {
        try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath), StorePaths.Json) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        var tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, StorePaths.Json));
        File.Move(tmp, FilePath, true);
    }
}
