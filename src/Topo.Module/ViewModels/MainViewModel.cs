using System.Collections.ObjectModel;
using System.Windows.Input;
using Topo.Module.Services;
using FlyBrain.BrainClient;
using Topo.Core;
using Topo.Training;

namespace Topo.Module.ViewModels;

public enum ApplicationMode { NeuralDecode, AutonomousSearch }

public sealed class TokenProgressItem(int index, string expected) : ObservableObject
{
    private string _predicted = "—", _state = "WAITING"; private double _confidence, _membrane;
    public int Index => index; public string Expected => expected;
    public string Predicted { get => _predicted; set => Set(ref _predicted, value); }
    public string State { get => _state; set => Set(ref _state, value); }
    public double Confidence { get => _confidence; set => Set(ref _confidence, value); }
    public double Membrane { get => _membrane; set => Set(ref _membrane, value); }
}

public sealed record SearchHistoryItem(int Iteration, string Proposed, string Decoded, double Reward, double AngularError, bool Accepted, int Radius, bool IsExploration);

public sealed class MainViewModel : ObservableObject
{
    private readonly IInteractiveDecodeService _decoder; private readonly IInteractiveSearchService _search; private CancellationTokenSource? _cancellation;
    private string _coordinateText = "N35 W54", _brainState = "OFFLINE", _brainDetail = "Not connected", _decoded = "—", _target = "N35 W54", _error = "", _log = "";
    private bool _isRunning; private int _simulationStep, _totalSpikes, _searchIteration, _searchRadius; private double _maximumMembrane, _activeFraction, _reward, _angularError, _minimumConfidence, _meanConfidence, _bestReward, _bestAngularError;
    private string _currentToken = "—", _safetyStatus = "—", _currentProposed = "—", _currentDecoded = "—", _bestDecoded = "—", _searchStatus = "IDLE";
    private ApplicationMode _applicationMode; private int _searchSeed = 20260924;

    public MainViewModel(IInteractiveDecodeService decoder, IInteractiveSearchService? search = null)
    {
        _decoder = decoder; _search = search ?? new AutonomousSearchService(decoder);
        DecodeCommand = new AsyncCommand(DecodeAsync, () => !_isRunning && ApplicationMode == ApplicationMode.NeuralDecode);
        CancelCommand = new AsyncCommand(CancelAsync, () => _isRunning && ApplicationMode == ApplicationMode.NeuralDecode);
        ReconnectCommand = new AsyncCommand(ConnectAsync, () => !_isRunning);
        StartSearchCommand = new AsyncCommand(StartSearchAsync, () => !_isRunning && ApplicationMode == ApplicationMode.AutonomousSearch);
        StopSearchCommand = new AsyncCommand(CancelAsync, () => _isRunning && ApplicationMode == ApplicationMode.AutonomousSearch);
        ResetSearchCommand = new AsyncCommand(ResetSearchAsync, () => !_isRunning && ApplicationMode == ApplicationMode.AutonomousSearch);
        NeuralDecodeModeCommand = new AsyncCommand(() => SetModeAsync(ApplicationMode.NeuralDecode), () => !_isRunning);
        AutonomousSearchModeCommand = new AsyncCommand(() => SetModeAsync(ApplicationMode.AutonomousSearch), () => !_isRunning);
    }

