using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NAudio.Wave;
using static ReadAloudStudio.UiText;

namespace ReadAloudStudio;

public partial class MainWindow
{
    private readonly List<DemoVoice> builtinVoices;
    private List<ExampleTrack> importedTracks=[];
    private string? sourcesOwner;
    private int sourceRevision;
    private bool updatingVoices;
    private string ExampleReference => sentenceSession?.Origin.Passage ?? reference;
    private int ExampleSectionIndex => sentenceSession?.Origin.SectionIndex ?? sectionIndex;
    private int WordInSentence => word-(CurrentSection?.FirstWord ?? 0);
    private string ExampleSentence => CurrentSection?.Reference ?? reference;

    private static string SourceKey(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n").Trim())));

    private void SetSources(string owner,List<ExampleTrack> tracks,string? selected=null)
    {
        sourcesOwner=owner; importedTracks=tracks;
        var voices=builtinVoices.Concat(tracks.Select(t=>new DemoVoice("file:"+t.Id,t.Name,false,track:t))).ToList();
        selected??=prefs.ExampleSources.GetValueOrDefault(SourceKey(owner)) ?? prefs.DemoVoiceId;
        int index=voices.FindIndex(v=>v.Id==selected);
        if (index<0) index=builtinVoices.FindIndex(v=>v.Id==prefs.DemoVoiceId);
        updatingVoices=true;
        try
        {
            VoiceBox.ItemsSource=voices;
            VoiceBox.SelectedIndex=Math.Max(0,index);
        }
        finally { updatingVoices=false; }
    }

    private void RememberSource(DemoVoice selected)
    {
        if (sourcesOwner==null) return;
        if (selected.Track==null) prefs.DemoVoiceId=selected.Id;
        if (importedTracks.Count>0) prefs.ExampleSources[SourceKey(sourcesOwner)]=selected.Id;
    }

    private (double Start,double End,string Text)? ImportedExcerpt(ExampleTrack track,bool neighbours)
    {
        var section=track.Sections.ElementAtOrDefault(ExampleSectionIndex);
        int local=WordInSentence;
        if (section==null || local<0 || local>=section.WordCount) return null;
        return track.Excerpt(section.FirstWord+local,ExampleSectionIndex,neighbours);
    }

    private void UpdateExampleControls(bool idle)
    {
        var track=SelectedVoice.Track;
        bool canFindWord=SelectedWord()!=null && (track==null || ImportedExcerpt(track,false)!=null);
        bool canFindSentence=track==null || track.Sections.ElementAtOrDefault(ExampleSectionIndex)?.CanPlay==true;
        PlayWordExample.IsEnabled=idle && canFindWord && (track!=null || SelectedVoice.British || prepared!=null || preparedText==null);
        PlayContext.IsEnabled=idle && canFindWord;
        PlayContext.ToolTip=Tr(track!=null && !canFindWord ? "UnlocatedWord" : "ContextHelp");
        ToolTipService.SetShowOnDisabled(PlayContext,true);
        PlayWordExample.ToolTip=Tr(track!=null ? (canFindWord ? "ImportedWordHelp" : "UnlocatedWord") : "WordExampleHelp");
        ToolTipService.SetShowOnDisabled(PlayWordExample,true);
        PlayExample.IsEnabled=idle && (sentenceSession==null || canFindSentence);
        PlayModelSentence.Visibility=practiceSections.Count>1 ? Visibility.Visible : Visibility.Collapsed;
        PlayModelSentence.IsEnabled=idle && canFindSentence;
        PlayModelSentence.ToolTip=canFindSentence ? null : Tr("UnlocatedSentence");
        ToolTipService.SetShowOnDisabled(PlayModelSentence,true);
        ImportModel.Visibility=sentenceSession==null ? Visibility.Visible : Visibility.Collapsed;
        ImportModel.IsEnabled=idle;
        SourceCoverage.Text=track==null ? "" : Tr("AlignedCoverage",track.Words.Count(w=>w.CanPlay),track.Words.Count);
        SourceCoverage.Visibility=track!=null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ImportModelClick(object sender,RoutedEventArgs e) => await Safely(async () => {
        if (phase!=Phase.Idle || sentenceSession!=null) return;
        var pick=new OpenFileDialog {Title=Tr("ImportModelTitle"),Filter=Tr("WavFiles")+"|*.wav",CheckFileExists=true};
        if (pick.ShowDialog(this)!=true) return;
        await ImportModelFile(pick.FileName);
    });

    private async Task ImportModelFile(string file)
    {
        string owner=ExampleReference, id=Guid.NewGuid().ToString(), draft=StorePaths.ExampleDraft(id);
        StopAudio(); phase=Phase.AligningExample; Controls(); Message(() => Tr("AligningExample"));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(draft)!);
            await Task.Run(()=>StudioAudio.Import(file,draft));
            var track=await engine.ImportExample(id,owner,Path.GetFileName(file));
            if (closing) return;
            var tracks=await engine.Examples(owner);
            ++sourceRevision;
            if (prepared!=null) prepared.Examples=tracks;
            SetSources(owner,tracks,"file:"+track.Id);
            RememberSource(SelectedVoice); SavePrefs(); RenderFocus();
            Message(() => Tr("ImportedExample",track.Name,track.Words.Count(w=>w.CanPlay),track.Words.Count));
        }
        finally
        {
            phase=Phase.Idle; if (!closing) Controls();
            if (File.Exists(draft)) File.Delete(draft);
        }
    }

