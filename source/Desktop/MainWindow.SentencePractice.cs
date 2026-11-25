using System.Windows;
using static ReadAloudStudio.UiText;

namespace ReadAloudStudio;

public partial class MainWindow
{
    private sealed record SentenceSession(SentenceOrigin Origin,Attempt? Parent,PreparedReference? Passage,ReadingSection Section);
    private SentenceSession? sentenceSession;
    private int? pendingFocus;

    private void ClearPending()
    {
        pendingId=prefs.PendingId=null;
        pendingFocus=prefs.PendingWord=null;
        prefs.PendingReference=null;
        prefs.PendingSentence=null;
    }

    private void RestorePendingForView()
    {
        bool same=prefs.PendingReference==reference && prefs.PendingSentence==sentenceSession?.Origin;
        pendingId=same ? prefs.PendingId : null;
        pendingFocus=same ? prefs.PendingWord : null;
    }

    private async Task<SentenceSession?> RestoreSession(SentenceOrigin? origin)
    {
        if (origin==null) return null;
        var parent=origin.ParentId==null ? null : await engine.Get(origin.ParentId);
        var passage=await engine.Prepare(origin.Passage,origin.Passage);
        var section=parent?.Sections.ElementAtOrDefault(origin.SectionIndex) ?? passage.Sections.ElementAtOrDefault(origin.SectionIndex);
        if (section==null) throw new InvalidOperationException(Tr("DiagInvalidOrigin"));
        return new(origin,parent,passage,section);
    }

    private async Task OpenAttempt(Attempt item)
    {
        sentenceSession=await RestoreSession(item.SentencePractice);
        Editor.Visibility=Visibility.Collapsed;
        reference=item.Reference;
        RestorePendingForView();
        if (sentenceSession is { } session) PrepareSentence(session);
        RenderAttempt(item);
        prefs.Reference=ExampleReference;
        SavePrefs();
    }

    // Reuse the passage dictionary immediately. Word numbers in a sentence take start at zero.
    private void PrepareSentence(SentenceSession session)
    {
        if (session.Passage is not { } passage) return;
        var section=session.Section;
        var words=passage.Words.Skip(section.FirstWord).Take(section.WordCount).Select((w,i)=>new Word {
            Index=i,Text=w.Text,Ipa=w.Ipa,BritishIpa=w.BritishIpa,AmericanIpa=w.AmericanIpa,Sapi=w.Sapi,Guessed=w.Guessed
        }).ToList();
        if (words.Count!=section.WordCount) return;
        SetSources(session.Origin.Passage,passage.Examples);
        ++prepareRevision;
        preparedText=section.Reference; preparedOwner=session.Origin.Passage;
        prepared=new PreparedReference { Words=words, Sections=[new ReadingSection { Reference=section.Reference, WordCount=words.Count }] };
    }

    private async void RetakeClick(object sender,RoutedEventArgs e) => await Safely(async () => {
        if (phase!=Phase.Idle || sentenceSession!=null || CurrentSection is not { } section) return;
        StopAudio(); phase=Phase.Preparing; Controls();
        try
        {
            var origin=new SentenceOrigin(reference,sectionIndex,attempt?.Id);
            var latest=history.FirstOrDefault(entry=>entry.SentencePractice==origin);
            var previous=latest==null ? null : await engine.Get(latest.Id);
            if (closing) return;
            int? localWord=section.Contains(word) ? word-section.FirstWord : null;
            sentenceSession=new(origin,attempt,prepared,section);
            Editor.Visibility=Visibility.Collapsed;
            reference=section.Reference; RestorePendingForView();
            PrepareSentence(sentenceSession);
            RenderAttempt(previous,0,pendingFocus ?? localWord);
            BodyScroll.ScrollToTop();
            Message(() => Tr("RetakeReady",origin.SectionIndex+1));
        }
        finally { phase=Phase.Idle; if (!closing) Controls(); }
    });