    public string Title => "TOPO — Fly Connectome Coordinate Sandbox";
    public string Mode => ApplicationMode == ApplicationMode.NeuralDecode ? "NEURAL DECODE MODE · decoded marker is a neural prediction, not autonomous navigation" : "AUTONOMOUS SEARCH MODE · external reward-guided policy; markers are decoded guesses, not biological locomotion";
    public ApplicationMode ApplicationMode { get => _applicationMode; private set { if (Set(ref _applicationMode, value)) { Raise(nameof(Mode)); RefreshCommands(); } } }
    public string CoordinateText { get => _coordinateText; set => Set(ref _coordinateText, value); } public string Target { get => _target; private set => Set(ref _target, value); } public string Decoded { get => _decoded; private set => Set(ref _decoded, value); }
    public string BrainState { get => _brainState; private set => Set(ref _brainState, value); } public string BrainDetail { get => _brainDetail; private set => Set(ref _brainDetail, value); } public string Error { get => _error; private set => Set(ref _error, value); } public string Log { get => _log; private set => Set(ref _log, value); }
    public bool IsRunning { get => _isRunning; private set { if (Set(ref _isRunning, value)) RefreshCommands(); } } public int SimulationStep { get => _simulationStep; private set => Set(ref _simulationStep, value); } public string CurrentToken { get => _currentToken; private set => Set(ref _currentToken, value); }
    public double MaximumMembrane { get => _maximumMembrane; set => Set(ref _maximumMembrane, value); } public int TotalSpikes { get => _totalSpikes; private set => Set(ref _totalSpikes, value); } public double ActiveFraction { get => _activeFraction; private set => Set(ref _activeFraction, value); } public string SafetyStatus { get => _safetyStatus; private set => Set(ref _safetyStatus, value); } public double Reward { get => _reward; set => Set(ref _reward, value); } public double AngularError { get => _angularError; private set => Set(ref _angularError, value); } public double MinimumConfidence { get => _minimumConfidence; private set => Set(ref _minimumConfidence, value); } public double MeanConfidence { get => _meanConfidence; private set => Set(ref _meanConfidence, value); }
    public int SearchSeed { get => _searchSeed; set => Set(ref _searchSeed, value); } public int SearchIteration { get => _searchIteration; private set => Set(ref _searchIteration, value); } public int SearchRadius { get => _searchRadius; private set => Set(ref _searchRadius, value); } public string CurrentProposed { get => _currentProposed; private set => Set(ref _currentProposed, value); } public string CurrentDecoded { get => _currentDecoded; private set => Set(ref _currentDecoded, value); } public string BestDecoded { get => _bestDecoded; private set => Set(ref _bestDecoded, value); } public double BestReward { get => _bestReward; private set => Set(ref _bestReward, value); } public double BestAngularError { get => _bestAngularError; private set => Set(ref _bestAngularError, value); } public string SearchStatus { get => _searchStatus; private set => Set(ref _searchStatus, value); }
    public ObservableCollection<TokenProgressItem> Tokens { get; } = []; public ObservableCollection<SearchHistoryItem> SearchHistory { get; } = []; public ObservableCollection<GeoCoordinate> SearchTrail { get; } = [];
    public ICommand DecodeCommand { get; } public ICommand CancelCommand { get; } public ICommand ReconnectCommand { get; } public ICommand StartSearchCommand { get; } public ICommand StopSearchCommand { get; } public ICommand ResetSearchCommand { get; } public ICommand NeuralDecodeModeCommand { get; } public ICommand AutonomousSearchModeCommand { get; }
    public async Task InitializeAsync() => await ConnectAsync(); public Task DecodeForTestAsync() => DecodeAsync(); public Task StartSearchForTestAsync() => StartSearchAsync(); public Task ReconnectForTestAsync() => ConnectAsync(); public Task ResetSearchForTestAsync() => ResetSearchAsync(); public Task SetSearchModeForTestAsync() => SetModeAsync(ApplicationMode.AutonomousSearch); public Task SetDecodeModeForTestAsync() => SetModeAsync(ApplicationMode.NeuralDecode);
    public bool IsBusy => _isRunning;
    public void CancelRunningOperation() => _cancellation?.Cancel();
    public void SetBrainOffline(string detail) { CancelRunningOperation(); BrainState = "OFFLINE"; BrainDetail = detail; AddLog(detail); }
    public void SelectMapCoordinate(double latitude, double longitude) { var coordinate = MapCoordinateConverter.ToTopoCoordinate(latitude, longitude); CoordinateText = coordinate.ToString(); Target = coordinate.ToString(); Error = ""; AddLog($"Map selected {latitude:F3}, {longitude:F3} → {coordinate}"); }