    private sealed class SentenceAudio(Task<DemoAudio> audio)
    {
        public Task<DemoAudio> Audio { get; }=audio;
        public Task<ExampleTrack>? Timing { get; set; }
    }
    private readonly Dictionary<(string Voice,string Text),SentenceAudio> sampleCache=[];
    private readonly Queue<(string Voice,string Text)> sampleOrder=[];

    private SentenceAudio SentenceSample(DemoVoice voice,string phrase)
    {
        var key=(voice.Id,phrase);
        if (sampleCache.TryGetValue(key,out var entry) && !entry.Audio.IsFaulted) return entry;
        if (entry==null)
        {
            if (sampleOrder.Count>=12) sampleCache.Remove(sampleOrder.Dequeue());
            sampleOrder.Enqueue(key);
        }
        var audio=voice.British ? engine.Demo(voice.Id,phrase) : Task.Run(()=>WordVoice.SynthesizeAmerican(voice,phrase,""));
        return sampleCache[key]=new(audio);
    }

    private Task PlayTrack(ExampleTrack track,Func<string> label,double? start=null,double? end=null) =>
        PlaySound(label,()=>player.Play(StorePaths.ExampleAudio(track.Id),start,end,false));

    private async Task PlayModelSection()
    {
        if (SelectedVoice.Track is { } track)
        {
            if (track.Sections.ElementAtOrDefault(ExampleSectionIndex) is {CanPlay:true} section)
                await PlayTrack(track,() => track.Name+" · "+Tr("SentenceNumber",section.Index+1),section.Start,section.End);
        }
        else await PlayDemo([ExampleSentence],() => Tr("FullSentenceExample"));
    }

    private async void ModelSentenceClick(object sender,RoutedEventArgs e) => await Safely(PlayModelSection);

    private async void ContextClick(object sender,RoutedEventArgs e) => await Safely(async () => {
        if (phase!=Phase.Idle || SelectedWord() is not { } target) return;
        var voice=SelectedVoice;
        if (voice.Track is { } track)
        {
            if (ImportedExcerpt(track,true) is { } clip) await PlayTrack(track,() => Tr("ContextExcerpt",clip.Text),clip.Start,clip.End);
            return;
        }
        string sentence=ExampleSentence, text=target.Text;
        int local=WordInSentence;
        StopAudio();
        int revision=playbackRevision+1;
        bool Current() => !closing && phase==Phase.Idle && playbackRevision==revision;
        await PlaySound(() => Tr("ContextExcerpt",text),async () => {
            try
            {
                var sample=SentenceSample(voice,sentence);
                var audio=await sample.Audio;
                if (!Current()) return;
                if (sample.Timing==null || sample.Timing.IsFaulted) sample.Timing=engine.ExampleTiming(sentence,audio);
                var timing=await sample.Timing;
                if (!Current()) return;
                if (timing.Excerpt(local,0,true) is not { } part) throw new InvalidOperationException(Tr("UnlocatedWord"));
                text=part.Text;
                Message(() => Tr("Playing",Tr("ContextExcerpt",text)));
                int Frame(double t) => 2*Math.Clamp((int)Math.Round(t*audio.SampleRate),0,audio.Pcm.Length/2);
                int start=Frame(part.Start),end=Frame(part.End);
                if (end<=start) throw new InvalidOperationException(Tr("UnlocatedWord"));
                await player.PlayPcm(audio.Pcm[start..end],new WaveFormat(audio.SampleRate,16,1));
            }
            catch (Exception) when (!Current()) { }
        });
    });
}
