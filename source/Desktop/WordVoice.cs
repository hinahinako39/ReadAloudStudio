using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;

using static ReadAloudStudio.UiText;

namespace ReadAloudStudio;

internal sealed class DemoVoice(string id,string name,bool british,string gender="",ExampleTrack? track=null) : INotifyPropertyChanged
{
    public string Id { get; }=id;
    public string Name { get; }=name;
    public bool British { get; }=british;
    public string Gender { get; }=gender;
    public ExampleTrack? Track { get; }=track;
    public static readonly DemoVoice Default = new("gb-cori", "Cori", true);
    public string Label => Track != null ? Tr("ImportedVoice",Name) : Id switch {
        "gb-cori" => Tr("VoiceCori"), "gb-northern" => Tr("VoiceNorthern"),
        _ => Tr(Gender switch { "female"=>"VoiceUSFemale", "male"=>"VoiceUSMale", _=>"VoiceUS" }, Name)
    };
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLabel() => PropertyChanged?.Invoke(this,new(nameof(Label)));
    public override string ToString() => Label;
}

internal static class WordVoice
{
    // A synthesis call may run on a different thread from the voice picker.
    private sealed class SapiObjects : IDisposable
    {
        private readonly Stack<object> owned = new();
        public dynamic Keep(object obj) { owned.Push(obj); return obj; }
        public dynamic Create(string name) => Keep(Activator.CreateInstance(Type.GetTypeFromProgID(name, true)!)!);
        public void Dispose() { while (owned.TryPop(out var obj)) Marshal.ReleaseComObject(obj); }
    }

    public static List<DemoVoice> Choices()
    {
        List<DemoVoice> choices = [DemoVoice.Default, new("gb-northern", "Northern England", true)];
        var american = new List<DemoVoice>();
        using var com = new SapiObjects();
        try
        {
            dynamic speaker = com.Create("SAPI.SpVoice");
            dynamic tokens = com.Keep(speaker.GetVoices("Language=409", "Name=Microsoft Zira Desktop"));
            for (int i=0; i<tokens.Count; i++)
            {
                dynamic token = com.Keep(tokens.Item(i));
                string name = ((string)token.GetDescription()).Split(" - ")[0]
                    .Replace("Microsoft ", "").Replace(" Desktop", "").Trim();
                string gender = "";
                try { gender = ((string)token.GetAttribute("Gender")).ToLowerInvariant(); }
                catch (COMException) { }
                american.Add(new((string)token.Id, name, false, gender));
            }
        }
        catch (COMException) { } // The two bundled voices are available without SAPI.
        choices.AddRange(american);
        return choices;
    }

    public static DemoAudio SynthesizeAmerican(DemoVoice selected, string text, string phones)
    {
        using var com = new SapiObjects();
        try
        {
            dynamic voice = com.Create("SAPI.SpVoice");
            dynamic tokens = com.Keep(voice.GetVoices("Language=409", ""));
            for (int i=0; i<tokens.Count; i++)
            {
                dynamic token = com.Keep(tokens.Item(i));
                if (!string.Equals(selected.Id, (string)token.Id, StringComparison.OrdinalIgnoreCase)) continue;
                dynamic format = com.Create("SAPI.SpAudioFormat");
                format.Type = 18; // SAFT16kHz16BitMono.
                dynamic stream = com.Create("SAPI.SpMemoryStream");
                stream.Format = format;
                voice.Voice = token;
                voice.AllowAudioOutputFormatChangesOnNextSet = false;
                voice.AudioOutputStream = stream;
                string speech = phones.Length == 0 ? text :
                    $"<PRON SYM=\"{SecurityElement.Escape(phones)}\">{SecurityElement.Escape(text)}</PRON>";
                voice.Speak(speech, phones.Length == 0 ? 16 : 8 | 128);
                byte[] pcm = (byte[])stream.GetData();
                if (pcm.Length > 0) return new DemoAudio { Pcm=pcm, SampleRate=16000 };
                throw new InvalidOperationException(Tr("DiagVoiceSilent"));
            }
            throw new InvalidOperationException(Tr("DiagVoiceGone"));
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException(Tr("WindowsVoiceError",ex.ErrorCode));
        }
    }
}