    private Task SetModeAsync(ApplicationMode mode) { ApplicationMode = mode; Error = ""; AddLog(mode == ApplicationMode.NeuralDecode ? "Neural Decode Mode selected" : "Autonomous Search Mode selected"); return Task.CompletedTask; }
    private async Task ConnectAsync() { Error = ""; BrainState = "CONNECTING"; try { var status = await _decoder.ConnectAsync(); BrainState = status.State; BrainDetail = $"{status.Detail} {status.NeuronCount?.ToString() ?? ""} neurons · v{status.ServiceVersion}"; AddLog($"Brain Service {status.State.ToLowerInvariant()}"); } catch (Exception ex) { BrainState = "OFFLINE"; BrainDetail = "Brain Service unavailable"; Error = UserMessage(ex); AddLog(Error); } }
    private async Task DecodeAsync()
    {
        if (!TryTarget(out var source)) return; ResetDecodePresentation(source); _cancellation = new CancellationTokenSource(); IsRunning = true; BrainState = "RUNNING"; AddLog($"Decoding {source}");
        try { var result = await _decoder.DecodeAsync(source, new Progress<CoordinateDecodeProgress>(UpdateNeuralProgress), _cancellation.Token); if (!result.Decoding.IsSuccess) { Error = result.Decoding.ErrorMessage ?? "Predicted token sequence is invalid."; return; } var prediction = result.Decoding.Coordinate!.Value; SetDecodedResult(source, prediction, result.Predictions); AddLog($"Decoded {Decoded} · reward {Reward:F3}"); }
        catch (OperationCanceledException) { AddLog("Decode cancelled"); }
        catch (Exception ex) { Error = UserMessage(ex); BrainState = "ERROR"; AddLog($"{ex.GetType().Name}: {ex.Message}"); }
        finally { EndOperation(); }
    }
    private async Task StartSearchAsync()
    {
        if (!TryTarget(out var target)) return; ResetSearchPresentation(target); _cancellation = new CancellationTokenSource(); IsRunning = true; BrainState = "RUNNING"; SearchStatus = "RUNNING"; AddLog($"Searching for {target} with seed {SearchSeed}");
        try
        {
            var result = await _search.RunAsync(target, new SearchSettings(SearchSeed), false, new Progress<SearchIteration>(UpdateSearchProgress), new Progress<CoordinateDecodeProgress>(UpdateNeuralProgress), _cancellation.Token);
            SearchStatus = result.Termination.ToString().ToUpperInvariant();
            if (result.BestDecodedCoordinate is { } best) { BestDecoded = best.ToString(); BestReward = result.BestReward ?? 0; BestAngularError = result.BestAngularErrorDegrees ?? 0; }
            if (result.Termination is SearchTerminationReason.EvaluatorFailure or SearchTerminationReason.DecodeFailure) Error = result.Trace.LastOrDefault()?.FailureMessage ?? "Search evaluation failed.";
            AddLog($"Search {SearchStatus} after {result.IterationsUsed} evaluations");
        }
        catch (OperationCanceledException) { SearchStatus = "CANCELLED"; AddLog("Search cancelled"); }
        catch (Exception ex) { SearchStatus = "ERROR"; Error = UserMessage(ex); BrainState = "ERROR"; AddLog($"{ex.GetType().Name}: {ex.Message}"); }
        finally { EndOperation(); }
    }
    private Task ResetSearchAsync() { ResetSearchPresentation(GeoCoordinate.TryParse(CoordinateText, out var target) ? target : null); AddLog("Search display reset"); return Task.CompletedTask; }
    private Task CancelAsync() { _cancellation?.Cancel(); return Task.CompletedTask; }
    private bool TryTarget(out GeoCoordinate coordinate) { if (!GeoCoordinate.TryParse(CoordinateText, out coordinate)) { Error = "Enter an integer coordinate such as N35 W54."; return false; } Target = coordinate.ToString(); Error = ""; return true; }
    private void ResetDecodePresentation(GeoCoordinate source) { Target = source.ToString(); Decoded = "—"; Tokens.Clear(); Reward = AngularError = MinimumConfidence = MeanConfidence = MaximumMembrane = 0; SimulationStep = 0; TotalSpikes = 0; ActiveFraction = 0; SafetyStatus = "—"; foreach (var token in source.ToTokens()) Tokens.Add(new TokenProgressItem(Tokens.Count, Display(token))); }
    private void ResetSearchPresentation(GeoCoordinate? target) { if (target is { } value) Target = value.ToString(); SearchHistory.Clear(); SearchTrail.Clear(); Raise(nameof(SearchTrail)); SearchIteration = SearchRadius = 0; CurrentProposed = CurrentDecoded = BestDecoded = "—"; BestReward = BestAngularError = Reward = AngularError = 0; SearchStatus = "IDLE"; Decoded = "—"; Tokens.Clear(); }
    private void SetDecodedResult(GeoCoordinate source, GeoCoordinate prediction, IReadOnlyList<CoordinateTokenPrediction> predictions) { var reward = RewardCalculator.Calculate(source, prediction); Decoded = prediction.ToString(); AngularError = reward.AngularDistanceDegrees; Reward = reward.Reward; MinimumConfidence = predictions.Min(item => item.ConfidenceScore); MeanConfidence = predictions.Average(item => item.ConfidenceScore); }
    private void UpdateNeuralProgress(CoordinateDecodeProgress progress)
    {
        if (ApplicationMode == ApplicationMode.AutonomousSearch && progress.SequenceIndex == 0 && progress.Stage == CoordinateDecodeProgressStage.TokenStarted) Tokens.Clear(); while (Tokens.Count <= progress.SequenceIndex) Tokens.Add(new TokenProgressItem(Tokens.Count, Display(progress.ExpectedToken)));
        var item = Tokens[progress.SequenceIndex]; CurrentToken = Display(progress.ExpectedToken); SimulationStep = progress.SimulationStep; MaximumMembrane = progress.MaximumMembraneDeltaMillivolts; TotalSpikes = progress.TotalSpikes; ActiveFraction = progress.ActiveFraction; SafetyStatus = progress.SafetyStatus;
        if (progress.Stage == CoordinateDecodeProgressStage.TokenStarted) item.State = "RUNNING"; if (progress.Stage == CoordinateDecodeProgressStage.NeuralObservationCaptured) item.State = "OBSERVED"; if (progress.Stage == CoordinateDecodeProgressStage.TokenPredicted) { item.Predicted = Display(progress.PredictedToken!.Value); item.Confidence = progress.ConfidenceScore!.Value; item.Membrane = progress.MaximumMembraneDeltaMillivolts; item.State = "COMPLETE"; AddLog($"{item.Expected} → {item.Predicted} ({item.Confidence:F3})"); }
    }
    private void UpdateSearchProgress(SearchIteration item)
    {
        SearchIteration = item.Iteration; SearchRadius = item.RadiusDegrees; CurrentProposed = item.ProposedCoordinate.ToString(); CurrentDecoded = item.DecodedCoordinate?.ToString() ?? "—"; Decoded = CurrentDecoded; Reward = item.Reward ?? 0; AngularError = item.AngularErrorDegrees ?? 0;
        if (item.BestDecodedCoordinate is { } best) { BestDecoded = best.ToString(); BestReward = item.BestReward ?? 0; BestAngularError = item.BestAngularErrorDegrees ?? 0; }
        if (item.DecodedCoordinate is { } decoded) { SearchHistory.Add(new(item.Iteration, item.ProposedCoordinate.ToString(), decoded.ToString(), item.Reward ?? 0, item.AngularErrorDegrees ?? 0, item.Accepted, item.RadiusDegrees, item.IsExploration)); SearchTrail.Add(decoded); while (SearchHistory.Count > 100) SearchHistory.RemoveAt(0); while (SearchTrail.Count > 100) SearchTrail.RemoveAt(0); Raise(nameof(SearchTrail)); }
    }
    private void EndOperation() { IsRunning = false; if (BrainState == "RUNNING") BrainState = "READY"; _cancellation?.Dispose(); _cancellation = null; }
    private void RefreshCommands() { foreach (var command in new[] { DecodeCommand, CancelCommand, ReconnectCommand, StartSearchCommand, StopSearchCommand, ResetSearchCommand, NeuralDecodeModeCommand, AutonomousSearchModeCommand }) ((AsyncCommand)command).RaiseCanExecuteChanged(); }
    private void AddLog(string line) => Log = $"{DateTime.Now:HH:mm:ss} {line}\n" + Log;
    private static string Display(GeoCoordinateToken token) => token switch { GeoCoordinateToken.North => "N", GeoCoordinateToken.South => "S", GeoCoordinateToken.East => "E", GeoCoordinateToken.West => "W", GeoCoordinateToken.Separator => "|", _ => ((int)token - (int)GeoCoordinateToken.Digit0).ToString() };
    private static string UserMessage(Exception exception) => exception switch { BrainServiceException { Code: "GRAPH_NOT_READY" } => "Brain graph is not ready. Reconnect after graph initialization.", BrainServiceException { Code: "SESSION_BUSY" } => "Brain session is busy. Wait for the current operation or retry.", BrainServiceException { Code: "UNKNOWN_ROOT_ID" } => "A configured neural root is unavailable. Check the frozen channel configuration.", BrainServiceException { Code: var code } => $"Brain Service error: {code}. See the log for details.", InvalidOperationException => exception.Message, _ => "Brain Service or frozen-model operation failed. See log for details." };
}