    private void ReturnPassageClick(object sender,RoutedEventArgs e)
    {
        if (phase!=Phase.Idle || sentenceSession is not { } session) return;
        StopAudio();
        int? globalWord=word>=0 ? session.Section.FirstWord+word : null;
        sentenceSession=null;
        reference=session.Origin.Passage; RestorePendingForView();
        if (session.Passage!=null)
        {
            ++prepareRevision; preparedText=preparedOwner=reference; prepared=session.Passage;
        }
        RenderAttempt(session.Parent,session.Origin.SectionIndex,globalWord);
        BodyScroll.ScrollToTop();
        Message(() => Tr("ReturnedToPassage",session.Origin.SectionIndex+1));
    }

    private void UpdateSentenceControls(bool idle)
    {
        bool focused=sentenceSession!=null;
        RetakeButton.Visibility=!focused && practiceSections.Count>1 ? Visibility.Visible : Visibility.Collapsed;
        RetakeButton.IsEnabled=idle;
        ReturnPassage.Visibility=RetakePanel.Visibility=focused ? Visibility.Visible : Visibility.Collapsed;
        ChangeSentence.Visibility=focused ? Visibility.Collapsed : Visibility.Visible;
        ReadingHeading.Text=sentenceSession is { } session ? Tr("RetakeNumber",session.Origin.SectionIndex+1) : Tr("ReadingText");
        ReturnPassage.IsEnabled=idle;
        PlayBaseline.IsEnabled=idle && sentenceSession?.Parent!=null && sentenceSession.Section.CanPlay;
        PlayRetakePair.IsEnabled=PlayBaseline.IsEnabled && attempt!=null;
    }

    private void RenderRetake()
    {
        if (sentenceSession is not { } session) return;
        string Detail(Attempt take,ReadingSection? section,Word? focus)
        {
            if (!(section?.Reliable ?? take.Reliable)) return Tr("RetakeUnscored");
            var scores=section?.Scores ?? take.Scores;
            string total=scores.TryGetValue("total",out double value) ? value.ToString("0") : "—";
            var words=section==null ? take.Words : take.Words.Where(w=>section.Contains(w.Index)).ToList();
            string pauses=PauseDescription(take,null,words);
            var lines=new List<string> { Tr("RetakeTotal",total,take.AccentLabel) };
            if (focus!=null)
            {
                string finding=focus.Candidate is { } phone ? Tr("RetakeWordFinding",phone.Expected,phone.Heard) : Describe(take,focus);
                lines.Add(focus.Text+" · "+finding);
            }
            if (pauses.Length>0) lines.Add(pauses);
            return string.Join(Environment.NewLine,lines);
        }
        var parent=session.Parent;
        BaselineDetail.Text=parent==null ? Tr("OriginalNotRecorded") :
            Detail(parent,session.Section,word<0 ? null : parent.Words.ElementAtOrDefault(session.Section.FirstWord+word));
        RetakeDetail.Text=attempt==null ? Tr("SentenceNotRecorded") : Detail(attempt,null,SelectedWord());
    }

    private Task PlayBaselineSentence(SentenceSession session) =>
        Play(StorePaths.Audio(session.Parent!.Id),() => Tr("OriginalTakeLabel"),session.Section.Start,session.Section.End);

    private async void BaselineClick(object sender,RoutedEventArgs e) => await Safely(async () => {
        if (sentenceSession is { Parent:not null,Section.CanPlay:true } session) await PlayBaselineSentence(session);
    });

    private async void RetakePairClick(object sender,RoutedEventArgs e) => await Safely(async () => {
        if (sentenceSession is not { Parent:not null,Section.CanPlay:true } session || attempt is not { } take) return;
        int revision=playbackRevision+1;
        await PlayBaselineSentence(session);
        await Task.Delay(220);
        if (revision==playbackRevision && sentenceSession==session) await Play(StorePaths.Audio(take.Id),() => Tr("SentenceTakeLabel"));
    });
}
