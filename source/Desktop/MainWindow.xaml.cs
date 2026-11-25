using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

using static ReadAloudStudio.UiText;

namespace ReadAloudStudio;

public partial class MainWindow : Window
{
    private enum Phase { Preparing, Idle, OpeningMic, Recording, Assessing, AligningExample }
    private static readonly Regex Tokens = new(@"[A-Za-z0-9]+(?:['’‘ʼ＇][A-Za-z0-9]+)*", RegexOptions.Compiled);
    private static readonly (string Text, string File, string TitleKey)[] Examples = [
        ("I think it’s worth it.", "worth-it.wav", "SampleWorth"),
        ("Please leave a message after the tone.", "message.wav", "SampleMessage"),
        ("She bought three tickets for the show.", "tickets.wav", "SampleTickets"),
        ("We can meet at the station.", "station.wav", "SampleStation"),
        ("Would you like a cup of tea?", "tea.wav", "SampleTea"),
        ("The rain stopped just before lunch. I put on my jacket and walked to the park. By the time I got there, the sun was shining.", "after-the-rain.wav", "ParkExample")
    ];
    private readonly EngineClient engine = new();
    private readonly AudioPlayer player;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly Stopwatch activity = new();
    private readonly Preferences prefs;
    private List<AttemptSummary> history = [];
    private int historyVisibleCount;
    private InferenceDevice? currentDevice;
    private Func<string>? statusMessage;
    private string reference;
    private Attempt? attempt, left, right;
    private int word = -1, compareWord = -1, pageRevision, compareRevision, playbackRevision;
    private string page = "practice";
    private Phase phase = Phase.Preparing;
    private string? pendingId;
    private CaptureSession? capture;
    private bool selectingMic, settingComparison, closing;
    private List<ReadingSection> practiceSections = [], comparisonSections = [];
    private readonly List<Span> previewSentences = [];
    private readonly Dictionary<int, (Hyperlink Link, Word Word)> markedPreviewWords = [];
    private int[] reviewWords = [];
    private int previewFocus = -1;
    private PreparedReference? prepared;
    private string? preparedText, preparedOwner;
    private int sectionIndex, comparisonIndex, prepareRevision;
    private const int EntireRecording = -1, CurrentSentence = -2;
    private const double CollapsedPreviewHeight = 140;
    private bool previewExpanded;

    public MainWindow() : this(new AudioPlayer()) { }
    internal MainWindow(AudioPlayer player)
    {
        this.player=player;
        Directory.CreateDirectory(StorePaths.Root);
        Directory.CreateDirectory(System.IO.Path.Combine(StorePaths.Root, "drafts"));
        prefs = Preferences.Read();
        UiText.SetLanguage(prefs.UiLanguage);
        InitializeComponent();
        Language = XmlLanguage.GetLanguage(UiText.LanguageCode);
        PreviewScroll.MaxHeight = CollapsedPreviewHeight;
        reference = string.IsNullOrWhiteSpace(prefs.Reference) ? Examples[0].Text : prefs.Reference;
        builtinVoices = WordVoice.Choices();
        SetSources(reference,[]);
        VoiceBox.SelectionChanged += VoiceChanged;
        foreach (var phrase in Examples.OrderByDescending(p=>p.File=="after-the-rain.wav"))
        {
            var button = new Button { ToolTip=phrase.Text,
                Padding=new Thickness(9,4,9,4), Margin=new Thickness(0,0,6,4) };
            button.SetResourceReference(ContentControl.ContentProperty,"Ui." + phrase.TitleKey);
            button.Click += (_,_) => { SentenceBox.Text=phrase.Text; SentenceBox.CaretIndex=phrase.Text.Length; };
            ExampleChoices.Children.Add(button);
        }
        PracticeNavigator.Step += delta => SelectSection(sectionIndex + delta);
        CompareNavigator.Step += MoveCompareSection;
        timer.Tick += TimerTick;
    }
    private Brush Color(string key) => (Brush)FindResource(key);
    private static string SentenceKey(string phrase) =>
        string.Join(" ", Tokens.Matches(phrase).Select(x => x.Value.Replace('’','\'').Replace('‘','\'').Replace('ʼ','\'').Replace('＇','\'').ToLowerInvariant()));
    private DemoVoice SelectedVoice => VoiceBox.SelectedItem as DemoVoice ?? DemoVoice.Default;
    private void VoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingVoices || VoiceBox.SelectedItem is not DemoVoice chosen) return;
        StopAudio();
        RememberSource(chosen);
        RenderFocus();
        Controls();
        Message(() => Tr("VoiceSwitched",SelectedVoice.Label));
        SavePrefs();
    }
    private void Message(Func<string> format)
    {
        statusMessage = format;
        if (!closing) Status.Text = format();
    }
    private void LanguageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new LanguageWindow { Owner=this };
        if (dialog.ShowDialog() == true) ApplyUiLanguage(dialog.SelectedLanguage);
    }
    private void ApplyUiLanguage(string code)
    {
        if (code == UiText.LanguageCode) return;
        UiText.SetLanguage(code);
        prefs.UiLanguage = UiText.LanguageCode;
        Language = XmlLanguage.GetLanguage(prefs.UiLanguage);
        foreach (DemoVoice voice in VoiceBox.Items) voice.RefreshLabel();
        foreach (AttemptSummary take in CompareA.Items) take.RefreshLabel();
        UpdateNavigation();
        ShowDevice(currentDevice);
        UpdateMicLabel();
        SetPreviewExpanded(previewExpanded);
        ConfigurePassage(word >= 0 ? word : null);
        if (page == "history") RenderHistory(Math.Max(35,historyVisibleCount));
        if (page == "compare")
        {
            UpdateCompareNotice();
            if (CompareControls.Visibility == Visibility.Visible && left != null && right != null &&
                (CompareA.SelectedItem as AttemptSummary)?.Id == left.Id && (CompareB.SelectedItem as AttemptSummary)?.Id == right.Id)
                RenderCompare();
        }
        if (statusMessage != null) Status.Text = statusMessage();
        SavePrefs();
    }
    private async Task Safely(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (!closing) { Message(() => Diagnostic(e.Message)); }
        catch (Exception) when (closing) { }
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        ShowPage("practice"); RenderAttempt(null);
        timer.Start();
        await Safely(async () => {
            try
            {
                await Task.WhenAll(RefreshDevices(), LoadHistory(), LoadDevice());
                if (Guid.TryParse(prefs.PendingId,out var recovered) && !string.IsNullOrWhiteSpace(prefs.PendingReference))
                {
                    string id=recovered.ToString();
                    if (history.Any(h=>h.Id==id))
                    {
                        await OpenAttempt(await engine.Get(id));
                        ClearPending(); SavePrefs(); Message(() => Tr("SavedScore")); return;
                    }
                    if (File.Exists(StorePaths.Draft(id)) || File.Exists(StorePaths.Audio(id)))
                    {
                        sentenceSession=await RestoreSession(prefs.PendingSentence);
                        reference=prefs.PendingReference; pendingId=id; pendingFocus=prefs.PendingWord;
                        RenderAttempt(null,0,pendingFocus);
                        Message(() => Tr("PendingRecovered")); return;
                    }
                }
                string text=reference.ReplaceLineEndings("\n").Trim();
                var last = history.FirstOrDefault(h => h.SentencePractice==null && h.Reference.ReplaceLineEndings("\n").Trim()==text);
                if (last != null) RenderAttempt(await engine.Get(last.Id));
                Message(() => MicBox.SelectedItem == null ? Tr("NoMicReady") : Tr("Ready"));
            }
            finally { phase = Phase.Idle; Controls(); }
        });
    }

    private void Controls()
    {
        bool idle = phase == Phase.Idle;
        RecordButton.IsEnabled = phase == Phase.Recording || (idle && MicBox.SelectedItem != null);
        RecordButton.Content = phase switch { Phase.Recording => Tr("StopAndScore"), Phase.OpeningMic => Tr("OpeningMic"),
            Phase.Assessing => Tr("Assessing"), _ => sentenceSession!=null ? Tr("RecordThisSentence") :
                attempt == null ? Tr("StartRecording") : Tr("ReadAgain") };
        RecordButton.Tag=FindResource(phase==Phase.Recording ? "IconStop" : "IconMic");
        RecordButton.Background = RecordButton.BorderBrush = Color(phase == Phase.Recording ? "PronunciationInk" : "Accent");
        MicMeter.Visibility = phase == Phase.Recording ? Visibility.Visible : Visibility.Collapsed;
        PlayWhole.IsEnabled = idle && attempt != null;
        PlayWhole.Visibility = attempt == null ? Visibility.Collapsed : Visibility.Visible;
        PlaySentence.IsEnabled = idle && CurrentSection?.CanPlay == true;
        PassageNav.IsEnabled = PreviewPanel.IsEnabled = idle || phase==Phase.Recording;
        VoiceBox.IsEnabled = idle;
        PlayExample.Content = practiceSections.Count>1 ? Tr("ExamplePassage") : Tr("ExampleSentence");
        ImportButton.IsEnabled = ChangeSentence.IsEnabled = MicBox.IsEnabled = RefreshMic.IsEnabled = idle;
        Navigation.IsEnabled = Editor.IsEnabled = idle;
        PlayWord.IsEnabled = idle && SelectedWord()?.CanPlay == true;
        UpdateExampleControls(idle);
        UpdateSentenceControls(idle);
        PlayExtra.IsEnabled = idle;
        RescoreBritish.Visibility = attempt is { BritishReference: false } ? Visibility.Visible : Visibility.Collapsed;
        RescoreBritish.IsEnabled = idle;
        NextMarked.IsEnabled = idle;
        RetryButton.Visibility = idle && pendingId != null ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task RefreshDevices()
    {
        List<InputDevice> devices;
        try { devices = await Task.Run(StudioAudio.Inputs); }
        catch (Exception e) { devices = []; MicBox.ToolTip = e.Message; }
        selectingMic = true;
        try
        {
            MicBox.ItemsSource = devices;
            MicBox.SelectedItem = devices.FirstOrDefault(d => d.Id == prefs.MicrophoneId)
                ?? devices.FirstOrDefault(d => d.Name.Contains("XIBERIA K03", StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(d => d.Default) ?? devices.FirstOrDefault();
            if (devices.Count == 0) MicSettings.IsExpanded = true;
        }
        finally { selectingMic = false; UpdateMicLabel(); }
        Controls();
    }
    private async void RefreshMicClick(object s, RoutedEventArgs e) => await Safely(async () => {
        await RefreshDevices();
        Message(() => MicBox.SelectedItem == null ? Tr("NoMicImport") : Tr("MicRefreshed"));
    });
    private void MicChanged(object s, SelectionChangedEventArgs e)
    {
        UpdateMicLabel();
        if (!selectingMic && prefs != null && MicBox.SelectedItem is InputDevice mic)
        { prefs.MicrophoneId = mic.Id; SavePrefs(); Controls(); }
    }
    private void UpdateMicLabel() => MicDeviceName.Text = (MicBox.SelectedItem as InputDevice)?.Name ?? Tr("NoMicSelected");
    private void SavePrefs()
    {
        try { prefs.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message(() => Tr("SavePrefsFailed",Diagnostic(e.Message))); }
    }
    private async Task LoadHistory() => history = await engine.History();
    private async Task LoadDevice() => ShowDevice((await engine.Health()).Device);
    private void ShowDevice(InferenceDevice? device)
    {
        if (device == null) return;
        currentDevice = device;
        DeviceLabel.Text = device.Kind == "cuda" ? Tr("LocalGpu",device.Name.Replace("NVIDIA GeForce ", "")) : Tr("LocalCpu");
        DeviceLabel.ToolTip = string.IsNullOrWhiteSpace(device.Note) ? device.Name : Diagnostic(device.Note);
    }
    private void ShowPage(string destination)
    {
        StopAudio();
        pageRevision++; compareRevision++;
        page = destination;
        PracticePage.Visibility = page == "practice" ? Visibility.Visible : Visibility.Collapsed;
        ComparePage.Visibility = page == "compare" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
        BodyScroll.ScrollToTop();
        UpdateNavigation();
    }
    private void UpdateNavigation()
    {
        foreach (var (button, name) in new[] {(PracticeNav,"practice"),(CompareNav,"compare"),(HistoryNav,"history")})
        {
            bool active = page == name;
            button.Background = active ? Color("AccentSoft") : Brushes.Transparent;
            button.Foreground = active ? Color("Masthead") : Color("HeaderText");
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            AutomationProperties.SetItemStatus(button, active ? Tr("ActivePage") : "");
        }
    }
    private void PracticeClick(object s, RoutedEventArgs e) { ShowPage("practice"); Message(() => Tr("NavPractice")); }
    private void EditSentenceClick(object s, RoutedEventArgs e)
    {
        Editor.Visibility = Editor.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (Editor.IsVisible)
        {
            SentenceBox.Text=reference; SentenceBox.Focus(); SentenceBox.CaretIndex=reference.Length;
        }
    }
    private void SentenceKeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; ApplySentence(); }
        if (e.Key == Key.Escape) { e.Handled = true; CloseEditor(); }
    }
    private void CancelEditClick(object s, RoutedEventArgs e) => CloseEditor();
    private void CloseEditor()
    {
        Editor.Visibility = Visibility.Collapsed;
        ChangeSentence.Focus();
    }
    private void ApplySentenceClick(object s, RoutedEventArgs e) => ApplySentence();
    private void ApplySentence()
    {
        var text = SentenceBox.Text.Trim();
        if (!Regex.IsMatch(text,"[A-Za-z]") || text.Length > 6000) { Message(() => Tr("InvalidText")); return; }
        StopAudio();
        if (reference != text)
        {
            reference = text; prefs.Reference = text;
            RestorePendingForView(); SavePrefs(); RenderAttempt(null);
        }
        CloseEditor(); Message(() => Tr("TextReady"));
    }
    private void PageFrameSizeChanged(object s, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        bool narrow = e.NewSize.Width < 875;
        PracticeGrid.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 16);
        PracticeGrid.ColumnDefinitions[2].Width = narrow ? new(0) : new(1,GridUnitType.Star);
        Grid.SetColumn(FeedbackColumn, narrow ? 0 : 2);
        Grid.SetRow(FeedbackColumn, narrow ? 1 : 0);
        FeedbackColumn.Margin = narrow ? new Thickness(0,12,0,0) : new Thickness(0);
        bool stackTakes = e.NewSize.Width < 700;
        ComparePair.Columns = stackTakes ? 1 : 2;
        CompareACard.Margin = stackTakes ? new(0) : new(0,0,7,0);
        CompareBCard.Margin = stackTakes ? new(0,10,0,0) : new(7,0,0,0);
    }

    private List<Word>? PracticeWords => attempt?.Words ?? prepared?.Words;
    private Word? SelectedWord() => PracticeWords?.ElementAtOrDefault(word);
    private ReadingSection? CurrentSection => practiceSections.ElementAtOrDefault(sectionIndex);
    private void RenderAttempt(Attempt? result, int position=0, int? selectedWord=null)
    {
        attempt = result;
        if (result != null) reference = result.Reference;
        string owner=ExampleReference;
        bool load = preparedText != reference || preparedOwner!=owner;
        if (sourcesOwner!=owner) SetSources(owner,[]);
        if (load) { preparedText=reference; preparedOwner=owner; prepared=null; SetPreviewExpanded(false); }
        sectionIndex = position;
        FullDetails.IsExpanded = false;
        TranscriptDetails.IsExpanded = false;
        ConfigurePassage(selectedWord);
        if (load) _ = LoadPrepared(reference,owner,++prepareRevision,position,selectedWord);
    }
    private async Task LoadPrepared(string text,string owner,int revision,int position,int? selected)
    {
        int generation=sourceRevision;
        try
        {
            var info=await engine.Prepare(text,owner);
            if (closing || revision!=prepareRevision) return;
            prepared=info;
            if (generation==sourceRevision) SetSources(owner,info.Examples);
            else info.Examples=importedTracks;
            if (attempt==null) { sectionIndex=position; ConfigurePassage(selected); }
            else { RenderFocus(); Controls(); }
        }
        catch (Exception) when (closing || revision!=prepareRevision) { }
        catch (Exception e) { preparedText=null; Controls(); Message(() => Tr("PrepareFailed",Diagnostic(e.Message))); }
    }
    private void ConfigurePassage(int? selectedWord=null)
    {
        var result=attempt;
        practiceSections=result?.Sections ?? (prepared?.Sections.Count>1 ? prepared.Sections : []);
        sectionIndex=Math.Clamp(sectionIndex,0,Math.Max(0,practiceSections.Count-1));
        if (selectedWord is int selected && (PracticeWords?.ElementAtOrDefault(selected)==null || CurrentSection?.Contains(selected)==false))
            selectedWord=null;
        bool passage=practiceSections.Count>0;
        PassageNav.Visibility=PreviewPanel.Visibility=passage ? Visibility.Visible : Visibility.Collapsed;
        OriginalSound.Visibility=TranscriptCard.Visibility=result==null ? Visibility.Collapsed : Visibility.Visible;
        ReviewLegend.Visibility=result!=null && practiceSections.Any(s=>!s.Reliable) ? Visibility.Visible : Visibility.Collapsed;
        PlaySentence.Visibility=FullDetails.Visibility=passage && result!=null ? Visibility.Visible : Visibility.Collapsed;
        PlayWhole.Content=result==null ? Tr("PlayOriginal") : passage ? Tr("PlayPassage") : Tr("PlaySentence");
        PassageSummary.Text=passage && result!=null ? Tr("ScoredCount",result.AssessedCount,practiceSections.Count) + "  ·  " +
            (result.Reliable ? Tr("PassageScoreValue",result.Score("total")) : Tr("PassageReview")) : "";
        PassageSummary.ToolTip=result?.Reliable==true ? Tr("WeightedHelp") : Reasons(result?.Reasons ?? []);
        FullText.Text=passage && result!=null ? (result.Transcript.Length>0 ? result.Transcript : Tr("NoSpeech"))+
            (result.TextDiff.Count>0 ? Environment.NewLine+string.Join("  ·  ",result.TextDiff.Select(Diagnostic)) : "") : "";
        BuildPreview();
        RenderSection(selectedWord);
    }
    private void BuildPreview()
    {
        PreviewText.Inlines.Clear();
        previewSentences.Clear();
        markedPreviewWords.Clear();
        previewFocus=-1;
        int offset=0;
        foreach (var section in practiceSections)
        {
            int at=reference.IndexOf(section.Reference,offset,StringComparison.Ordinal);
            if (at>=offset) PreviewText.Inlines.Add(new Run(reference[offset..at]));
            else if (previewSentences.Count>0) PreviewText.Inlines.Add(new Run(" "));
            var span=BuildPreviewSentence(section,previewSentences.Count);
            PreviewText.Inlines.Add(span);
            previewSentences.Add(span);
            if (at>=offset) offset=at+section.Reference.Length;
        }
        if (previewSentences.Count>0 && offset<reference.Length) PreviewText.Inlines.Add(new Run(reference[offset..]));
        int missing=markedPreviewWords.Values.Count(item=>item.Word.Missing);
        int pronunciation=markedPreviewWords.Count-missing;
        PreviewHint.Text=markedPreviewWords.Count>0 ? Tr("MarkedHint") : Tr("JumpHint");
        IEnumerable<int> marks = markedPreviewWords.Keys;
        if (practiceSections.Count == 0 && attempt is {Reliable:true} single)
            marks = single.Words.Where(w=>w.NeedsReview && (w.CanPlay || w.Missing)).Select(w=>w.Index);
        reviewWords = marks.Order().ToArray();
        NextMarked.Visibility=reviewWords.Length>0 ? Visibility.Visible : Visibility.Collapsed;
        NextMarked.Content=reviewWords.Length==1 ? Tr("ViewFlag") : Tr("NextFlag");
        PronunciationLegend.Text=Tr("PronunciationCount",pronunciation);
        PronunciationLegend.Visibility=pronunciation>0 ? Visibility.Visible : Visibility.Collapsed;
        MissingWordLegend.Text=Tr("MissingCount",missing);
        MissingWordLegend.Visibility=missing>0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ExpandPreviewClick(object sender, RoutedEventArgs e)
    {
        SetPreviewExpanded(!previewExpanded);
        if (!previewExpanded) MarkPreview();
    }
    private void SetPreviewExpanded(bool expanded)
    {
        previewExpanded = expanded;
        PreviewScroll.MaxHeight = expanded ? double.PositiveInfinity : CollapsedPreviewHeight;
        ExpandPreview.Content = expanded ? Tr("CollapseText") : Tr("ExpandText");
    }
    private void PreviewScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource == PreviewScroll)
            ExpandPreview.Visibility = e.ExtentHeight > CollapsedPreviewHeight + 1 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void NextMarkedClick(object sender, RoutedEventArgs e)
    {
        if (reviewWords.Length == 0) return;
        int next = reviewWords.FirstOrDefault(position => position > word, reviewWords[0]);
        if (practiceSections.Count > 0)
            OpenPreviewWord(practiceSections.FindIndex(section => section.Contains(next)), next);
        else
        {
            StopAudio(); RenderSection(next); BringFeedbackIntoView();
        }
    }
    private void OpenPreviewWord(int section, int position)
    {
        SelectSection(section, position);
        BringFeedbackIntoView();
    }
    private void BringFeedbackIntoView()
    {
        if (phase == Phase.Idle) FeedbackCard.BringIntoView();
    }
    private Span BuildPreviewSentence(ReadingSection section, int index)
    {
        var span=new Span();
        string text=section.Reference;
        bool review=attempt!=null && !section.Reliable;
        string sentenceLabel=Tr("GoToSentence",index+1) + (review ? " · " + Tr(section.Status=="missing" ? "PossibleSkip" : "NeedsReview") : "");
        void AddText(string content)
        {
            if (content.Length==0) return;
            if (string.IsNullOrWhiteSpace(content)) { span.Inlines.Add(new Run(content)); return; }
            var link=new Hyperlink(new Run(content)) { TextDecorations=review ? TextDecorations.Underline : null, ToolTip=sentenceLabel };
            link.SetBinding(TextElement.ForegroundProperty,new Binding(nameof(Span.Foreground)) {Source=span});
            AutomationProperties.SetName(link,sentenceLabel+" · "+content.Trim());
            link.Click+=(_,_)=>SelectSection(index);
            span.Inlines.Add(link);
        }

        int cursor=0, wordIndex=section.FirstWord;
        if (attempt!=null && section.Reliable)
        {
            foreach (Match token in Tokens.Matches(text))
            {
                int position=wordIndex++;
                if (!section.Contains(position)) break;
                var item=attempt.Words.ElementAtOrDefault(position);
                if (item==null || item.Index!=position || !item.NeedsReview || (!item.CanPlay && !item.Missing)) continue;
                AddText(text[cursor..token.Index]);
                string reason=item.Missing ? Tr("PossibleOmission") : Tr("PronunciationFlag");
                string hint=item.Candidate is { } phone
                    ? Tr("PhoneAlternative",phone.Expected,phone.Heard) : reason;
                var link=new Hyperlink(new Run(token.Value)) {
                    TextDecorations=item.Missing ? TextDecorations.Strikethrough : TextDecorations.Underline,
                    FontWeight=FontWeights.Bold, ToolTip=$"{token.Value} · {hint}" };
                AutomationProperties.SetName(link,Tr("SentenceNumber",index+1) + $" · {token.Value} · {reason}");
                AutomationProperties.SetHelpText(link,hint);
                link.Click+=(_,_)=>OpenPreviewWord(index,position);
                span.Inlines.Add(link);
                markedPreviewWords.Add(position,(link,item));
                StylePreviewWord(position,false);
                cursor=token.Index+token.Length;
            }
        }
        AddText(text[cursor..]);
        return span;
    }
    private void StylePreviewWord(int position, bool selected)
    {
        if (!markedPreviewWords.TryGetValue(position,out var mark)) return;
        Brush ink=Color(mark.Word.Missing ? "ReviewInk" : "PronunciationInk");
        mark.Link.Foreground=selected ? Brushes.White : ink;
        mark.Link.Background=selected ? ink : Color(mark.Word.Missing ? "ReviewFill" : "PronunciationFill");
    }
    private void SelectSection(int index, int? selectedWord=null)
    {
        if (index<0 || index>=practiceSections.Count || (index==sectionIndex && selectedWord==null)) return;
        StopAudio(); sectionIndex=index; RenderSection(selectedWord);
    }
    private void MarkPreview()
    {
        if (practiceSections.Count==0) return;
        PracticeNavigator.ShowPosition(sectionIndex,practiceSections.Count);
        for (int i=0;i<previewSentences.Count;i++)
        {
            var span=previewSentences[i];
            bool review=attempt!=null && !practiceSections[i].Reliable;
            span.Background=i==sectionIndex ? Color(review ? "ReviewFill" : "AccentSoft") : Brushes.Transparent;
            span.Foreground=Color(review ? "ReviewInk" : i==sectionIndex ? "Accent" : "Ink");
            span.FontWeight=i==sectionIndex ? FontWeights.Bold : FontWeights.Normal;
        }
        var active=previewSentences[sectionIndex];
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>{
            if (!PreviewPanel.IsVisible || previewSentences.ElementAtOrDefault(sectionIndex)!=active || !PreviewText.IsArrangeValid) return;
            Span anchor=markedPreviewWords.TryGetValue(word,out var mark) ? mark.Link : active;
            var top=anchor.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
            var bottom=anchor.ContentEnd.GetCharacterRect(LogicalDirection.Backward).Bottom;
            double view=PreviewScroll.ViewportHeight, scroll=PreviewScroll.VerticalOffset;
            if (top<scroll || bottom-top>view) PreviewScroll.ScrollToVerticalOffset(top);
            else if (bottom>scroll+view) PreviewScroll.ScrollToVerticalOffset(bottom-view);
        }));
    }
    private void RenderSection(int? selectedWord=null)
    {
        var result = attempt;
        var section = CurrentSection;
        var words = PracticeWords?.Where(w => section==null || section.Contains(w.Index)).ToList() ?? [];
        word = selectedWord ?? (words.Where(w => w.NeedsReview).OrderByDescending(w => w.ReviewPriority).FirstOrDefault()
              ?? words.FirstOrDefault(w => w.CanPlay) ?? words.FirstOrDefault())?.Index ?? -1;
        var transcript = section?.Transcript ?? result?.Transcript;
        Transcript.Text = result == null ? Tr("TranscriptPending") :
            string.IsNullOrWhiteSpace(transcript) ? (section==null ? Tr("NoClearSpeech") : Tr("SectionNoSpeech")) : transcript;
        TextDiff.Text = result == null ? "" : string.Join("   ·   ",(section?.TextDiff ?? result.TextDiff).Select(Diagnostic));
        var extra = section?.Extra ?? result?.Extra;
        if (extra?.Count > 0) TextDiff.Text += (TextDiff.Text.Length > 0 ? "   ·   " : "") + Tr("ExtraAlignment");
        TranscriptHeading.Text = (section == null ? Tr("TranscriptTitle") : Tr("SentenceTranscript")) + (TextDiff.Text.Length > 0 ? " · " + Tr("Differences") : "");
        TextDiff.Visibility = TextDiff.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool reliable = section?.Reliable ?? result?.Reliable ?? false;
        string accent=result?.AccentLabel ?? Tr("British");
        ScoreTitle.Text = (result==null ? Tr("ReadingScore") : section==null ? Tr("SentenceScore") : Tr("NumberedScore",section.Index+1)) + " · " + accent;
        ScoreBasis.Text = (result?.BritishReference==false
            ? Tr("BasisLegacy")
            : Tr("BasisUK")) + Environment.NewLine +
            Tr("BasisModel");
        ScoreNote.Text = result == null ? Tr("ScorePending") : section?.Reliable == true
            ? Tr("SectionTime",section.Start,section.End) : reliable
            ? result.Created.ToLocalTime().ToString("MM-dd HH:mm") + "  ·  " + Tr("ElapsedScore",result.Elapsed)
            : Reasons(section?.Reasons ?? result.Reasons);
        if ((section==null ? result?.Device : section.Device) is { } used) ScoreNote.Text += "  ·  " + used.Processor;
        ScoreNote.Foreground = Color(result != null && !reliable ? "ReviewInk" : "Muted");
        ScoresPanel.Visibility = reliable ? Visibility.Visible : Visibility.Collapsed;
        var scores = section?.Scores ?? result?.Scores;
        void Metric(string key, TextBlock value, ProgressBar? bar = null)
        {
            double score = scores?.GetValueOrDefault(key, double.NaN) ?? double.NaN;
            bool available = double.IsFinite(score);
            value.Text = available ? score.ToString("0") : "—";
            if (bar == null) return;
            bar.Visibility = available ? Visibility.Visible : Visibility.Hidden;
            bar.Value = available ? score : 0;
        }
        Metric("total",Total); Metric("accuracy",Accuracy,AccuracyBar);
        Metric("fluency",Fluency,FluencyBar); Metric("prosodic",Prosody,ProsodyBar);
        PauseSummary.Text = reliable && result!=null ? PauseDescription(result,section,words) : "";
        PauseSummary.Visibility = PauseSummary.Text.Length>0 ? Visibility.Visible : Visibility.Collapsed;
        PlayExtra.Visibility = extra?.Any(x=>x.End>x.Start)==true ? Visibility.Visible : Visibility.Collapsed;
        ReaderScroll.ScrollToTop();
        MarkPreview(); RenderWords(); RenderFocus(); DrawWave(); Controls();
    }
    private static string PauseDescription(Attempt result, ReadingSection? section, List<Word> words)
    {
        var gaps=words.Zip(words.Skip(1))
            .Where(pair=>pair.First.CanPlay && pair.Second.CanPlay && pair.Second.Index==pair.First.Index+1)
            .Select(pair=>(Before:pair.First.Text,After:pair.Second.Text,Seconds:pair.Second.Start!.Value-pair.First.End!.Value)).ToList();
        var pauses=gaps.Where(gap=>Math.Round(gap.Seconds,4)>.25).OrderByDescending(gap=>gap.Seconds).ToList();
        var lines=new List<string>();
        if (pauses.Count>0)
        {
            var longest=pauses[0];
            lines.Add(Tr("WordPauses",pauses.Count,longest.Seconds,longest.Before,longest.After));
        }
        else if (gaps.Count>0) lines.Add(Tr("NoWordPauses"));
        if (section is {Index:>0} && result.Sections.ElementAtOrDefault(section.Index-1)?.Reliable==true &&
            words.FirstOrDefault() is {CanPlay:true} first && result.Words.ElementAtOrDefault(first.Index-1) is {CanPlay:true} previous)
            lines.Add(Tr("BetweenSentences",Math.Max(0,first.Start!.Value-previous.End!.Value)));
        return string.Join(Environment.NewLine,lines);
    }
    private Button TokenButton(string text, bool selected, bool flagged, bool large, bool missing=false)
    {
        var b = new Button { Content=new TextBlock { Text=text, TextWrapping=TextWrapping.Wrap },
            FontFamily=new FontFamily("Georgia, Microsoft YaHei UI"), FontSize=large ? 33 : 18,
            Margin=new Thickness(0,0,2,2), Padding=large ? new Thickness(3,1,3,2) : new Thickness(8,3,8,3) };
        SetTokenStyle(b, selected, flagged, missing);
        AutomationProperties.SetName(b, text);
        return b;
    }
    private void SetTokenStyle(Button button, bool selected, bool flagged, bool missing)
    {
        var ink = Color(flagged ? (missing ? "ReviewInk" : "PronunciationInk") : "Accent");
        var fill = Color(flagged ? (missing ? "ReviewFill" : "PronunciationFill") : "AccentSoft");
        button.Foreground = flagged || selected ? ink : Color("Ink");
        button.Background = selected ? fill : Brushes.Transparent;
        button.BorderBrush = flagged || selected ? ink : Brushes.Transparent;
        button.BorderThickness = new(0,0,0,2);
        if (button.Content is TextBlock label) label.TextDecorations = flagged && missing ? TextDecorations.Strikethrough : null;
    }
    private void RenderWords()
    {
        SentenceWords.Children.Clear();
        var words = PracticeWords;
        ReaderText.Visibility = words==null ? Visibility.Visible : Visibility.Collapsed;
        if (words==null) { ReaderText.Text=reference; ReaderText.FontSize=reference.Length>90 ? 25 : 35; return; }
        var section=CurrentSection;
        string text=section?.Reference ?? reference;
        var emphasized = words.Where(w => (section==null || section.Contains(w.Index)) && w.NeedsReview && ((section?.Reliable ?? attempt?.Reliable ?? false) || w.Missing))
            .OrderByDescending(w => w.ReviewPriority).Take(2).Select(w => w.Index).ToHashSet();
        bool Emphasize(int position) => emphasized.Contains(position) || (word==position && Array.BinarySearch(reviewWords,position)>=0);
        int last = 0, n = section?.FirstWord ?? 0;
        foreach (Match m in Tokens.Matches(text))
        {
            if (m.Index > last) AppendPunctuation(text[last..m.Index]);
            int i=n++;
            var w = words.ElementAtOrDefault(i);
            int end=m.Index+m.Length;
            while(end<text.Length && ",.!?;:%)]}’”\"…".Contains(text[end])) end++;
            var b = TokenButton(text[m.Index..end],word==i,Emphasize(i),true,w?.Missing==true);
            b.Tag=i;
            if(section!=null) b.FontSize=28;
            b.ToolTip = w?.Missing == true ? Tr("PossibleSkip") : Tr("WordHelp",m.Value);
            AutomationProperties.SetName(b, Tr("SelectWordName",m.Value));
            b.Click += (_,_) => {
                StopAudio();
                int previous=word; word=i;
                foreach (var token in SentenceWords.Children.OfType<Button>())
                    if (token.Tag is int id && (id==previous || id==word))
                        SetTokenStyle(token, id==word, Emphasize(id), words.ElementAtOrDefault(id)?.Missing==true);
                RenderFocus(); DrawWave(); Controls(); BringFeedbackIntoView();
            };
            SentenceWords.Children.Add(b);
            last=end;
        }
        if (last < text.Length) AppendPunctuation(text[last..]);
    }
    private void AppendPunctuation(string piece)
    {
        var text = piece.Trim();
        if (text.Length>0) SentenceWords.Children.Add(new TextBlock {Text=text, FontFamily=new FontFamily("Georgia"),
            FontSize=33, Margin=new Thickness(-2,0,3,0)});
    }
    private static string Describe(Attempt a, Word? w)
    {
        if (w == null) return a.Reliable ? Tr("SelectForAudio") : Tr("NoAlignedWord");
        if (w.Missing) return Tr("MissingFeedback");
        var section=a.SectionFor(w.Index);
        if (section?.Reliable==false) return Reasons(section.Reasons);
        if (!(section?.Reliable ?? a.Reliable)) return Tr("UnreliableFeedback");
        if (w.Guessed) return Tr("GuessedFeedback");
        if (!w.NeedsReview) return Tr("ClearFeedback");
        var likely = w.Candidate;
        if (likely == null) return Tr("UncertainFeedback");
        string cue = likely.Expected switch {
            "θ" => Tr("CueTh"),
            "ð" => Tr("CueVoicedTh"),
            "ɪ" => Tr("CueKit"),
            "iː" or "i" => Tr("CueFleece"),
            "æ" => Tr("CueTrap"),
            "ɜː" => Tr("CueNurse"),
            "ɑː" => Tr("CuePalm"),
            "ɒ" => Tr("CueLot"),
            "əʊ" => Tr("CueGoat"),
            "l" => Tr("CueL"),
            "ɹ" or "r" => Tr("CueR"),
            "v" => Tr("CueV"),
            "f" => Tr("CueF"),
            "w" => Tr("CueW"),
            "s" => Tr("CueS"),
            "z" => Tr("CueZ"),
            "ʃ" => Tr("CueSh"),
            "ŋ" => Tr("CueNg"),
            _ => Tr("CueGeneral")
        };
        return Tr("SoundFeedback",likely.Expected,likely.Heard,cue);
    }
    private void RenderFocus()
    {
        if (previewFocus!=word)
        {
            StylePreviewWord(previewFocus,false);
            StylePreviewWord(word,true);
            previewFocus=word;
        }
        var w = SelectedWord();
        var section=CurrentSection;
        var target=prepared?.Words.ElementAtOrDefault(word);
        FocusWord.Text = w?.Text ?? (attempt == null ? Tr("SelectWord") : section?.Status=="missing" ? Tr("PossibleSkip") : Tr("ListenBack"));
        string? scoringIpa=attempt==null ? target?.Ipa : w?.Ipa;
        bool british = SelectedVoice.British;
        string? demoIpa = SelectedVoice.Track!=null ? null : british ? target?.BritishIpa : target?.AmericanIpa;
        FocusIpa.Text = string.IsNullOrEmpty(demoIpa) ? "" : $"{(british ? Tr("UKExample") : Tr("USExample"))} /{demoIpa}/";
        FocusIpa.Visibility = FocusIpa.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        bool showTarget=attempt==null ? scoringIpa!=demoIpa : (section?.Reliable ?? attempt.Reliable);
        ScoringIpa.Text = showTarget && !string.IsNullOrEmpty(scoringIpa)
            ? Tr("ScoringIpa",attempt?.AccentLabel ?? Tr("British"),scoringIpa) : "";
        ScoringIpa.Visibility = ScoringIpa.Text.Length>0 ? Visibility.Visible : Visibility.Collapsed;
        FeedbackText.Text = attempt==null ? (w?.Guessed==true ? Tr("UnknownBefore") :
            Tr("ListenBefore")) :
            w==null && section?.Reasons.Count>0 ? Reasons(section.Reasons) : Describe(attempt,w);
        RangeText.Text = w?.CanPlay==true ? w.Text + " · " + Tr("TimeRange",w.Start,w.End) :
            attempt==null ? "" : section!=null ? (section.CanPlay ? Tr("TimeRange",section.Start,section.End) : Tr("NotLocated")) : Tr("Seconds",attempt.Duration);
        bool reliable = section?.Reliable ?? attempt?.Reliable ?? false;
        bool review = attempt != null && (!reliable || w?.Missing == true);
        bool pronunciation = reliable && w?.NeedsReview == true && !review;
        FeedbackCallout.Background = Color(review ? "ReviewFill" : pronunciation ? "PronunciationFill" : "SoftSurface");
        FeedbackCallout.BorderBrush = Color(review ? "ReviewInk" : pronunciation ? "PronunciationInk" : "Line");
        PhoneDetails.Children.Clear();
        RenderRetake();
        PhoneDetails.Visibility = reliable && w is {Missing:false, Phones.Count:>0} ? Visibility.Visible : Visibility.Collapsed;
        if (PhoneDetails.Visibility != Visibility.Visible || w == null) return;
        foreach (var p in w.Phones.Take(30))
        {
            bool marked = !w.Guessed && p.HasAlternative;
            string hint = marked ? Tr("PhoneAlternative",p.Expected,p.Heard) : Tr("TargetPhone",p.Expected);
            if (p.End > p.Start) hint += " · " + Tr("TimeRange",p.Start,p.End);
            var tag = new Border { Background=Color(marked ? "PronunciationFill" : "SoftSurface"),
                CornerRadius=new(4), Padding=new(6,2,6,2), Margin=new(0,0,4,2), ToolTip=hint,
                Child=new TextBlock { Text="/"+p.Expected+"/", Foreground=Color(marked ? "PronunciationInk" : "Muted") } };
            AutomationProperties.SetName(tag, hint);
            PhoneDetails.Children.Add(tag);
        }
    }
    private void WaveSizeChanged(object s, SizeChangedEventArgs e) => DrawWave();
    private void DrawWave()
    {
        if (Wave == null) return;
        Wave.Children.Clear();
        var section=CurrentSection;
        var peaks = section?.Peaks ?? attempt?.Peaks;
        double width=Wave.ActualWidth, height=Wave.ActualHeight;
        if (width<2 || height<2) return;
        if (peaks == null || peaks.Count==0)
        {
            var baseline=new Rectangle {Width=width, Height=1, Fill=Color("Line")};
            Canvas.SetTop(baseline,height*.5); Wave.Children.Add(baseline); return;
        }
        var w = SelectedWord();
        double step=width/peaks.Count;
        Brush selectedInk=Color("Accent"), waveInk=Color("WaveInk");
        for(int i=0;i<peaks.Count;i++)
        {
            double t=(section?.Start ?? 0)+(i+.5)/peaks.Count*((section?.End-section?.Start) ?? attempt!.Duration),
                   h=Math.Clamp(peaks[i]*height*.9,2,height);
            bool selected=w?.CanPlay==true && t>=w.Start && t<=w.End;
            var bar=new Rectangle {Width=Math.Max(1,step-1.5),Height=h,RadiusX=1.3,RadiusY=1.3,
                Fill=selected ? selectedInk : waveInk};
            Canvas.SetLeft(bar,i*step);Canvas.SetTop(bar,(height-h)*.5);Wave.Children.Add(bar);
        }
    }

    private async void RecordClick(object s, RoutedEventArgs e) => await Safely(async () => {
        if (phase==Phase.Recording) { await FinishRecording(); return; }
        if (phase!=Phase.Idle || MicBox.SelectedItem is not InputDevice mic) return;
        StopAudio(); Editor.Visibility=Visibility.Collapsed;
        string id=Guid.NewGuid().ToString();
        phase=Phase.OpeningMic; Controls();
        try
        {
            capture=await Task.Run(()=>CaptureSession.Start(mic.Id,StorePaths.Draft(id)));
            pendingFocus=sentenceSession!=null && word>=0 ? word : null;
            pendingId=id; phase=Phase.Recording; activity.Restart(); RenderAttempt(null,0,pendingFocus);
            Message(() => Tr("RecordingStarted"));
        }
        catch (Exception ex)
        {
            phase=Phase.Idle; Controls();
            throw new InvalidOperationException(Tr("MicOpenError"),ex);
        }
    });
    private async void TimerTick(object? s, EventArgs e)
    {
        if (closing) return;
        if (phase==Phase.Recording && capture!=null)
        {
            MicMeter.Value=Math.Sqrt(capture.Level);
            Message(() => Tr("RecordingClock",activity.Elapsed.TotalSeconds));
            if (capture.Finished || activity.Elapsed.TotalSeconds>=StudioAudio.MaxSeconds)
                await Safely(FinishRecording);
        }
        else if (phase==Phase.Assessing)
            Message(() => Tr("AssessmentClock",activity.Elapsed.TotalSeconds));
    }
    private async Task FinishRecording()
    {
        var recording=capture;
        if (phase!=Phase.Recording || recording==null) return;
        phase=Phase.Preparing; Controls(); capture=null;
        try { await recording.StopAsync(); }
        catch { phase=Phase.Idle; pendingId=null; throw; }
        finally { recording.Dispose(); MicMeter.Value=0; Controls(); }
        await RunAssessment();
    }
    private async void ImportClick(object s, RoutedEventArgs e) => await Safely(async () => {
        if (phase!=Phase.Idle) return;
        var pick=new OpenFileDialog {Title=Tr("ImportTitle"), Filter=Tr("WavFiles")+"|*.wav", CheckFileExists=true,
            InitialDirectory=Directory.Exists(prefs.ImportDirectory) ? prefs.ImportDirectory : System.IO.Path.Combine(StorePaths.Base,"samples")};
        if (pick.ShowDialog(this)!=true) return;
        await ImportLearnerFile(pick.FileName);
    });
    private async Task ImportLearnerFile(string file)
    {
        StopAudio(); Editor.Visibility=Visibility.Collapsed;
        string id=Guid.NewGuid().ToString();
        phase=Phase.Preparing; Controls(); Message(() => Tr("PreparingAudio"));
        try { await Task.Run(()=>StudioAudio.Import(file,StorePaths.Draft(id))); }
        catch {phase=Phase.Idle;Controls();throw;}
        prefs.ImportDirectory=System.IO.Path.GetDirectoryName(file);
        pendingFocus=sentenceSession!=null && word>=0 ? word : null;
        pendingId=id; RenderAttempt(null,0,pendingFocus); await RunAssessment();
    }
    private async void RetryClick(object s, RoutedEventArgs e) => await Safely(RunAssessment);
    private async void RescoreBritishClick(object s, RoutedEventArgs e) => await Safely(async () => {
        if (phase!=Phase.Idle || attempt is not { BritishReference:false } original) return;
        StopAudio();
        int position=sectionIndex, selected=word;
        phase=Phase.Assessing; activity.Restart(); Controls();
        try
        {
            var result=await engine.Rescore(original.Id);
            if (closing) return;
            RenderAttempt(result,position,selected);
            if (result.Device?.Kind=="mixed") await LoadDevice(); else ShowDevice(result.Device);
            await LoadHistory();
            Message(() => Tr("RescoreDone"));
        }
        finally { phase=Phase.Idle; activity.Stop(); if (!closing) Controls(); }
    });
    private async Task RunAssessment()
    {
        if (pendingId==null) return;
        prefs.PendingId=pendingId; prefs.PendingReference=reference;
        prefs.PendingSentence=sentenceSession?.Origin; prefs.PendingWord=pendingFocus; SavePrefs();
        phase=Phase.Assessing; activity.Restart(); Controls();
        try
        {
            var result=await engine.Assess(pendingId,reference,sentenceSession?.Origin);
            if (closing) return;
            int? focus=pendingFocus;
            ClearPending(); SavePrefs(); RenderAttempt(result,0,focus);
            if(result.Device?.Kind=="mixed") await LoadDevice(); else ShowDevice(result.Device);
            await LoadHistory();
            Message(() => result.Reliable ? Tr("SavedScore") : Tr("SavedTake",Reasons(result.Reasons)));
        }
        finally {phase=Phase.Idle;activity.Stop();if (!closing) Controls();}
    }

    private void StopAudio()
    {
        playbackRevision++;
        player.Stop();
        if (StopSound!=null) StopSound.Visibility=Visibility.Collapsed;
    }
    private Task Play(string path, Func<string> description, double? start=null, double? end=null) =>
        PlaySound(description,()=>player.Play(path,start,end));
    private async Task PlaySound(Func<string> description, Func<Task> sound)
    {
        int revision=++playbackRevision;
        StopSound.Visibility=Visibility.Visible; Message(() => Tr("Playing",description()));
        try { await sound(); if (revision==playbackRevision) Message(() => Tr("FinishedPlayback",description())); }
        finally { if (revision==playbackRevision) StopSound.Visibility=Visibility.Collapsed; }
    }
    private void StopSoundClick(object s,RoutedEventArgs e) {StopAudio(); Message(() => Tr("PlaybackStopped"));}
    private async void PlayWholeClick(object s,RoutedEventArgs e) => await Safely(async () => {
        if (attempt is { } take) await Play(StorePaths.Audio(take.Id),() => Tr(take.Sections.Count>0 ? "PassageAudio" : "SentenceAudio"));
    });
    private async void PlaySentenceClick(object s,RoutedEventArgs e) => await Safely(async () => {
        if (attempt!=null && CurrentSection is {CanPlay:true} section)
            await Play(StorePaths.Audio(attempt.Id),() => Tr("SentenceNumber",section.Index+1),section.Start,section.End);
    });
    private async void ExampleClick(object s,RoutedEventArgs e) => await Safely(async () => {
        if (SelectedVoice.Track is { } track)
        {
            if (sentenceSession!=null) await PlayModelSection();
            else await PlayTrack(track,() => track.Name);
            return;
        }
        var units = prepared?.Sections ?? practiceSections;
        string[] phrases = units.Count>0 ? units.Select(section=>section.Reference).ToArray() : [reference];
        await PlayDemo(phrases, () => Tr(phrases.Length>1 ? "FullPassageExample" : "FullSentenceExample"));
    });
    private async void PlayWordClick(object s,RoutedEventArgs e) => await Safely(async () => {
        if (attempt!=null && SelectedWord() is {CanPlay:true} w) await Play(StorePaths.Audio(attempt.Id),() => w.Text,w.Start,w.End);
    });
    private async void PlayWordExampleClick(object s,RoutedEventArgs e) => await Safely(async () => {
        if (SelectedWord() is not { } w) return;
        if (SelectedVoice.Track is { } track)
        {
            if (ImportedExcerpt(track,false) is { } clip) await PlayTrack(track,() => clip.Text,clip.Start,clip.End);
            return;
        }
        var target=prepared?.Words.ElementAtOrDefault(w.Index);
        await PlayDemo([target?.Text ?? w.Text], () => w.Text, target?.Sapi ?? "");
    });
    private async Task PlayDemo(string[] phrases, Func<string> description, string phones="")
    {
        if (phase!=Phase.Idle || phrases.Length==0) return;
        StopAudio();
        int revision=playbackRevision+1;
        var voice=SelectedVoice;
        bool Current() => !closing && phase==Phase.Idle && revision==playbackRevision;
        await PlaySound(() => voice.Label+" · "+description(), async () => {
            for (int i=0; i<phrases.Length && Current(); i++)
            {
                string text=phrases[i];
                DemoAudio audio;
                try
                {
                    audio = phones.Length==0 || voice.British ? await SentenceSample(voice,text).Audio
                        : await Task.Run(()=>WordVoice.SynthesizeAmerican(voice,text,phones));
                }
                catch (Exception) when (!Current()) { return; }
                if (!Current()) return;
                await player.PlayPcm(audio.Pcm,new NAudio.Wave.WaveFormat(audio.SampleRate,16,1));
                if (Current() && i+1<phrases.Length) await Task.Delay(180);
            }
        });
    }
    private async void PlayExtraClick(object s,RoutedEventArgs e) => await Safely(async () => {
        var extra=(CurrentSection?.Extra ?? attempt?.Extra)?.FirstOrDefault(x=>x.End>x.Start);
        if (attempt!=null && extra!=null) await Play(StorePaths.Audio(attempt.Id),() => Tr("ExtraSpeech"),extra.Start,extra.End);
    });

    private async void HistoryClick(object s,RoutedEventArgs e) => await Safely(async () => {
        ShowPage("history");int revision=pageRevision;
        await LoadHistory(); if (revision!=pageRevision) return;
        RenderHistory(35); Message(() => Tr("HistoryStatus"));
    });
    private void RenderHistory(int count)
    {
        HistoryRows.Children.Clear();
        HistoryCaption.Text=history.Count==0 ? Tr("HistoryEmpty") : Tr("HistoryCount",history.Count);
        HistoryBatch(0,count);
    }
    private void HistoryBatch(int offset,int count=35)
    {
        foreach (var row in history.Skip(offset).Take(count))
        {
            var content=new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition {Width=new(1,GridUnitType.Star)});
            content.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            var text=new StackPanel();
            text.Children.Add(new TextBlock {Text=row.Reference,FontFamily=new FontFamily("Georgia, Microsoft YaHei UI"),FontSize=20,TextWrapping=TextWrapping.Wrap,
                MaxHeight=52,TextTrimming=TextTrimming.CharacterEllipsis});
            string coverage=row.SentenceCount>0 ? "  ·  " + Tr("ScoredCount",row.AssessedCount,row.SentenceCount) : "";
            if (row.SentencePractice is { } origin) coverage+="  ·  "+Tr("RetakeNumber",origin.SectionIndex+1);
            text.Children.Add(new TextBlock {Text=row.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "  ·  " + Tr("ShortSeconds",row.Duration) + coverage+"  ·  "+row.AccentLabel,
                Foreground=Color("Muted"),FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new(0,4,0,0)});
            content.Children.Add(text);
            var badge=new Border { Background=Color(row.Reliable?"AccentSoft":"ReviewFill"), CornerRadius=new(5),
                VerticalAlignment=VerticalAlignment.Center, Margin=new(12,0,0,0), Padding=new(8,4,8,4),
                Child=new TextBlock {Text=row.Reliable ? Tr(row.SentenceCount>0 ? "ReferenceScoreValue" : "OverallValue",row.Total?.ToString("0") ?? "—") : Tr("NeedsReview"),
                    FontWeight=FontWeights.SemiBold, Foreground=Color(row.Reliable?"Accent":"ReviewInk")} };
            Grid.SetColumn(badge,1);content.Children.Add(badge);
            var button=new Button {Content=content,HorizontalContentAlignment=HorizontalAlignment.Stretch,Padding=new(15,10,15,10),Margin=new(0,0,0,7)};
            AutomationProperties.SetName(button, row.Reference + " " + row.Label);
            button.Click+=async (_,_)=>await Safely(async ()=>{
                int rev=++pageRevision;var item=await engine.Get(row.Id);if(rev!=pageRevision) return;
                phase=Phase.Preparing; Controls();
                try { await OpenAttempt(item); ShowPage("practice"); Message(() => Tr("HistoryOpened",item.Engine)); }
                finally { phase=Phase.Idle; Controls(); }
            });
            HistoryRows.Children.Add(button);
        }
        historyVisibleCount = Math.Min(offset+count,history.Count);
        if(historyVisibleCount<history.Count)
        {
            var more=new Button {Content=Tr("MoreHistory"),HorizontalAlignment=HorizontalAlignment.Left};
            more.Click+=(_,_)=>{HistoryRows.Children.Remove(more);HistoryBatch(historyVisibleCount);};
            HistoryRows.Children.Add(more);
        }
    }
    private async void CompareClick(object s,RoutedEventArgs e) => await Safely(async () => {
        ShowPage("compare");int rev=pageRevision; CompareReference.Text=reference; CompareControls.Visibility=Visibility.Collapsed;
        await LoadHistory(); if(rev!=pageRevision) return;
        var matching=history.Where(x=>x.SentenceKey==SentenceKey(reference)).ToList();
        if (matching.Count<2)
        {
            UpdateCompareNotice();
            Message(() => Tr("CompareWelcome"));return;
        }
        CompareControls.Visibility=Visibility.Visible; CompareEmpty.Text="";
        settingComparison=true;
        try { CompareA.ItemsSource=matching; CompareB.ItemsSource=matching;CompareA.SelectedIndex=1;CompareB.SelectedIndex=0; }
        finally { settingComparison=false; }
        comparisonIndex=sectionIndex; compareWord=word; await RefreshCompare();
    });
    private async void CompareSelectionChanged(object s,SelectionChangedEventArgs e)
    {if(!settingComparison && page=="compare") await Safely(RefreshCompare);}
    private async Task RefreshCompare()
    {
        if (CompareA.SelectedItem is not AttemptSummary a || CompareB.SelectedItem is not AttemptSummary b) return;
        StopAudio();int rev=++compareRevision;
        APlay.IsEnabled=BPlay.IsEnabled=PlayPair.IsEnabled=false;
        var pair=await Task.WhenAll(engine.Get(a.Id),engine.Get(b.Id));
        if(page!="compare" || rev!=compareRevision) return;
        left=pair[0];right=pair[1];
        if(compareWord>=left.Words.Count) compareWord=-1;
        comparisonSections=left.Sections.Count>0 ? left.Sections : right.Sections;
        int selector=compareWord>=0 ? comparisonSections.FindIndex(u=>u.Contains(compareWord)) : comparisonIndex;
        comparisonIndex=comparisonSections.Count==0 ? 0 : Math.Clamp(selector,0,comparisonSections.Count-1);
        CompareNavigator.Visibility=comparisonSections.Count==0 ? Visibility.Collapsed : Visibility.Visible;
        if(comparisonSections.Count==0 && compareWord==CurrentSentence) compareWord=EntireRecording;
        UpdateCompareNotice();
        RenderCompare();Message(() => Tr("CompareReady"));
    }
    private void UpdateCompareNotice()
    {
        if (CompareControls.Visibility != Visibility.Visible)
        {
            var key = SentenceKey(reference);
            CompareEmpty.Text = Tr(history.Any(h=>h.SentenceKey==key) ? "CompareOne" : "CompareZero");
            return;
        }
        if (CompareA.SelectedItem is AttemptSummary a && CompareB.SelectedItem is AttemptSummary b)
            CompareEmpty.Text = a.Id==b.Id ? Tr("SameTake") : a.ReferenceAccent!=b.ReferenceAccent ? Tr("MixedReference") : "";
    }
    private void MoveCompareSection(int delta)
    {
        int next=comparisonIndex+delta;
        if(page!="compare" || next<0 || next>=comparisonSections.Count) return;
        StopAudio(); comparisonIndex=next; compareWord=CurrentSentence; RenderCompare();
    }
    private ReadingSection? CompareSection(Attempt a) => comparisonSections.ElementAtOrDefault(comparisonIndex) is ReadingSection chosen
        ? a.Sections.FirstOrDefault(u=>u.FirstWord==chosen.FirstWord && u.WordCount==chosen.WordCount) : null;
    private void RenderCompare()
    {
        if(left==null || right==null) return;
        CompareWords.Children.Clear();
        void Add(string title,int index)
        {
            var btn=TokenButton(title,index==compareWord,false,false);
            btn.Click+=(_,_)=>{StopAudio();compareWord=index;RenderCompare();};
            CompareWords.Children.Add(btn);
        }
        var chosen=comparisonSections.ElementAtOrDefault(comparisonIndex);
        if (chosen!=null) CompareNavigator.ShowPosition(comparisonIndex,comparisonSections.Count);
        CompareReference.Text=chosen?.Reference ?? left.Reference;
        Add(chosen==null ? Tr("WholeSentence") : Tr("WholePassage"),EntireRecording);
        if(chosen!=null) Add(Tr("ThisSentence"),CurrentSentence);
        foreach(var w in left.Words.Where(w=>chosen==null || chosen.Contains(w.Index))) Add(w.Text,w.Index);
        void Side(Attempt a,TextBlock title,TextBlock detail,Button play)
        {
            var w=a.Words.FirstOrDefault(x=>x.Index==compareWord);
            var section=compareWord==CurrentSentence ? CompareSection(a) : w!=null ? a.SectionFor(w.Index) : null;
            title.Text=compareWord==EntireRecording ? (chosen==null?Tr("WholeSentence"):Tr("WholePassage")) :
                       compareWord==CurrentSentence ? Tr("SentenceNumber",chosen!.Index+1) : w?.Text;
            string line=compareWord switch {
                EntireRecording => a.Reliable ? Tr("ScoreAndAccuracy",a.Score("total"),a.Score("accuracy")) : Reasons(a.Reasons),
                CurrentSentence => section==null ? Tr("NoCorrespondingSection") : section.Reliable ?
                    Tr("ComparedSection",section.Scores.GetValueOrDefault("total"),section.Start,section.End) : Reasons(section.Reasons),
                _ => Describe(a,w)};
            string transcript=section?.Transcript ?? (compareWord==CurrentSentence ? "" : a.Transcript);
            detail.Text=line+(transcript.Length>0 ? Environment.NewLine+Tr("TranscriptLine",transcript) : "");
            play.IsEnabled=compareWord==EntireRecording || (compareWord==CurrentSentence ? section?.CanPlay==true : w?.CanPlay==true);
        }
        Side(left,AWord,ADetail,APlay);Side(right,BWord,BDetail,BPlay);
        PlayPair.IsEnabled=APlay.IsEnabled && BPlay.IsEnabled && left.Id!=right.Id;
    }
    private Task PlaySide(Attempt a,string sideKey)
    {
        if(compareWord==CurrentSentence)
        {
            var section=CompareSection(a);
            return section?.CanPlay==true ? Play(StorePaths.Audio(a.Id),() => Tr(sideKey)+" · "+Tr("SentenceNumber",section.Index+1),section.Start,section.End) : Task.CompletedTask;
        }
        var w=a.Words.FirstOrDefault(x=>x.Index==compareWord);
        if(compareWord>=0 && w?.CanPlay!=true) return Task.CompletedTask;
        return Play(StorePaths.Audio(a.Id),() => Tr(sideKey)+" · "+(w?.Text??Tr(a.Sections.Count>0 ? "WholePassage" : "WholeSentence")),w?.Start,w?.End);
    }
    private async void APlayClick(object s,RoutedEventArgs e) => await Safely(async()=>{if(left!=null)await PlaySide(left,"TakeA");});
    private async void BPlayClick(object s,RoutedEventArgs e) => await Safely(async()=>{if(right!=null)await PlaySide(right,"TakeB");});
    private async void PairClick(object s,RoutedEventArgs e) => await Safely(async()=>{
        if(left==null || right==null) return;
        int revision=playbackRevision+1;
        await PlaySide(left,"TakeA");
        await Task.Delay(220);
        if(page=="compare" && revision==playbackRevision) await PlaySide(right,"TakeB");
    });
    private void WindowClosing(object? s, CancelEventArgs e)
    {
        if (phase==Phase.OpeningMic) {e.Cancel=true;return;}
        if ((phase==Phase.Recording || phase==Phase.Assessing) && MessageBox.Show(this,
            Tr("ExitConfirm"),Tr("AppName"),MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)
        {e.Cancel=true;return;}
        closing=true;timer.Stop();StopAudio();
        if(capture!=null)
        {
            try {capture.StopAsync().GetAwaiter().GetResult();} catch (Exception) { }
            capture.Dispose();
        }
        engine.Dispose();
    }
}
